namespace LettoreVideo.Engine;

public enum VsrMode
{
    /// <summary>Enable RTX VSR only for sources below <see cref="Vsr.AutoMaxSourceHeight"/>.</summary>
    Auto,
    Always,
    Off,
}

/// <summary>When NVIDIA RTX Video Super Resolution is worth running, and at which factor.</summary>
public static class Vsr
{
    /// <summary>In Auto mode, VSR engages for sources shorter than this (i.e. below 1080p).</summary>
    public const int AutoMaxSourceHeight = 1080;

    /// <summary>NVIDIA VSR tops out at 4x; asking for more just wastes GPU time.</summary>
    private const double MaxScale = 4.0;

    /// <summary>Below this factor upscaling is not worth the extra GPU load.</summary>
    private const double MinUsefulScale = 1.05;

    public static bool Wanted(VsrMode mode, int sourceHeight) => mode switch
    {
        VsrMode.Always => true,
        VsrMode.Auto => sourceHeight < AutoMaxSourceHeight,
        _ => false,
    };

    /// <summary>
    /// Decides the scale factor needed to bring <paramref name="sourceHeight"/> up to
    /// <paramref name="targetHeight"/>, or null when scaling would be pointless.
    /// </summary>
    public static double? ComputeScale(int sourceHeight, int targetHeight)
    {
        if (sourceHeight <= 0 || targetHeight <= 0) return null;

        double scale = Math.Min((double)targetHeight / sourceHeight, MaxScale);
        return scale >= MinUsefulScale ? Math.Round(scale, 2) : null;
    }
}
