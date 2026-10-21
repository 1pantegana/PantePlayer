using LettoreVideo.Engine.Gpu;
using LettoreVideo.Engine.Interop;

namespace LettoreVideo.Engine.Playback;

/// <summary>What the video processor needs to know about a frame's pixels.</summary>
internal readonly record struct FrameFormat(
    int Width,
    int Height,
    DxgiFormat PixelFormat,
    ColorSpace ColorSpace,
    int SarNum,
    int SarDen)
{
    /// <summary>Display aspect ratio, sample aspect ratio included.</summary>
    public double DisplayAspect =>
        Height == 0 ? 1 : Width * (SarNum > 0 && SarDen > 0 ? (double)SarNum / SarDen : 1.0) / Height;
}

/// <summary>
/// One picture on the GPU with the media time it must be shown at. Decoded and
/// interpolated frames look the same to the presenter.
/// </summary>
internal sealed class VideoFrame
{
    public VideoFrame(PooledTexture texture, FrameFormat format, double pts, double duration, int serial, bool generated)
    {
        Texture = texture;
        Format = format;
        Pts = pts;
        Duration = duration;
        Serial = serial;
        Generated = generated;
    }

    public PooledTexture Texture { get; }
    public FrameFormat Format { get; }

    /// <summary>Media time in seconds (container timeline, start time included).</summary>
    public double Pts { get; }
    public double Duration { get; }

    /// <summary>Seek generation the frame belongs to.</summary>
    public int Serial { get; }

    /// <summary>True for frames produced by the interpolator.</summary>
    public bool Generated { get; }

    private int _released;

    /// <summary>Hands the texture back to its pool; safe to call more than once.</summary>
    public void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
            Texture.Return();
    }
}

