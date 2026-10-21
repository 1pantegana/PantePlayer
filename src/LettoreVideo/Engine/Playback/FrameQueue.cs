namespace LettoreVideo.Engine.Playback;

/// <summary>
/// Frames ready to be shown, ordered by presentation time. The producer blocks
/// while the queue is full; a seek bumps <see cref="Serial"/>, which discards the
/// queued frames and makes stale producers drop theirs.
/// </summary>
internal sealed class FrameQueue
{
    private readonly List<VideoFrame> _frames = [];
    private readonly object _sync = new();
    private int _capacity;
    private int _serial;
    private int _eofSerial = -1;
    private bool _closed;

    public FrameQueue(int capacity) => _capacity = capacity;

    public int Serial { get { lock (_sync) return _serial; } }

    public int Count { get { lock (_sync) return _frames.Count; } }

    public int Capacity
    {
        get { lock (_sync) return _capacity; }
        set { lock (_sync) { _capacity = Math.Max(1, value); Monitor.PulseAll(_sync); } }
    }

    /// <summary>True once the decoder has delivered its last frame for the current serial.</summary>
    public bool EndOfStream { get { lock (_sync) return _eofSerial == _serial; } }

    /// <summary>
    /// Queues <paramref name="frame"/>, waiting for room. Returns false (and releases
    /// the frame) when it belongs to an old serial or the queue was closed.
    /// </summary>
    public bool Push(VideoFrame frame)
    {
        lock (_sync)
        {
            while (!_closed && frame.Serial == _serial && _frames.Count >= _capacity)
                Monitor.Wait(_sync);

            if (_closed || frame.Serial != _serial)
            {
                frame.Release();
                return false;
            }

            int i = _frames.Count;
            while (i > 0 && _frames[i - 1].Pts > frame.Pts) i--;
            _frames.Insert(i, frame);
            Monitor.PulseAll(_sync);
            return true;
        }
    }

    public void MarkEndOfStream(int serial)
    {
        lock (_sync)
        {
            if (serial == _serial) _eofSerial = serial;
            Monitor.PulseAll(_sync);
        }
    }

    public VideoFrame? Peek(int index = 0)
    {
        lock (_sync) return index < _frames.Count ? _frames[index] : null;
    }

    /// <summary>Removes the head frame if it is still <paramref name="expected"/>.</summary>
    public bool TryDequeue(VideoFrame expected)
    {
        lock (_sync)
        {
            if (_frames.Count == 0 || _frames[0] != expected) return false;
            _frames.RemoveAt(0);
            Monitor.PulseAll(_sync);
            return true;
        }
    }

    /// <summary>Starts a new serial (seek): every queued frame is released.</summary>
    public int Flush()
    {
        lock (_sync)
        {
            _serial++;
            foreach (var f in _frames) f.Release();
            _frames.Clear();
            Monitor.PulseAll(_sync);
            return _serial;
        }
    }

    public void Close()
    {
        lock (_sync)
        {
            _closed = true;
            foreach (var f in _frames) f.Release();
            _frames.Clear();
            Monitor.PulseAll(_sync);
        }
    }
}

