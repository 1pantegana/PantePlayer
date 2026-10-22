using LettoreVideo.Engine.Interop;

namespace LettoreVideo.Engine.Playback;

internal enum PlaneLayout
{
    /// <summary>8-bit Y plane + interleaved UV plane.</summary>
    Nv12,
    /// <summary>10-bit in 16-bit words, same layout as NV12.</summary>
    P010,
    /// <summary>Anything else (software decoding): the renderer has to convert it.</summary>
    Other,
}

internal enum YuvMatrix { Bt601, Bt709, Bt2020 }

internal enum TransferCurve { Sdr, Pq, Hlg }

/// <summary>How a frame's YUV values map to colours.</summary>
internal readonly record struct ColorInfo(YuvMatrix Matrix, bool FullRange, TransferCurve Transfer)
{
    // ITU-T H.273 code points, which FFmpeg's AVColor* enums follow.
    private const int SpcBt709 = 1, SpcBt470Bg = 5, SpcSmpte170M = 6, SpcBt2020Ncl = 9, SpcBt2020Cl = 10;
    private const int PriBt2020 = 9;
    private const int TrcSmpte2084 = 16, TrcAribStdB67 = 18;

    public static unsafe ColorInfo FromFrame(AVFrame* f)
    {
        bool full = f->color_range == Av.ColorRangeJpeg;

        YuvMatrix matrix = f->colorspace switch
        {
            SpcBt2020Ncl or SpcBt2020Cl => YuvMatrix.Bt2020,
            SpcBt470Bg or SpcSmpte170M => YuvMatrix.Bt601,
            SpcBt709 => YuvMatrix.Bt709,
            _ when f->color_primaries == PriBt2020 => YuvMatrix.Bt2020,
            // Untagged: the usual convention, SD is 601 and HD is 709.
            _ => f->height < 720 ? YuvMatrix.Bt601 : YuvMatrix.Bt709,
        };

        TransferCurve transfer = f->color_trc switch
        {
            TrcSmpte2084 => TransferCurve.Pq,
            TrcAribStdB67 => TransferCurve.Hlg,
            _ => TransferCurve.Sdr,
        };

        return new ColorInfo(matrix, full, transfer);
    }
}

/// <summary>What the renderer needs to know about a frame's pixels.</summary>
internal readonly record struct FrameFormat(
    int Width,
    int Height,
    PlaneLayout Layout,
    ColorInfo Color,
    int SarNum,
    int SarDen)
{
    /// <summary>Display aspect ratio, sample aspect ratio included.</summary>
    public double DisplayAspect =>
        Height == 0 ? 1 : Width * (SarNum > 0 && SarDen > 0 ? (double)SarNum / SarDen : 1.0) / Height;
}

/// <summary>The pixels of a frame, wherever they live; released once nobody needs them.</summary>
internal interface IFrameImage
{
    void Release();
}

/// <summary>
/// One picture with the media time it must be shown at. Decoded and interpolated
/// frames look the same to the presenter.
/// </summary>
internal sealed class VideoFrame(IFrameImage image, FrameFormat format, double pts, double duration, int serial, bool generated)
{
    public IFrameImage Image { get; } = image;
    public FrameFormat Format { get; } = format;

    /// <summary>Media time in seconds (container timeline, start time included).</summary>
    public double Pts { get; } = pts;
    public double Duration { get; } = duration;

    /// <summary>Seek generation the frame belongs to.</summary>
    public int Serial { get; } = serial;

    /// <summary>True for frames produced by the interpolator.</summary>
    public bool Generated { get; } = generated;

    private int _released;

    /// <summary>Hands the pixels back; safe to call more than once.</summary>
    public void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
            Image.Release();
    }
}
