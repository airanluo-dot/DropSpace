using DropSpace.Core.Overlay;

namespace DropSpace.Core.Island;

/// <summary>Canonical visible surface geometry. Material and native clipping derive from the same animated values.</summary>
public sealed record IslandGeometry(double Width, double Height, double Radius)
{
    // Use the same bounded measurement for the content and the native body. Large
    // accessibility text can exceed the old 100-DIP cap even with just two rows.
    public static double MusicCompactHeight(double measuredHeight) => double.IsFinite(measuredHeight)
        ? Math.Clamp(measuredHeight, 40, OverlayPlacementPolicy.MaximumSurfaceHeightDips) : 40;

    public static IslandGeometry ForMusicCompact(double measuredWidth, double measuredHeight, double scale)
    {
        scale = Math.Clamp(scale, 0.001, 2);
        var height = MusicCompactHeight(measuredHeight) * scale;
        var width = Math.Clamp(measuredWidth, 180, 460) * scale;
        return new(width, height, Math.Min(width, height) / 2);
    }
    public static IslandGeometry ForFiles(OverlayState state) => state switch
    {
        OverlayState.DragApproaching => new(300, 54, 27),
        OverlayState.DragReady => new(430, 92, 30),
        OverlayState.Compact => new(340, 64, 32),
        OverlayState.Expanded => new(560, 340, 28),
        _ => new(120, 12, 6),
    };
}
