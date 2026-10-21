using FFmpeg.AutoGen;

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
        FFmpegLoader.Check(ffmpeg.avformat_open_input(&fmt, path, null, null), "apertura del file");
        _fmt = fmt;

        try
        {
            FFmpegLoader.Check(ffmpeg.avformat_find_stream_info(_fmt, null), "lettura degli stream");

            VideoIndex = ffmpeg.av_find_best_stream(_fmt, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (VideoIndex >= 0 && (VideoStream->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0)
                VideoIndex = -1;   // cover art of an audio file is not a video

            AudioIndex = ffmpeg.av_find_best_stream(_fmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1,
                VideoIndex >= 0 ? VideoIndex : -1, null, 0);
            if (AudioIndex < 0) AudioIndex = -1;

            if (VideoIndex < 0)
                throw new InvalidOperationException("il file non contiene video");

            // Everything else is never decoded: tell the demuxer not to bother.
            for (int i = 0; i < _fmt->nb_streams; i++)
                if (i != VideoIndex && i != AudioIndex)
                    _fmt->streams[i]->discard = AVDiscard.AVDISCARD_ALL;

            StartTime = _fmt->start_time != ffmpeg.AV_NOPTS_VALUE ? _fmt->start_time / (double)ffmpeg.AV_TIME_BASE : 0;
            Duration = _fmt->duration > 0 ? _fmt->duration / (double)ffmpeg.AV_TIME_BASE : 0;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Reads the next packet into <paramref name="packet"/>; AVERROR_EOF at the end.</summary>
    public int Read(AVPacket* packet) => ffmpeg.av_read_frame(_fmt, packet);

    /// <summary>Seeks to the keyframe at or before <paramref name="mediaSeconds"/>.</summary>
    public void Seek(double mediaSeconds)
    {
        long ts = (long)(mediaSeconds * ffmpeg.AV_TIME_BASE);
        int r = ffmpeg.avformat_seek_file(_fmt, -1, long.MinValue, ts, ts, 0);
        if (r < 0)
            r = ffmpeg.av_seek_frame(_fmt, -1, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
        FFmpegLoader.Check(r, "seek");
    }

    public void Dispose()
    {
        if (_fmt == null) return;
        AVFormatContext* fmt = _fmt;
        ffmpeg.avformat_close_input(&fmt);
        _fmt = null;
    }
}
