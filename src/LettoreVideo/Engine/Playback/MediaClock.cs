using System.Diagnostics;

namespace LettoreVideo.Engine.Playback;

/// <summary>
/// Media time driven by the system clock. Until stage 2 brings in the audio
/// clock this is the master: it starts at the first frame after a load or a
/// seek, and stands still while paused.
/// </summary>
internal sealed class MediaClock
{
    private readonly object _sync = new();
    private long _baseTicks;
    private double _baseMedia;
    private bool _running;
    private bool _paused;

    /// <summary>False after <see cref="Reset"/> until the first frame calls <see cref="Start"/>.</summary>
    public bool Started { get { lock (_sync) return _running; } }

    public bool Paused { get { lock (_sync) return _paused; } }

    /// <summary>Current media time in seconds (NaN before <see cref="Start"/>).</summary>
    public double Now
    {
        get
        {
            lock (_sync)
            {
                if (!_running) return double.NaN;
                if (_paused) return _baseMedia;
                return _baseMedia + Stopwatch.GetElapsedTime(_baseTicks).TotalSeconds;
            }
        }
    }

    /// <summary>Forgets the time line (load, seek): the next frame restarts it.</summary>
    public void Reset()
    {
        lock (_sync) _running = false;
    }

    public void Start(double mediaTime)
    {
        lock (_sync)
        {
            _baseMedia = mediaTime;
            _baseTicks = Stopwatch.GetTimestamp();
            _running = true;
        }
    }

    public void SetPaused(bool paused)
    {
        lock (_sync)
        {
            if (paused == _paused) return;

            if (_running)
            {
                if (paused)
                    _baseMedia += Stopwatch.GetElapsedTime(_baseTicks).TotalSeconds;
                else
                    _baseTicks = Stopwatch.GetTimestamp();
            }

            _paused = paused;
        }
    }
}
