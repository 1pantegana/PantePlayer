using LettoreVideo.Engine.Interop;

namespace LettoreVideo.Engine.Media;

/// <summary>Opens a media file with libavformat and picks its video and audio streams.</summary>
internal sealed unsafe class Demuxer : IDisposable
{
    private AVFormatContext* _fmt;

    public int VideoIndex { get; } = -1;
    public int AudioIndex { get; } = -1;

    public AVStream* VideoStream => VideoIndex >= 0 ? _fmt->streams[VideoIndex] : null;
    public AVStream* AudioStream => AudioIndex >= 0 ? _fmt->streams[AudioIndex] : null;
    public AVFormatContext* Format => _fmt;

    /// <summary>Media time (seconds) at which the file starts; UI positions are relative to it.</summary>
    public double StartTime { get; }

    /// <summary>Length in seconds, or 0 when the container does not say.</summary>
    public double Duration { get; }

    public Demuxer(string path)
    {
        AVFormatContext* fmt = null;
        FFmpegLoader.Check(AvFormat.avformat_open_input(&fmt, path, null, null), "apertura del file");
        _fmt = fmt;

        try
        {
            FFmpegLoader.Check(AvFormat.avformat_find_stream_info(_fmt, null), "lettura degli stream");

            VideoIndex = AvFormat.av_find_best_stream(_fmt, Av.MediaTypeVideo, -1, -1, null, 0);
            if (VideoIndex >= 0 && (VideoStream->disposition & Av.DispositionAttachedPic) != 0)
                VideoIndex = -1;   // cover art of an audio file is not a video

            AudioIndex = AvFormat.av_find_best_stream(_fmt, Av.MediaTypeAudio, -1,
                VideoIndex >= 0 ? VideoIndex : -1, null, 0);
            if (AudioIndex < 0) AudioIndex = -1;

            if (VideoIndex < 0)
                throw new InvalidOperationException("il file non contiene video");

            // Everything else is never decoded: tell the demuxer not to bother.
            for (int i = 0; i < _fmt->nb_streams; i++)
                if (i != VideoIndex && i != AudioIndex)
                    _fmt->streams[i]->discard = Av.DiscardAll;

            StartTime = _fmt->start_time != Av.NoPtsValue ? _fmt->start_time / (double)Av.TimeBase : 0;
            Duration = _fmt->duration > 0 ? _fmt->duration / (double)Av.TimeBase : 0;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Reads the next packet into <paramref name="packet"/>; AVERROR_EOF at the end.</summary>
    public int Read(AVPacket* packet) => AvFormat.av_read_frame(_fmt, packet);

    /// <summary>Seeks to the keyframe at or before <paramref name="mediaSeconds"/>.</summary>
    public void Seek(double mediaSeconds)
    {
        long ts = (long)(mediaSeconds * Av.TimeBase);
        int r = AvFormat.avformat_seek_file(_fmt, -1, long.MinValue, ts, ts, 0);
        if (r < 0)
            r = AvFormat.av_seek_frame(_fmt, -1, ts, Av.SeekFlagBackward);
        FFmpegLoader.Check(r, "seek");
    }

    public void Dispose()
    {
        if (_fmt == null) return;
        AVFormatContext* fmt = _fmt;
        AvFormat.avformat_close_input(&fmt);
        _fmt = null;
    }
}
