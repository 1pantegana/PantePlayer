using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace LettoreVideo.Engine.Playback;

/// <summary>
/// Frame counters. With PANTE_LOG set to a file path, one line per second is
/// appended with what happened in that second: the numbers the plan's
/// acceptance criteria are written against.
/// </summary>
internal sealed class PlaybackStats : IDisposable
{
    private readonly StreamWriter? _log;
    private readonly Timer? _timer;
    private int _decoded, _presented, _dropped;

    public PlaybackStats()
    {
        if (Environment.GetEnvironmentVariable("PANTE_LOG") is not { Length: > 0 } path) return;

        _log = new StreamWriter(path, append: true) { AutoFlush = true };
        _log.WriteLine($"# {DateTime.Now:yyyy-MM-dd HH:mm:ss} decodificati/s presentati/s scartati/s");
        _timer = new Timer(_ => Flush(), null, 1000, 1000);
    }

    /// <summary>What is playing, written to the log when a file starts.</summary>
    public void Note(string text) => _log?.WriteLine("# " + text);

    public void Decoded() => Interlocked.Increment(ref _decoded);
    public void Presented() => Interlocked.Increment(ref _presented);
    public void Dropped() => Interlocked.Increment(ref _dropped);

    private void Flush()
    {
        int d = Interlocked.Exchange(ref _decoded, 0);
        int p = Interlocked.Exchange(ref _presented, 0);
        int x = Interlocked.Exchange(ref _dropped, 0);
        try
        {
            _log?.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{DateTime.Now:HH:mm:ss} dec={d} pres={p} scart={x}"));
        }
        catch (ObjectDisposedException)
        {
            // Raced with Dispose: the last second is simply not logged.
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _log?.Dispose();
    }
}

/// <summary>Millisecond sleeps without raising the system-wide timer resolution.</summary>
internal sealed class HighResolutionTimer : IDisposable
{
    private const uint CreateWaitableTimerHighResolution = 0x2;
    private const uint TimerAllAccess = 0x1F0003;
    private const uint Infinite = 0xFFFFFFFF;

    private readonly IntPtr _handle =
        CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);

    public void Sleep(int milliseconds)
    {
        if (_handle == IntPtr.Zero)
        {
            Thread.Sleep(milliseconds);   // older Windows: the ~15 ms default will do
            return;
        }

        long due = -10_000L * milliseconds;   // relative, in 100 ns units
        if (SetWaitableTimer(_handle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
            WaitForSingleObject(_handle, Infinite);
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) CloseHandle(_handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion,
        IntPtr arg, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
