namespace LettoreVideo.Mpv;

public enum SmoothMotionMode
{
    /// <summary>Interpolate only sources below <see cref="SmoothMotion.AutoMaxSourceFps"/>.</summary>
    Auto,
    Always,
    Off,
}

/// <summary>
/// Frame interpolation ("Fluido"): mpv retimes the video to the display refresh
/// with video-sync=display-resample and blends neighbouring frames (interpolation)
/// so 24/25/30 fps sources stop juddering on a 60–144 Hz monitor. The audio is
/// resampled by a fraction of a percent to stay locked — inaudible in practice.
/// </summary>
public static class SmoothMotion
{
    /// <summary>
    /// In Auto mode, interpolation engages below this frame rate. 50/60 fps
    /// sources already move smoothly, and interpolating them only costs GPU time.
    /// </summary>
    public const double AutoMaxSourceFps = 45;

    /// <summary>video-sync value to use when interpolating, and when not.</summary>
    public const string SyncOn = "display-resample";
    public const string SyncOff = "audio";
}
