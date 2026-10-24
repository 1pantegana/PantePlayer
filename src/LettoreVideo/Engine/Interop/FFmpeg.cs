using System.Runtime.InteropServices;

namespace LettoreVideo.Engine.Interop;

// Hand-written FFmpeg 9.0 bindings (avutil 61, avcodec 63, avformat 63).
//
// Structs only declare the fields the engine touches, at their x64 offsets
// (see private/_dev/docs/fork-nvfruc-engine-handover.md). They are never
// allocated or copied by value on our side — FFmpeg owns them and we only read
// and write through pointers — so the declared size does not matter. Updating
// FFmpeg to a new major version means re-checking every offset here.

[StructLayout(LayoutKind.Sequential)]
internal struct AVRational
{
    public int num, den;
    public readonly double ToDouble() => den == 0 ? 0 : (double)num / den;
}

[StructLayout(LayoutKind.Explicit)]
internal unsafe struct AVFormatContext
{
    [FieldOffset(44)] public uint nb_streams;
    [FieldOffset(48)] public AVStream** streams;
    [FieldOffset(96)] public long start_time;
    [FieldOffset(104)] public long duration;
}

[StructLayout(LayoutKind.Explicit)]
internal unsafe struct AVStream
{
    [FieldOffset(8)] public int index;
    [FieldOffset(16)] public AVCodecParameters* codecpar;
    [FieldOffset(32)] public AVRational time_base;
    [FieldOffset(40)] public long start_time;
    [FieldOffset(48)] public long duration;
    [FieldOffset(64)] public int disposition;
    [FieldOffset(68)] public int discard;
    [FieldOffset(72)] public AVRational sample_aspect_ratio;
    [FieldOffset(88)] public AVRational avg_frame_rate;
    [FieldOffset(204)] public AVRational r_frame_rate;
}

[StructLayout(LayoutKind.Explicit)]
internal struct AVCodecParameters
{
    [FieldOffset(0)] public int codec_type;
    [FieldOffset(4)] public int codec_id;
    [FieldOffset(44)] public int format;
    [FieldOffset(64)] public int profile;
    [FieldOffset(72)] public int width;
    [FieldOffset(76)] public int height;
    [FieldOffset(80)] public AVRational sample_aspect_ratio;
    [FieldOffset(100)] public int color_range;
    [FieldOffset(152)] public int sample_rate;
}

[StructLayout(LayoutKind.Explicit)]
internal unsafe struct AVPacket
{
    [FieldOffset(8)] public long pts;
    [FieldOffset(16)] public long dts;
    [FieldOffset(24)] public byte* data;
    [FieldOffset(32)] public int size;
    [FieldOffset(36)] public int stream_index;
    [FieldOffset(64)] public long duration;
}

[StructLayout(LayoutKind.Explicit)]
internal unsafe struct AVFrame
{
    /// <summary>8 plane pointers; for hardware frames data[0] is the API's frame (AVVkFrame*).</summary>
    [FieldOffset(0)] public fixed ulong data[8];
    [FieldOffset(64)] public fixed int linesize[8];
    [FieldOffset(104)] public int width;
    [FieldOffset(108)] public int height;
    [FieldOffset(116)] public int format;
    [FieldOffset(124)] public AVRational sample_aspect_ratio;
    [FieldOffset(136)] public long pts;
    [FieldOffset(280)] public int color_range;
    [FieldOffset(284)] public int color_primaries;
    [FieldOffset(288)] public int color_trc;
    [FieldOffset(292)] public int colorspace;
    [FieldOffset(304)] public long best_effort_timestamp;
    [FieldOffset(328)] public AVBufferRef* hw_frames_ctx;
    [FieldOffset(408)] public long duration;
}

[StructLayout(LayoutKind.Explicit)]
internal unsafe struct AVCodecContext
{
    [FieldOffset(12)] public int codec_type;
    [FieldOffset(48)] public void* opaque;
    [FieldOffset(92)] public AVRational pkt_timebase;
    [FieldOffset(112)] public int width;
    [FieldOffset(136)] public int pix_fmt;
    [FieldOffset(192)] public delegate* unmanaged[Cdecl]<AVCodecContext*, int*, int> get_format;
    [FieldOffset(552)] public AVBufferRef* hw_frames_ctx;
    [FieldOffset(560)] public AVBufferRef* hw_device_ctx;
    [FieldOffset(572)] public int extra_hw_frames;
    [FieldOffset(656)] public int thread_count;
}

[StructLayout(LayoutKind.Explicit)]
internal unsafe struct AVBufferRef
{
    [FieldOffset(8)] public byte* data;
}

[StructLayout(LayoutKind.Explicit)]
internal unsafe struct AVHWDeviceContext
{
    [FieldOffset(16)] public void* hwctx;
}

[StructLayout(LayoutKind.Explicit)]
internal unsafe struct AVHWFramesContext
{
    [FieldOffset(24)] public void* hwctx;
    [FieldOffset(60)] public int format;
    [FieldOffset(64)] public int sw_format;
    [FieldOffset(68)] public int width;
}

[StructLayout(LayoutKind.Explicit)]
internal unsafe struct AVCodec
{
    [FieldOffset(0)] public byte* name;
    [FieldOffset(16)] public int type;
}

