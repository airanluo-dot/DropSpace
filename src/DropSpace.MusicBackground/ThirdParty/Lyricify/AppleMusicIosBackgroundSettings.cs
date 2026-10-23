// Derived from Lyricify Backgrounds by XY Wang / contributors, Apache-2.0.
// Pinned upstream: cbef8a667c02a01707c78c7e76dc67d65cf161ed.
// DropSpace modifications are documented in UPSTREAM.md; see LICENSE.txt.
namespace Lyricify.Backgrounds.AppleMusicInspired.Ios;

public sealed class AppleMusicIosBackgroundSettings
{
    public int? FrameRateLimit { get; set; } = -1;
    public double RenderScale { get; set; } = 1d;
    public double RotationScale { get; set; } = 1d;
    public double BassPulseScale { get; set; } = 1d;
    public double BlurScale { get; set; } = 1d;
    public int PortraitControlPointCount { get; set; } = -1;
    public int PortraitSubdivisionLevels { get; set; } = -1;
    public int LandscapeControlPointCount { get; set; } = -1;
    public int LandscapeSubdivisionLevels { get; set; } = -1;

    public AppleMusicIosBackgroundSettings Clone() =>
        (AppleMusicIosBackgroundSettings)MemberwiseClone();
}
