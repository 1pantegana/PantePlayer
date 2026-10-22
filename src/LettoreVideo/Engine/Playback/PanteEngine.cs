using LettoreVideo.Engine.Media;

namespace LettoreVideo.Engine.Playback;

public sealed class EndFileEventArgs(bool reachedEnd, Exception? error) : EventArgs
{
    /// <summary>The file played through to its natural end.</summary>
    public bool ReachedEnd { get; } = reachedEnd;

    /// <summary>Playback aborted: unreadable file, broken container, vanished share.</summary>
    public bool Failed => Error is not null;

    public Exception? Error { get; } = error;
}

/// <summary>What the UI shows about the file that just opened.</summary>
public sealed record MediaInfo(string Path, double Duration, int Width, int Height, double Fps, string Codec, bool HardwareDecoding);

/// <summary>
/// The playback engine as the view model sees it. Every event is raised on an
/// engine thread: the subscriber marshals to the UI itself.
/// </summary>
public sealed class PanteEngine : IDisposable
{
    private readonly object _sync = new();
    private readonly PlaybackStats _stats = new();
    private VulkanHwDevice? _hwDevice;
    private bool _hwDeviceTried;
    private PlaybackSession? _session;
    private int _generation;
    private bool _paused;
    private bool _disposed;

    /// <summary>Native window the video is presented into (the swapchain arrives with the Vulkan presenter).</summary>
    public IntPtr VideoHandle { get; private set; }

    public event EventHandler<MediaInfo>? FileLoaded;
    public event EventHandler<EndFileEventArgs>? EndFile;
    public event EventHandler<bool>? PausedChanged;

    /// <summary>Position in seconds from the start of the file.</summary>
    public event EventHandler<double>? PositionChanged;

    /// <summary>Volume 0–130 (above 100 is software gain). Audio arrives with stage 2.</summary>
    public double Volume { get; set; } = 70;

    public bool Paused
    {
        get { lock (_sync) return _paused; }
    }

    public void Attach(IntPtr videoHandle)
    {
        VideoHandle = videoHandle;
        FFmpegLoader.Initialize();
    }

    /// <summary>
    /// Replaces whatever is playing with <paramref name="path"/>. Opening happens in
    /// the background (a network share can take seconds); the outcome arrives as
    /// <see cref="FileLoaded"/> or as a failed <see cref="EndFile"/>.
    /// </summary>
    public void Load(string path)
    {
        int generation;
        PlaybackSession? old;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            generation = ++_generation;
            old = _session;
            _session = null;
        }

        Task.Run(() =>
        {
            old?.Dispose();
            Open(path, generation);
        });
    }

    private void Open(string path, int generation)
    {
        PlaybackSession session;
        try
        {
            session = new PlaybackSession(path, HwDevice(), presenter: null, _stats);
        }
        catch (Exception ex)
        {
            if (IsCurrent(generation))
                EndFile?.Invoke(this, new EndFileEventArgs(false, ex));
            return;
        }

        lock (_sync)
        {
            if (generation != _generation || _disposed)
            {
                session.Dispose();   // another Load (or shutdown) got here first
                return;
            }

            _session = session;
            session.SetPaused(_paused);
        }

        session.PositionChanged += pos => { if (IsCurrent(generation)) PositionChanged?.Invoke(this, pos); };
        session.Ended += error =>
        {
            if (IsCurrent(generation))
                EndFile?.Invoke(this, new EndFileEventArgs(error is null, error));
        };

        var info = new MediaInfo(path, session.Duration, session.Width, session.Height,
            session.NominalFps, session.CodecName, session.HardwareDecoding);
        _stats.Note($"{System.IO.Path.GetFileName(path)}: {info.Width}x{info.Height} {info.Fps:0.###} fps, "
                    + $"{info.Codec}, {(info.HardwareDecoding ? "Vulkan" : "software")}");
        FileLoaded?.Invoke(this, info);
        session.Start();
    }

    private bool IsCurrent(int generation)
    {
        lock (_sync) return generation == _generation && !_disposed;
    }

    /// <summary>The shared Vulkan device; null (software decoding) if it cannot be created.</summary>
    private VulkanHwDevice? HwDevice()
    {
        lock (_sync)
        {
            if (_hwDeviceTried) return _hwDevice;
            _hwDeviceTried = true;
            try
            {
                _hwDevice = new VulkanHwDevice();
            }
            catch (Exception ex)
            {
                _stats.Note($"Vulkan non disponibile, decodifica software: {ex.Message}");
            }
            return _hwDevice;
        }
    }

    public void TogglePause() => SetPaused(!Paused);

    public void SetPaused(bool paused)
    {
        lock (_sync)
        {
            if (_paused == paused) return;
            _paused = paused;
            _session?.SetPaused(paused);
        }
        PausedChanged?.Invoke(this, paused);
    }

    /// <summary>Seeks to <paramref name="seconds"/> from the start of the file.</summary>
    public void Seek(double seconds)
    {
        lock (_sync) _session?.Seek(seconds);
    }

    public void Dispose()
    {
        PlaybackSession? session;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            session = _session;
            _session = null;
        }

        session?.Dispose();
        _hwDevice?.Dispose();
        _stats.Dispose();
    }
}