[StructLayout(LayoutKind.Explicit)]
internal struct AVCodecHWConfig
{
    [FieldOffset(0)] public int pix_fmt;
    [FieldOffset(4)] public int methods;
    [FieldOffset(8)] public int device_type;
}

/// <summary>Constants from the FFmpeg headers that are part of the stable ABI.</summary>
internal static class Av
{
    public const long NoPtsValue = long.MinValue;
    public const int TimeBase = 1_000_000;

    public const int MediaTypeVideo = 0;
    public const int MediaTypeAudio = 1;

    public const int DiscardAll = 48;
    public const int DispositionAttachedPic = 1 << 10;
    public const int SeekFlagBackward = 1;
    public const int CodecHwConfigMethodHwDeviceCtx = 0x01;

    public const int PixFmtNone = -1;
    public const int ColorRangeJpeg = 2;
    public const int LogError = 16;

    /// <summary>AVERROR(EAGAIN): EAGAIN is 11 in the MinGW CRT FFmpeg is built with.</summary>
    public const int ErrorEagain = -11;

    /// <summary>AVERROR_EOF = -MKTAG('E','O','F',' ').</summary>
    public const int ErrorEof = -('E' | ('O' << 8) | ('F' << 16) | (' ' << 24));
}

internal static unsafe partial class AvUtil
{
    private const string Lib = "avutil-61";

    [LibraryImport(Lib)] public static partial void av_log_set_level(int level);
    [LibraryImport(Lib)] public static partial byte* av_version_info();
    [LibraryImport(Lib)] public static partial int av_strerror(int errnum, byte* buf, nuint size);

    [LibraryImport(Lib)] public static partial AVBufferRef* av_buffer_ref(AVBufferRef* buf);
    [LibraryImport(Lib)] public static partial void av_buffer_unref(AVBufferRef** buf);

    [LibraryImport(Lib)] public static partial AVFrame* av_frame_alloc();
    [LibraryImport(Lib)] public static partial void av_frame_free(AVFrame** frame);
    [LibraryImport(Lib)] public static partial void av_frame_unref(AVFrame* frame);
    [LibraryImport(Lib)] public static partial int av_frame_ref(AVFrame* dst, AVFrame* src);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int av_dict_set(void** dict, string key, string value, int flags);
    [LibraryImport(Lib)] public static partial void av_dict_free(void** dict);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int av_get_pix_fmt(string name);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int av_hwdevice_find_type_by_name(string name);

    [LibraryImport(Lib)]
    public static partial int av_hwdevice_ctx_create(AVBufferRef** deviceCtx, int type, byte* device, void* opts, int flags);
}

internal static unsafe partial class AvCodec
{
    private const string Lib = "avcodec-63";

    [LibraryImport(Lib)] public static partial AVCodec* avcodec_find_decoder(int id);
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial AVCodec* avcodec_find_decoder_by_name(string name);
    [LibraryImport(Lib)] public static partial byte* avcodec_get_name(int id);
    [LibraryImport(Lib)] public static partial AVCodecHWConfig* avcodec_get_hw_config(AVCodec* codec, int index);

    [LibraryImport(Lib)] public static partial AVCodecContext* avcodec_alloc_context3(AVCodec* codec);
    [LibraryImport(Lib)] public static partial void avcodec_free_context(AVCodecContext** ctx);
    [LibraryImport(Lib)] public static partial int avcodec_parameters_to_context(AVCodecContext* ctx, AVCodecParameters* par);
    [LibraryImport(Lib)] public static partial int avcodec_open2(AVCodecContext* ctx, AVCodec* codec, void** options);
    [LibraryImport(Lib)] public static partial int avcodec_send_packet(AVCodecContext* ctx, AVPacket* packet);
    [LibraryImport(Lib)] public static partial int avcodec_receive_frame(AVCodecContext* ctx, AVFrame* frame);
    [LibraryImport(Lib)] public static partial void avcodec_flush_buffers(AVCodecContext* ctx);
    [LibraryImport(Lib)] public static partial int avcodec_default_get_format(AVCodecContext* ctx, int* formats);

    [LibraryImport(Lib)] public static partial AVPacket* av_packet_alloc();
    [LibraryImport(Lib)] public static partial void av_packet_free(AVPacket** packet);
}

internal static unsafe partial class AvFormat
{
    private const string Lib = "avformat-63";

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int avformat_open_input(AVFormatContext** ctx, string url, void* fmt, void** options);
    [LibraryImport(Lib)] public static partial int avformat_find_stream_info(AVFormatContext* ctx, void** options);
    [LibraryImport(Lib)] public static partial void avformat_close_input(AVFormatContext** ctx);
    [LibraryImport(Lib)]
    public static partial int av_find_best_stream(AVFormatContext* ctx, int type, int wanted, int related, AVCodec** decoder, int flags);
    [LibraryImport(Lib)] public static partial int av_read_frame(AVFormatContext* ctx, AVPacket* packet);
    [LibraryImport(Lib)]
    public static partial int avformat_seek_file(AVFormatContext* ctx, int stream, long minTs, long ts, long maxTs, int flags);
    [LibraryImport(Lib)] public static partial int av_seek_frame(AVFormatContext* ctx, int stream, long timestamp, int flags);
}
