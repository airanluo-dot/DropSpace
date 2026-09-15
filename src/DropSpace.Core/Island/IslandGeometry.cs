using DropSpace.Core.Overlay;

namespace DropSpace.Core.Island;

/// <summary>Canonical visible surface geometry. Material and native clipping derive from the same animated values.</summary>
public sealed record IslandGeometry(double Width, double Height, double Radius)
{
    public static IslandGeometry ForMusicCompact(double measuredWidth, double measuredHeight, double scale)
    {
        scale = Math.Clamp(scale, 0.5, 2);
        var height = Math.Clamp(measuredHeight, 40, 100) * scale;
        return new(Math.Clamp(measuredWidth, 180, 460) * scale, height, height / 2);
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
