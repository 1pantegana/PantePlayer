using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LettoreVideo.Engine.Interop;
using LettoreVideo.Engine.Playback;

namespace LettoreVideo.Engine.Media;

/// <summary>
/// A decoded picture still owned by FFmpeg: a reference to the decoder's frame
/// (an AVVkFrame on the GPU).
/// The renderer copies it into images of its own; until then the decoder's
/// surface stays in use, which is why the decoder asks for spare surfaces.
/// </summary>
internal sealed unsafe class DecodedPicture : IFrameImage
{
    private AVFrame* _frame;

    public DecodedPicture(AVFrame* source)
    {
        _frame = AvUtil.av_frame_alloc();
        if (_frame == null || AvUtil.av_frame_ref(_frame, source) < 0)
        {
            Release();
            throw new OutOfMemoryException("riferimento al fotogramma non riuscito");
        }
    }

    public AVFrame* Frame => _frame;

    public void Release()
    {
        AVFrame* f = _frame;
        _frame = null;
        if (f != null) AvUtil.av_frame_free(&f);
    }
}

/// <summary>
/// Decodes the video stream with Vulkan Video on the device FFmpeg created.
/// There is no software path: a codec or profile the GPU cannot decode is refused.
/// </summary>
internal sealed unsafe class VideoDecoder : IDisposable
{
    /// <summary>Frames the queue can hold while still referencing decoder surfaces.</summary>
    public const int ExtraSurfaces = 12;

    private const int H264ProfileHigh10 = 110;

    private static readonly int PixFmtNv12 = AvUtil.av_get_pix_fmt("nv12");
    private static readonly int PixFmtP010 = AvUtil.av_get_pix_fmt("p010le");

    private AVCodecContext* _ctx;
    private AVFrame* _frame;

    // Set by GetFormat when FFmpeg could not set up Vulkan for the stream. The
    // callback runs synchronously inside send/receive on the decode thread
    // (frame threading is off), so a thread-static is enough to carry it back.
    [ThreadStatic] private static bool t_vulkanRefused;

    private readonly double _timeBase;
    private readonly double _nominalFrameDuration;
    private double _nextPts = double.NaN;

    public string CodecName { get; }

    /// <summary>Frames per second declared by the container, or 0 when it does not say.</summary>
    public double NominalFps { get; }

    public VideoDecoder(AVStream* stream, VulkanHwDevice hwDevice)
    {
        AVCodecParameters* par = stream->codecpar;
        string codecId = Marshal.PtrToStringUTF8((IntPtr)AvCodec.avcodec_get_name(par->codec_id)) ?? "?";

        // libdav1d is registered ahead of FFmpeg's own AV1 decoder but is software
        // only: ask for the native one by name so AV1 decodes on the GPU.
        AVCodec* codec = codecId == "av1" ? AvCodec.avcodec_find_decoder_by_name("av1") : null;
        if (codec == null) codec = AvCodec.avcodec_find_decoder(par->codec_id);
        if (codec == null)
            throw new NotSupportedException($"nessun decoder per il codec {codecId}");

        CodecName = Marshal.PtrToStringUTF8((IntPtr)codec->name) ?? "?";

        if (!SupportsVulkan(codec))
            throw new NotSupportedException($"il codec {codecId} non ha decodifica Vulkan");

        // Vulkan Video has no 10-bit H.264 profile.
        if (codecId == "h264" && par->profile == H264ProfileHigh10)
            throw new NotSupportedException("H.264 High 10 non ha decodifica Vulkan");

        _ctx = AvCodec.avcodec_alloc_context3(codec);
        FFmpegLoader.Check(AvCodec.avcodec_parameters_to_context(_ctx, par), "parametri del decoder");
        _ctx->pkt_timebase = stream->time_base;
        _ctx->hw_device_ctx = AvUtil.av_buffer_ref(hwDevice.Ref);
        _ctx->get_format = &GetFormat;
        _ctx->extra_hw_frames = ExtraSurfaces;
        _ctx->thread_count = 1;

        FFmpegLoader.Check(AvCodec.avcodec_open2(_ctx, codec, null), "apertura del decoder");
        _frame = AvUtil.av_frame_alloc();

        _timeBase = stream->time_base.ToDouble();
        AVRational rate = stream->avg_frame_rate.num > 0 ? stream->avg_frame_rate : stream->r_frame_rate;
        NominalFps = rate.num > 0 && rate.den > 0 ? rate.ToDouble() : 0;
        _nominalFrameDuration = NominalFps is > 1 and < 500 ? 1.0 / NominalFps : 1.0 / 25;
    }

