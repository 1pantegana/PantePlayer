using LettoreVideo.Engine.Interop;
using LettoreVideo.Engine.Media;

namespace LettoreVideo.Engine.Playback;

/// <summary>Receives the frame to show at each display refresh. Stage 1: the Vulkan presenter.</summary>
internal interface IVideoPresenter
{
    /// <summary>Shows <paramref name="frame"/> until the next call; the frame stays alive until then.</summary>
    void Present(VideoFrame frame);
}

/// <summary>
/// One opened file: demux, decode and presentation threads plus the queues
/// between them. Created per file by <see cref="PanteEngine"/>, which owns the
/// GPU device and the presenter that outlive it.
/// </summary>
internal sealed unsafe class PlaybackSession : IDisposable
{
    private const int PacketCapacity = 256;
    private const double PositionInterval = 0.25;

    private readonly Demuxer _demuxer;
    private readonly VideoDecoder _video;
    private readonly PacketQueue _videoPackets = new(PacketCapacity);
    private readonly FrameQueue _frames = new(VideoDecoder.ExtraSurfaces - 2);
    private readonly MediaClock _clock = new();
    private readonly IVideoPresenter? _presenter;
    private readonly PlaybackStats _stats;

    private readonly Thread _demuxThread, _decodeThread, _presentThread;
    private readonly object _demuxSync = new();
    private volatile bool _stopping;

    // Seek handshake: the UI posts a request, the demux thread carries it out.
    private double _pendingSeek = double.NaN;
    private int _pendingSerial;
    private double _seekTarget = double.NegativeInfinity;

    private VideoFrame? _shown;
    private int _ended;

    public double StartTime => _demuxer.StartTime;
    public double Duration => _demuxer.Duration;
    public int Width { get; }
    public int Height { get; }
    public double NominalFps => _video.NominalFps;
    public string CodecName => _video.CodecName;

    /// <summary>Media position relative to the file start, every quarter second.</summary>
    public event Action<double>? PositionChanged;

    /// <summary>Natural end (null) or failure (the error).</summary>
    public event Action<Exception?>? Ended;

    public PlaybackSession(string path, VulkanHwDevice hwDevice, IVideoPresenter? presenter, PlaybackStats stats)
    {
        _presenter = presenter;
        _stats = stats;
        _demuxer = new Demuxer(path);
        try
        {
            AVStream* stream = _demuxer.VideoStream;
            Width = stream->codecpar->width;
            Height = stream->codecpar->height;
            _video = new VideoDecoder(stream, hwDevice);
        }
        catch
        {
            _demuxer.Dispose();
            throw;
        }

        _demuxThread = new Thread(DemuxLoop) { Name = "Pante demux", IsBackground = true };
        _decodeThread = new Thread(DecodeLoop) { Name = "Pante video decode", IsBackground = true };
        _presentThread = new Thread(PresentLoop) { Name = "Pante present", IsBackground = true, Priority = ThreadPriority.AboveNormal };
    }

    public void Start()
    {
        _demuxThread.Start();
        _decodeThread.Start();
        _presentThread.Start();
    }

    public bool Paused => _clock.Paused;

    public void SetPaused(bool paused) => _clock.SetPaused(paused);

    /// <summary>Seeks to <paramref name="position"/> seconds from the start of the file.</summary>
    public void Seek(double position)
    {
        double target = StartTime + Math.Max(0, position);
        int serial = _frames.Flush();
        _videoPackets.Clear();
        _clock.Reset();
        Interlocked.Exchange(ref _ended, 0);

        lock (_demuxSync)
        {
            _pendingSeek = target;
            _pendingSerial = serial;
            Volatile.Write(ref _seekTarget, target);
            Monitor.PulseAll(_demuxSync);
        }
    }

    #region Threads

