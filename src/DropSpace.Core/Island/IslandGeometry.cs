using DropSpace.Core.Overlay;

namespace DropSpace.Core.Island;

/// <summary>Canonical visible surface geometry. Material and native clipping derive from the same animated values.</summary>
public sealed record IslandGeometry(double Width, double Height, double Radius)
{
    public const double EmptyCompactAspectRatio = 290d / 110;
    public const double InterludeAspectRatio = 476d / 110;

    private const double IdleHeight = 40;
    public static IslandGeometry EmptyCompact { get; } = new(IdleHeight * EmptyCompactAspectRatio, IdleHeight, IdleHeight / 2);

    public static IslandGeometry ForCompact(IslandPresentationVariant variant, bool hasModuleText,
        double musicWidth, double musicHeight) => variant switch
    {
        IslandPresentationVariant.Idle when hasModuleText => new(180, IdleHeight, IdleHeight / 2),
        IslandPresentationVariant.Idle or IslandPresentationVariant.EmptyWake => EmptyCompact,
        IslandPresentationVariant.SingleFile => ForFiles(OverlayState.Compact),
        IslandPresentationVariant.MultipleFiles => new(200, 64, 32),
        IslandPresentationVariant.Music => ForMusicCompact(musicWidth, musicHeight, 1),
        IslandPresentationVariant.Activity => new(400, 80, 40),
        _ => throw new ArgumentOutOfRangeException(nameof(variant)),
    };

    // Use the same bounded measurement for the content and the native body. Large
    // accessibility text can exceed the old 100-DIP cap even with just two rows.
    public static double MusicCompactHeight(double measuredHeight) => double.IsFinite(measuredHeight)
        ? Math.Clamp(measuredHeight, 40, OverlayPlacementPolicy.MaximumSurfaceHeightDips) : 40;

    public static IslandGeometry ForMusicCompact(double measuredWidth, double measuredHeight, double scale)
    {
        scale = Math.Clamp(scale, 0.001, 2);
        var height = MusicCompactHeight(measuredHeight) * scale;
        var width = (double.IsFinite(measuredWidth) ? Math.Max(1, measuredWidth) : 180) * scale;
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
