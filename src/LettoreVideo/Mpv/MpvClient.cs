using System.Diagnostics;
using System.Runtime.InteropServices;
using static LettoreVideo.Mpv.MpvNative;

namespace LettoreVideo.Mpv;

public sealed class MpvPropertyChangedEventArgs(string name, object? value) : EventArgs
{
    public string Name { get; } = name;
    public object? Value { get; } = value;
}

public sealed class MpvEndFileEventArgs(int reason) : EventArgs
{
    public int Reason { get; } = reason;

    /// <summary>The file played through to its natural end.</summary>
    public bool ReachedEnd => Reason == MPV_END_FILE_REASON_EOF;

    /// <summary>Playback aborted: unreadable file, broken container, vanished share.</summary>
    public bool Failed => Reason == MPV_END_FILE_REASON_ERROR;
}

/// <summary>
/// Managed wrapper around a libmpv instance. Commands may be issued from any
/// thread; events are read on a dedicated thread and raised there, so handlers
/// must marshal to the UI thread themselves.
/// </summary>
public sealed class MpvClient : IDisposable
{
    /// <summary>
    /// Serializes every native call against Dispose. Without it the null check and
    /// the P/Invoke that follows it are two separate reads of _ctx, and a teardown
    /// slipping between them would hand libmpv a destroyed handle.
    /// The blocking mpv_wait_event in the event loop deliberately stays outside.
    /// </summary>
    private readonly object _sync = new();

    private IntPtr _ctx;
    private Thread? _eventThread;
    private volatile bool _running;

    public event EventHandler<MpvPropertyChangedEventArgs>? PropertyChanged;
    public event EventHandler<MpvEndFileEventArgs>? EndFile;
    public event EventHandler? FileLoaded;
    public event EventHandler<string>? LogMessage;

    public bool IsCreated => _ctx != IntPtr.Zero;

    /// <summary>
    /// Creates the mpv instance and applies the startup options. Must be called
    /// before <see cref="Initialize"/>; some options (notably "wid") only take
    /// effect when set on an uninitialized handle.
    /// </summary>
    public void Create()
    {
        if (_ctx != IntPtr.Zero)
            throw new InvalidOperationException("mpv instance already created.");

        _ctx = mpv_create();
        if (_ctx == IntPtr.Zero)
            throw new InvalidOperationException("mpv_create() failed: libmpv-2.dll could not be loaded or initialized.");
    }

    public void SetOption(string name, string value)
    {
        lock (_sync)
            Check(mpv_set_option_string(_ctx, name, value), $"set option {name}={value}");
    }

    /// <summary>Sets an option that mpv may legitimately reject (unsupported hardware, etc.).</summary>
    public bool TrySetOption(string name, string value)
    {
        lock (_sync)
            return mpv_set_option_string(_ctx, name, value) >= 0;
    }

    public void Initialize()
    {
        lock (_sync)
        {
            Check(mpv_initialize(_ctx), "mpv_initialize");
            mpv_request_log_messages(_ctx, "warn");
        }

        _running = true;
        _eventThread = new Thread(EventLoop)
        {
            IsBackground = true,
            Name = "mpv-events",
        };
        _eventThread.Start();
    }

    public void ObserveProperty(string name, int format)
    {
        lock (_sync)
            Check(mpv_observe_property(_ctx, 0, name, format), $"observe {name}");
    }

    public void Command(params string[] args)
    {
        // Arguments are marshaled individually, so file paths containing spaces,
        // brackets or quotes never need escaping.
        var ptrs = new IntPtr[args.Length + 1];
        try
        {
            for (int i = 0; i < args.Length; i++)
                ptrs[i] = Utf8ToPtr(args[i]);
            ptrs[args.Length] = IntPtr.Zero;

            lock (_sync)
            {
                if (_ctx == IntPtr.Zero) return;
                Check(mpv_command(_ctx, ptrs), $"command {string.Join(' ', args)}");
            }
        }
        finally
        {
            for (int i = 0; i < args.Length; i++)
                if (ptrs[i] != IntPtr.Zero)
                    Marshal.FreeHGlobal(ptrs[i]);
        }
    }

    public void SetProperty(string name, string value)
    {
        lock (_sync)
        {
            if (_ctx == IntPtr.Zero) return;
            Check(mpv_set_property_string(_ctx, name, value), $"set property {name}={value}");
        }
    }

    public void SetProperty(string name, double value)
    {
        lock (_sync)
        {
            if (_ctx == IntPtr.Zero) return;
            Check(mpv_set_property(_ctx, name, MPV_FORMAT_DOUBLE, ref value), $"set property {name}={value}");
        }
    }

    public void SetProperty(string name, bool value)
    {
        lock (_sync)
        {
            if (_ctx == IntPtr.Zero) return;
            int flag = value ? 1 : 0;
            Check(mpv_set_property(_ctx, name, MPV_FORMAT_FLAG, ref flag), $"set property {name}={value}");
        }
    }