    private static bool SupportsVulkan(AVCodec* codec)
    {
        for (int i = 0; ; i++)
        {
            AVCodecHWConfig* cfg = AvCodec.avcodec_get_hw_config(codec, i);
            if (cfg == null) return false;
            if ((cfg->methods & Av.CodecHwConfigMethodHwDeviceCtx) != 0
                && cfg->device_type == VulkanHwDevice.DeviceType)
                return true;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int GetFormat(AVCodecContext* ctx, int* formats)
    {
        for (int* p = formats; *p != Av.PixFmtNone; p++)
            if (*p == VulkanHwDevice.PixelFormat)
                return *p;

        // Vulkan setup failed for this stream (FFmpeg dropped it from the list):
        // no software formats, the decoder errors out and Decode reports it.
        t_vulkanRefused = true;
        return Av.PixFmtNone;
    }

    /// <summary>Forgets every buffered picture (seek).</summary>
    public void Flush()
    {
        AvCodec.avcodec_flush_buffers(_ctx);
        _nextPts = double.NaN;
    }

    /// <summary>
    /// Feeds one packet (null = drain at end of stream) and hands every finished
    /// picture to <paramref name="sink"/>.
    /// </summary>
    public void Decode(AVPacket* packet, int serial, Action<VideoFrame> sink)
    {
        t_vulkanRefused = false;
        while (true)
        {
            int sent = AvCodec.avcodec_send_packet(_ctx, packet);
            bool again = sent == Av.ErrorEagain;

            ReceiveAll(serial, sink);

            if (t_vulkanRefused)
                throw new NotSupportedException($"la GPU non decodifica questo stream {CodecName} con Vulkan");
            if (!again) break;   // a broken packet (sent < 0) is just skipped
        }
    }

    private void ReceiveAll(int serial, Action<VideoFrame> sink)
    {
        while (true)
        {
            int r = AvCodec.avcodec_receive_frame(_ctx, _frame);
            if (r < 0) return;   // EAGAIN, EOF or a decode error: nothing more now

            try
            {
                if (Wrap(serial) is { } frame)
                    sink(frame);
            }
            finally
            {
                AvUtil.av_frame_unref(_frame);
            }
        }
    }

    private VideoFrame? Wrap(int serial)
    {
        AVFrame* f = _frame;
        int width = f->width & ~1, height = f->height & ~1;
        if (width <= 0 || height <= 0) return null;

        long ts = f->best_effort_timestamp != Av.NoPtsValue ? f->best_effort_timestamp : f->pts;
        double pts = ts != Av.NoPtsValue ? ts * _timeBase : _nextPts;
        if (double.IsNaN(pts)) pts = 0;

        double duration = f->duration > 0 ? f->duration * _timeBase : _nominalFrameDuration;
        _nextPts = pts + duration;

        int pixelFormat = f->format;
        if (pixelFormat == VulkanHwDevice.PixelFormat && f->hw_frames_ctx != null)
            pixelFormat = ((AVHWFramesContext*)f->hw_frames_ctx->data)->sw_format;

        PlaneLayout layout = pixelFormat == PixFmtNv12 ? PlaneLayout.Nv12
            : pixelFormat == PixFmtP010 ? PlaneLayout.P010
            : PlaneLayout.Other;

        var format = new FrameFormat(width, height, layout, ColorInfo.FromFrame(f),
            f->sample_aspect_ratio.num, f->sample_aspect_ratio.den);
        return new VideoFrame(new DecodedPicture(f), format, pts, duration, serial, generated: false);
    }

    public void Dispose()
    {
        if (_frame != null) { AVFrame* fr = _frame; AvUtil.av_frame_free(&fr); _frame = null; }
        if (_ctx != null) { AVCodecContext* c = _ctx; AvCodec.avcodec_free_context(&c); _ctx = null; }
    }
}