    private void DemuxLoop()
    {
        AVPacket* packet = null;
        int serial = _frames.Serial;
        try
        {
            while (!_stopping)
            {
                lock (_demuxSync)
                {
                    if (!double.IsNaN(_pendingSeek))
                    {
                        _demuxer.Seek(_pendingSeek);
                        serial = _pendingSerial;
                        _pendingSeek = double.NaN;
                    }
                }

                packet = AvCodec.av_packet_alloc();
                int r = _demuxer.Read(packet);
                if (r == Av.ErrorEof)
                {
                    PacketQueue.Free(packet);
                    packet = null;
                    _videoPackets.Put(null, serial);   // drain the decoder

                    // Idle until a seek (or stop) gives us something to read again.
                    lock (_demuxSync)
                        while (!_stopping && double.IsNaN(_pendingSeek))
                            Monitor.Wait(_demuxSync);
                    continue;
                }

                FFmpegLoader.Check(r, "lettura del file");

                if (packet->stream_index == _demuxer.VideoIndex)
                    _videoPackets.Put(packet, serial);   // the queue owns it now
                else
                    PacketQueue.Free(packet);            // audio arrives with stage 2
                packet = null;
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        finally
        {
            PacketQueue.Free(packet);
        }
    }

    private void DecodeLoop()
    {
        int decoderSerial = -1;
        try
        {
            while (_videoPackets.Take(out var entry))
            {
                int serial = _frames.Serial;
                if (entry.Serial != serial)
                {
                    PacketQueue.Free(entry.Pkt);   // read before the last seek
                    continue;
                }

                if (decoderSerial != serial)
                {
                    if (decoderSerial >= 0) _video.Flush();
                    decoderSerial = serial;
                }

                _video.Decode(entry.Pkt, serial, OnDecoded);
                if (entry.IsDrain)
                    _frames.MarkEndOfStream(serial);
                PacketQueue.Free(entry.Pkt);
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
    }

    private void OnDecoded(VideoFrame frame)
    {
        _stats.Decoded();

        // Accurate seek: the demuxer lands on the keyframe before the target, and
        // the frames up to the target are decoded but never shown.
        if (frame.Pts + frame.Duration <= Volatile.Read(ref _seekTarget))
        {
            frame.Release();
            return;
        }

        _frames.Push(frame);
    }

    private void PresentLoop()
    {
        using var timer = new HighResolutionTimer();
        double lastReported = double.NegativeInfinity;
        try
        {
            while (!_stopping)
            {
                Tick(ref lastReported);
                timer.Sleep(1);
            }
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        finally
        {
            _shown?.Release();
            _shown = null;
        }
    }

    private void Tick(ref double lastReported)
    {
        VideoFrame? head = _frames.Peek();
        if (head is null)
        {
            if (_frames.EndOfStream && Volatile.Read(ref _ended) == 0)
            {
                double now = _clock.Now;
                double end = _shown is { } last ? last.Pts + last.Duration : double.NegativeInfinity;
                if (double.IsNaN(now) || now >= end)
                {
                    Interlocked.Exchange(ref _ended, 1);
                    Ended?.Invoke(null);
                }
            }
            return;
        }

        if (!_clock.Started)
            _clock.Start(head.Pts);   // first frame after a load or seek shows at once

        double t = _clock.Now;
        if (head.Pts > t) return;

        // Late: skip every frame whose successor is also due.
        while (_frames.Peek(1) is { } next && next.Pts <= t)
        {
            if (_frames.TryDequeue(head))
            {
                head.Release();
                _stats.Dropped();
            }
            head = next;
        }

        if (!_frames.TryDequeue(head)) return;   // a seek flushed it meanwhile

        _presenter?.Present(head);
        _stats.Presented();
        _shown?.Release();
        _shown = head;

        if (Math.Abs(head.Pts - lastReported) >= PositionInterval)
        {
            lastReported = head.Pts;
            PositionChanged?.Invoke(head.Pts - StartTime);
        }
    }

    private void Fail(Exception ex)
    {
        if (_stopping) return;
        if (Interlocked.Exchange(ref _ended, 1) == 0)
            Ended?.Invoke(ex);
    }

    #endregion

    public void Dispose()
    {
        _stopping = true;
        lock (_demuxSync) Monitor.PulseAll(_demuxSync);
        _videoPackets.Close();
        _frames.Close();

        if (_demuxThread.IsAlive) _demuxThread.Join();
        if (_decodeThread.IsAlive) _decodeThread.Join();
        if (_presentThread.IsAlive) _presentThread.Join();

        _videoPackets.Dispose();
        _video.Dispose();
        _demuxer.Dispose();
    }
}
