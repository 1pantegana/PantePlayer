using System.Globalization;

namespace LettoreVideo.Mpv;

public enum RtxVsrMode
{
    /// <summary>Enable RTX VSR only for sources below <see cref="RtxVideoFilter.AutoMaxSourceHeight"/>.</summary>
    Auto,
    Always,
    Off,
}

/// <summary>
/// Builds the d3d11vpp filter chain that drives NVIDIA RTX Video Super Resolution.
///
/// Everything version-specific about the filter lives here: mpv exposes VSR as
/// "scaling-mode=nvidia" on the d3d11vpp filter (mpv 0.39+), and the extension
/// only kicks in when the filter actually upscales, hence the "scale" factor.
/// Requires hwdec=d3d11va and a D3D11 video output.
/// </summary>
public static class RtxVideoFilter
{
    /// <summary>In Auto mode, VSR engages for sources shorter than this (i.e. below 1080p).</summary>
    public const int AutoMaxSourceHeight = 1080;

    /// <summary>NVIDIA VSR tops out at 4x; asking for more just wastes GPU time.</summary>
    private const double MaxScale = 4.0;

    /// <summary>Below this factor upscaling is not worth the extra GPU load.</summary>
    private const double MinUsefulScale = 1.05;

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

    /// <summary>The mpv filter string, or an empty string to clear the chain.</summary>
    public static string Build(double scale, bool trueHdr)
    {
        string filter = string.Create(CultureInfo.InvariantCulture,
            $"d3d11vpp=deint=no:scaling-mode=nvidia:scale={scale:0.##}");

        return trueHdr ? filter + ":nvidia-true-hdr=yes" : filter;
    }
}
