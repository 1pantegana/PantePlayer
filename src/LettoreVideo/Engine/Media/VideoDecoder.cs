using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using LettoreVideo.Engine.Gpu;
using LettoreVideo.Engine.Playback;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace LettoreVideo.Engine.Media;

/// <summary>
/// Decodes the video stream with D3D11VA on the shared device and copies every
/// picture into a pooled texture of our own: the decoder's surfaces are recycled
/// as soon as the frame is unreferenced, while ours live as long as the
/// presenter (or the interpolator) needs them. Codecs or profiles NVDEC cannot do
/// fall back to software decoding with an NV12 upload.
/// </summary>
internal sealed unsafe class VideoDecoder : IDisposable
{
    private const int H264ProfileHigh10 = 110;

    private readonly GpuDevice _gpu;
    private readonly TexturePool _pool;
    private readonly AVStream* _stream;
    private AVCodecContext* _ctx;
    private AVFrame* _frame;
    private SwsContext* _sws;
    private ID3D11Texture2D? _staging;
    private (int W, int H) _stagingSize;

    private readonly double _timeBase;
    private readonly double _nominalFrameDuration;
    private double _nextPts = double.NaN;

    public bool Hardware { get; }
    public string CodecName { get; }

    /// <summary>Frames per second declared by the container, or 0 when it does not say.</summary>
    public double NominalFps { get; }

    public VideoDecoder(AVStream* stream, AVBufferRef* hwDevice, GpuDevice gpu, TexturePool pool)
    {
        _gpu = gpu;
        _pool = pool;
        _stream = stream;
        AVCodecParameters* par = stream->codecpar;

        // libdav1d is registered ahead of FFmpeg's own AV1 decoder but cannot use
        // D3D11VA: ask for the native one by name so AV1 decodes on NVDEC.
        AVCodec* codec = par->codec_id == AVCodecID.AV_CODEC_ID_AV1
            ? ffmpeg.avcodec_find_decoder_by_name("av1")
            : null;
        if (codec == null) codec = ffmpeg.avcodec_find_decoder(par->codec_id);
        if (codec == null)
            throw new NotSupportedException($"nessun decoder per il codec {ffmpeg.avcodec_get_name(par->codec_id)}");

        CodecName = Marshal.PtrToStringUTF8((IntPtr)codec->name) ?? "?";

        _ctx = ffmpeg.avcodec_alloc_context3(codec);
        FFmpegLoader.Check(ffmpeg.avcodec_parameters_to_context(_ctx, par), "parametri del decoder");
        _ctx->pkt_timebase = stream->time_base;

        bool hwProfile = !(par->codec_id == AVCodecID.AV_CODEC_ID_H264 && par->profile == H264ProfileHigh10);
        Hardware = hwDevice != null && hwProfile && SupportsD3D11(codec);

        if (Hardware)
        {
            _ctx->hw_device_ctx = ffmpeg.av_buffer_ref(hwDevice);
            _ctx->get_format = new AVCodecContext_get_format_func
            {
                Pointer = (IntPtr)(delegate* unmanaged[Cdecl]<AVCodecContext*, AVPixelFormat*, AVPixelFormat>)&GetFormat,
            };
            // Frames are copied out right away, a few spare surfaces are plenty.
            _ctx->extra_hw_frames = 4;
            _ctx->thread_count = 1;
        }
        else
        {
            _ctx->thread_count = 0;   // all cores
        }

        FFmpegLoader.Check(ffmpeg.avcodec_open2(_ctx, codec, null), "apertura del decoder");
        _frame = ffmpeg.av_frame_alloc();

        _timeBase = ffmpeg.av_q2d(stream->time_base);
        AVRational rate = stream->avg_frame_rate.num > 0 ? stream->avg_frame_rate : stream->r_frame_rate;
        NominalFps = rate.num > 0 && rate.den > 0 ? ffmpeg.av_q2d(rate) : 0;
        _nominalFrameDuration = NominalFps is > 1 and < 500 ? 1.0 / NominalFps : 1.0 / 25;
    }