    public double? GetDouble(string name)
    {
        lock (_sync)
        {
            if (_ctx == IntPtr.Zero) return null;
            return mpv_get_property(_ctx, name, MPV_FORMAT_DOUBLE, out double value) >= 0 ? value : null;
        }
    }

    public long? GetLong(string name)
    {
        lock (_sync)
        {
            if (_ctx == IntPtr.Zero) return null;
            return mpv_get_property(_ctx, name, MPV_FORMAT_INT64, out long value) >= 0 ? value : null;
        }
    }

    private void EventLoop()
    {
        // Captured once: Dispose() clears _ctx before waking this thread up, but
        // keeps the handle alive until this loop has returned.
        IntPtr ctx = _ctx;

        try
        {
            PumpEvents(ctx);
        }
        catch (Exception ex)
        {
            // This runs on a background thread, where an escaping exception would
            // kill the process outright with no dispatcher handler to catch it.
            Debug.WriteLine("mpv event loop failed: " + ex);
            LoopFailed?.Invoke(this, ex);
        }
    }

    /// <summary>Raised when the event thread dies unexpectedly; playback is over at that point.</summary>
    public event EventHandler<Exception>? LoopFailed;

    private void PumpEvents(IntPtr ctx)
    {
        while (_running)
        {
            IntPtr evPtr = mpv_wait_event(ctx, 0.1);
            if (evPtr == IntPtr.Zero) continue;

            var ev = Marshal.PtrToStructure<MpvEvent>(evPtr);
            switch (ev.EventId)
            {
                case MPV_EVENT_NONE:
                    continue;

                case MPV_EVENT_SHUTDOWN:
                    _running = false;
                    return;

                case MPV_EVENT_FILE_LOADED:
                    FileLoaded?.Invoke(this, EventArgs.Empty);
                    break;

                case MPV_EVENT_END_FILE:
                {
                    var end = Marshal.PtrToStructure<MpvEventEndFile>(ev.Data);
                    EndFile?.Invoke(this, new MpvEndFileEventArgs(end.Reason));
                    break;
                }

                case MPV_EVENT_LOG_MESSAGE:
                {
                    var msg = Marshal.PtrToStructure<MpvEventLogMessage>(ev.Data);
                    string text = $"[{PtrToUtf8(msg.Prefix)}] {PtrToUtf8(msg.Text)?.TrimEnd()}";
                    Debug.WriteLine("mpv " + text);
                    LogMessage?.Invoke(this, text);
                    break;
                }

                case MPV_EVENT_PROPERTY_CHANGE:
                {
                    var prop = Marshal.PtrToStructure<MpvEventProperty>(ev.Data);
                    string? name = PtrToUtf8(prop.Name);
                    if (name is null) break;

                    object? value = prop.Format switch
                    {
                        MPV_FORMAT_DOUBLE when prop.Data != IntPtr.Zero => Marshal.PtrToStructure<double>(prop.Data),
                        MPV_FORMAT_INT64 when prop.Data != IntPtr.Zero => Marshal.PtrToStructure<long>(prop.Data),
                        MPV_FORMAT_FLAG when prop.Data != IntPtr.Zero => Marshal.PtrToStructure<int>(prop.Data) != 0,
                        MPV_FORMAT_STRING when prop.Data != IntPtr.Zero => PtrToUtf8(Marshal.ReadIntPtr(prop.Data)),
                        _ => null,
                    };

                    PropertyChanged?.Invoke(this, new MpvPropertyChangedEventArgs(name, value));
                    break;
                }
            }
        }
    }

    private static void Check(int status, string what)
    {
        if (status < 0)
            Debug.WriteLine($"mpv error on '{what}': {PtrToUtf8(mpv_error_string(status))}");
    }

    private static IntPtr Utf8ToPtr(string s)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(s);
        IntPtr ptr = Marshal.AllocHGlobal(bytes.Length + 1);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        Marshal.WriteByte(ptr, bytes.Length, 0);
        return ptr;
    }

    private static string? PtrToUtf8(IntPtr ptr) =>
        ptr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(ptr);

    public void Dispose()
    {
        _running = false;

        IntPtr ctx;
        lock (_sync)
        {
            ctx = _ctx;
            if (ctx == IntPtr.Zero) return;

            // Blocks any further API calls from this point on; the handle stays
            // valid until the event thread has left mpv_wait_event.
            _ctx = IntPtr.Zero;
        }

        mpv_wakeup(ctx);

        // mpv forbids destroying a handle another thread is still using. If the
        // event loop refuses to come back, leaking the handle is the lesser evil:
        // the process is exiting anyway, and destroying it here would be a crash.
        if (_eventThread is not null && !_eventThread.Join(TimeSpan.FromSeconds(2)))
        {
            Debug.WriteLine("mpv: event thread did not stop in time; skipping mpv_terminate_destroy.");
            return;
        }

        mpv_terminate_destroy(ctx);
    }
}
