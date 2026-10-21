using FFmpeg.AutoGen;

namespace LettoreVideo.Engine.Media;

/// <summary>
/// Demuxed packets waiting for one decoder. Each entry carries the seek serial it
/// was read under, so the decoder notices a seek and flushes its codec. A null
/// packet means "end of stream: drain the decoder".
/// </summary>
internal sealed unsafe class PacketQueue : IDisposable
{
    public readonly record struct Entry(IntPtr Packet, int Serial)
    {
        public AVPacket* Pkt => (AVPacket*)Packet;
        public bool IsDrain => Packet == IntPtr.Zero;
    }

    private readonly Queue<Entry> _items = new();
    private readonly object _sync = new();
    private bool _closed;

    public int Count { get { lock (_sync) return _items.Count; } }
    public long Bytes { get { lock (_sync) return _bytes; } }
    private long _bytes;

    /// <summary>Takes ownership of <paramref name="packet"/> (null = drain marker).</summary>
    public void Put(AVPacket* packet, int serial)
    {
        lock (_sync)
        {
            if (_closed)
            {
                Free(packet);
                return;
            }

            _items.Enqueue(new Entry((IntPtr)packet, serial));
            if (packet != null) _bytes += packet->size;
            Monitor.PulseAll(_sync);
        }
    }

    /// <summary>Blocks until a packet is available; false once the queue is closed.</summary>
    public bool Take(out Entry entry)
    {
        lock (_sync)
        {
            while (_items.Count == 0 && !_closed)
                Monitor.Wait(_sync);

            if (_closed)
            {
                entry = default;
                return false;
            }

            entry = _items.Dequeue();
            if (entry.Pkt != null) _bytes -= entry.Pkt->size;
            Monitor.PulseAll(_sync);
            return true;
        }
    }

    /// <summary>Drops every queued packet (after a seek).</summary>
    public void Clear()
    {
        lock (_sync)
        {
            while (_items.Count > 0)
                Free(_items.Dequeue().Pkt);
            _bytes = 0;
            Monitor.PulseAll(_sync);
        }
    }

    /// <summary>Wakes every waiter for good; later Puts are discarded.</summary>
    public void Close()
    {
        lock (_sync)
        {
            _closed = true;
            Monitor.PulseAll(_sync);
        }
    }

    public void Dispose()
    {
        Close();
        Clear();
    }

    public static void Free(AVPacket* packet)
    {
        if (packet == null) return;
        ffmpeg.av_packet_free(&packet);
    }
}
