using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using LettoreVideo.Engine.Interop;

namespace LettoreVideo.Engine.Media;

/// <summary>Loads the shared FFmpeg build shipped in "ffmpeg\" next to the exe.</summary>
internal static unsafe class FFmpegLoader
{
    // Dependency order: each DLL finds the ones before it already in the process.
    private static readonly string[] Libraries =
        ["avutil-61", "swresample-7", "swscale-10", "avcodec-63", "avformat-63", "avfilter-12"];

    private static readonly object Sync = new();
    private static readonly Dictionary<string, IntPtr> Handles = new(StringComparer.OrdinalIgnoreCase);
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

            foreach (string name in Libraries)
                Handles[name] = NativeLibrary.Load(Path.Combine(dir, name + ".dll"));

            NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), Resolve);

            AvUtil.av_log_set_level(Av.LogError);
            Version = Marshal.PtrToStringUTF8((IntPtr)AvUtil.av_version_info()) ?? "?";
            _initialized = true;
        }
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path) =>
        Handles.TryGetValue(name, out IntPtr handle) ? handle : IntPtr.Zero;

    public static string ErrorText(int error)
    {
        const int size = 256;
        byte* buffer = stackalloc byte[size];
        AvUtil.av_strerror(error, buffer, size);
        return Marshal.PtrToStringUTF8((IntPtr)buffer) ?? $"errore {error}";
    }

    public static void Check(int result, string what)
    {
        if (result < 0)
            throw new InvalidOperationException($"{what}: {ErrorText(result)}");
    }
}