    private static bool SupportsD3D11(AVCodec* codec)
    {
        for (int i = 0; ; i++)
        {
            AVCodecHWConfig* cfg = ffmpeg.avcodec_get_hw_config(codec, i);
            if (cfg == null) return false;
            if ((cfg->methods & ffmpeg.AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) != 0
                && cfg->device_type == AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA)
                return true;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static AVPixelFormat GetFormat(AVCodecContext* ctx, AVPixelFormat* formats)
    {
        for (AVPixelFormat* p = formats; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
            if (*p == AVPixelFormat.AV_PIX_FMT_D3D11)
                return *p;

        // Hardware setup failed for this stream: FFmpeg offers the software formats.
        return ffmpeg.avcodec_default_get_format(ctx, formats);
    }

    /// <summary>Forgets every buffered picture (seek).</summary>
    public void Flush()
    {
        ffmpeg.avcodec_flush_buffers(_ctx);
        _nextPts = double.NaN;
    }

    /// <summary>
    /// Feeds one packet (null = drain at end of stream) and hands every finished
    /// picture to <paramref name="sink"/>.
    /// </summary>
    public void Decode(AVPacket* packet, int serial, Action<VideoFrame> sink)
    {
        while (true)
        {
            int sent = ffmpeg.avcodec_send_packet(_ctx, packet);
            bool again = sent == ffmpeg.AVERROR(ffmpeg.EAGAIN);

            ReceiveAll(serial, sink);

            if (!again) break;   // a broken packet (sent < 0) is just skipped
        }
    }

    private void ReceiveAll(int serial, Action<VideoFrame> sink)
    {
        while (true)
        {
            int r = ffmpeg.avcodec_receive_frame(_ctx, _frame);
            if (r < 0) return;   // EAGAIN, EOF or a decode error: nothing more now

            try
            {
                if (Convert(serial) is { } frame)
                    sink(frame);
            }
            finally
            {
                ffmpeg.av_frame_unref(_frame);
            }
        }
    }

    private VideoFrame? Convert(int serial)
    {
        AVFrame* f = _frame;
        int width = f->width & ~1, height = f->height & ~1;
        if (width <= 0 || height <= 0) return null;

        long ts = f->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE ? f->best_effort_timestamp : f->pts;
        double pts = ts != ffmpeg.AV_NOPTS_VALUE ? ts * _timeBase : _nextPts;
        if (double.IsNaN(pts)) pts = 0;

        double duration = f->duration > 0 ? f->duration * _timeBase : _nominalFrameDuration;
        _nextPts = pts + duration;

        bool hwFrame = f->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11;
        Format pixelFormat = hwFrame ? HwSurfaceFormat(f) : Format.NV12;
        if (pixelFormat == Format.Unknown) return null;

        PooledTexture target = _pool.Rent(width, height, pixelFormat);
        try
        {
            if (hwFrame) CopyHardware(f, target.Texture, width, height);
            else UploadSoftware(f, target.Texture, width, height);
        }
        catch
        {
            target.Return();
            throw;
        }

        var format = new FrameFormat(width, height, pixelFormat, ColorSpaces.FromFrame(f),
            f->sample_aspect_ratio.num, f->sample_aspect_ratio.den);
        return new VideoFrame(target, format, pts, duration, serial, generated: false);
    }

    private static Format HwSurfaceFormat(AVFrame* f)
    {
        if (f->hw_frames_ctx == null) return Format.Unknown;
        var frames = (AVHWFramesContext*)f->hw_frames_ctx->data;
        return frames->sw_format switch
        {
            AVPixelFormat.AV_PIX_FMT_NV12 => Format.NV12,
            AVPixelFormat.AV_PIX_FMT_P010LE => Format.P010,
            _ => Format.Unknown,
        };
    }

    private void CopyHardware(AVFrame* f, ID3D11Texture2D target, int width, int height)
    {
        IntPtr srcPtr = (IntPtr)f->data[0];
        uint srcIndex = (uint)(nint)f->data[1];

        // Borrowed from FFmpeg: take our own reference for the wrapper's lifetime.
        Marshal.AddRef(srcPtr);
        using var source = new ID3D11Texture2D(srcPtr);
        _gpu.Context.CopySubresourceRegion(target, 0, 0, 0, 0, source, srcIndex,
            new Box(0, 0, 0, width, height, 1));
    }

    private void UploadSoftware(AVFrame* f, ID3D11Texture2D target, int width, int height)
    {
        if (_staging is null || _stagingSize != (width, height))
        {
            _staging?.Dispose();
            _staging = _gpu.Device.CreateTexture2D(new Texture2DDescription(Format.NV12, (uint)width, (uint)height,
                1, 1, BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Write));
            _stagingSize = (width, height);
            ffmpeg.sws_freeContext(_sws);
            _sws = null;
        }

        _sws = ffmpeg.sws_getCachedContext(_sws, f->width, f->height, (AVPixelFormat)f->format,
            width, height, AVPixelFormat.AV_PIX_FMT_NV12, (int)SwsFlags.SWS_BILINEAR, null, null, null);
        if (_sws == null) throw new InvalidOperationException("conversione in NV12 non disponibile");

        MappedSubresource map = _gpu.Context.Map(_staging, 0, MapMode.Write);
        try
        {
            var dst = new byte_ptrArray4();
            var dstStride = new int_array4();
            dst[0] = (byte*)map.DataPointer;
            dst[1] = (byte*)map.DataPointer + map.RowPitch * height;
            dstStride[0] = dstStride[1] = (int)map.RowPitch;
            ffmpeg.sws_scale(_sws, f->data, f->linesize, 0, f->height, dst, dstStride);
        }
        finally
        {
            _gpu.Context.Unmap(_staging, 0);
        }

        _gpu.Context.CopyResource(target, _staging);
    }

    public void Dispose()
    {
        _staging?.Dispose();
        _staging = null;
        if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
        if (_frame != null) { AVFrame* fr = _frame; ffmpeg.av_frame_free(&fr); _frame = null; }
        if (_ctx != null) { AVCodecContext* c = _ctx; ffmpeg.avcodec_free_context(&c); _ctx = null; }
    }
}

/// <summary>Maps FFmpeg's colour description of a frame to a DXGI colour space.</summary>
internal static unsafe class ColorSpaces
{
    public static ColorSpaceType FromFrame(AVFrame* f)
    {
        bool full = f->color_range == AVColorRange.AVCOL_RANGE_JPEG;

        bool bt2020 = f->colorspace is AVColorSpace.AVCOL_SPC_BT2020_NCL or AVColorSpace.AVCOL_SPC_BT2020_CL
                      || f->color_primaries == AVColorPrimaries.AVCOL_PRI_BT2020;
        if (bt2020)
        {
            return f->color_trc switch
            {
                AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084 => ColorSpaceType.YcbcrStudioG2084LeftP2020,
                AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67 => ColorSpaceType.YcbcrStudioGhlgTopLeftP2020,
                _ => full ? ColorSpaceType.YcbcrFullG22LeftP2020 : ColorSpaceType.YcbcrStudioG22LeftP2020,
            };
        }

        bool bt601 = f->colorspace switch
        {
            AVColorSpace.AVCOL_SPC_BT470BG or AVColorSpace.AVCOL_SPC_SMPTE170M => true,
            AVColorSpace.AVCOL_SPC_BT709 => false,
            // Untagged: the usual convention, SD is 601 and HD is 709.
            _ => f->height < 720,
        };

        return (bt601, full) switch
        {
            (true, true) => ColorSpaceType.YcbcrFullG22LeftP601,
            (true, false) => ColorSpaceType.YcbcrStudioG22LeftP601,
            (false, true) => ColorSpaceType.YcbcrFullG22LeftP709,
            (false, false) => ColorSpaceType.YcbcrStudioG22LeftP709,
        };
    }
}
