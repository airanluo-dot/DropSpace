namespace DropSpace.Core.Overlay;

/// <summary>
/// Projects a motion frame once to physical pixels. The XAML layout reverses the surface
/// transform so that its transformed bounds and radii agree with the native region identity.
/// </summary>
public readonly record struct OverlayFrameGeometry(
    OverlayRegionSignature Region,
    double DpiScale,
    double SurfaceScale)
{
    private double VisualScale => DpiScale * SurfaceScale;

    public double WidthDips => Region.WidthPixels / VisualScale;

    public double HeightDips => Region.HeightPixels / VisualScale;

    public double TopRadiusDips => Region.TopRadiusPixels / VisualScale;

    public double BottomRadiusDips => Region.BottomRadiusPixels / VisualScale;

    public double TranslationXDips(double rootWidthDips) =>
        (Region.LeftPixels + Region.WidthPixels / 2d) / DpiScale - rootWidthDips / 2;

    public double TranslationYDips =>
        Region.TopPixels / DpiScale - HeightDips * (1 - SurfaceScale) / 2;

    public static OverlayFrameGeometry Create(
        OverlayMotionValues values,
        double hostWidthDips,
        double dpiScale)
    {
        if (!double.IsFinite(dpiScale) || dpiScale <= 0)
            throw new ArgumentOutOfRangeException(nameof(dpiScale));
        if (!double.IsFinite(hostWidthDips) || hostWidthDips <= 0)
            throw new ArgumentOutOfRangeException(nameof(hostWidthDips));

        values = values.ProjectToSafeRange();
        var surfaceScale = Math.Min(values.DropTargetScale, 1);
        var region = OverlayRegionSignature.Create(
            values.Width * surfaceScale,
            values.Height * surfaceScale,
            values.TopRadius * surfaceScale,
            values.BottomRadius * surfaceScale,
            dpiScale);
        // Retain the existing rounded bounds and integer placement. XAML compensates for
        // the half-pixel center when host and body widths have different parity.
        region = region with
        {
            LeftPixels = (RoundPixels(hostWidthDips * dpiScale) - region.WidthPixels) / 2,
            TopPixels = RoundPixels(
                (values.TopOffset + values.Height * (1 - surfaceScale) / 2) * dpiScale),
        };
        return new OverlayFrameGeometry(region, dpiScale, surfaceScale);
    }

    private static int RoundPixels(double pixels) =>
        (int)Math.Clamp(Math.Round(pixels, MidpointRounding.ToEven), 0, int.MaxValue);
}
