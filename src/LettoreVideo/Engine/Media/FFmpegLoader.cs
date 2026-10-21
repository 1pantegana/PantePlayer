using System.IO;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace LettoreVideo.Engine.Media;

/// <summary>Points FFmpeg.AutoGen at the shared FFmpeg build shipped in "ffmpeg\".</summary>
internal static unsafe class FFmpegLoader
{
    private static readonly object Sync = new();
    private static bool _initialized;

    public static string Version { get; private set; } = string.Empty;

    public static void Initialize()
    {
        lock (Sync)
        {
            if (_initialized) return;

            string dir = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
            if (!File.Exists(Path.Combine(dir, "avcodec-63.dll")))
                throw new FileNotFoundException(
                    $"FFmpeg non trovato in «{dir}». Copia lì le DLL della build LGPL shared (vedi README).");

            ffmpeg.RootPath = dir;
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
            Version = ffmpeg.av_version_info();
            _initialized = true;
        }
    }

    public static string ErrorText(int error)
    {
        const int size = 256;
        byte* buffer = stackalloc byte[size];
        ffmpeg.av_strerror(error, buffer, size);
        return Marshal.PtrToStringUTF8((IntPtr)buffer) ?? $"errore {error}";
    }

    public static void Check(int result, string what)
    {
        if (result < 0)
            throw new InvalidOperationException($"{what}: {ErrorText(result)}");
    }
}
