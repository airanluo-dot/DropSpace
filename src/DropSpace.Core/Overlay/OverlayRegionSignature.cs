namespace DropSpace.Core.Overlay;

public enum OverlayShapeMode
{
    AsymmetricRounded,
    Empty,
}

/// <summary>
/// The complete physical identity that can cause a native HRGN update. It includes the client
/// origin because SetWindowRgn and the visible surface must remain identical after scale pulses,
/// monitor relocation, and mixed-DPI transitions.
/// </summary>
public readonly record struct OverlayRegionSignature(
    int LeftPixels,
    int TopPixels,
    int WidthPixels,
    int HeightPixels,
    int TopRadiusPixels,
    int BottomRadiusPixels,
    int DpiMilliScale,
    OverlayShapeMode ShapeMode)
{
    public static OverlayRegionSignature Empty { get; } = new(
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        OverlayShapeMode.Empty);

    public static OverlayRegionSignature Create(
        int leftPixels,
        int topPixels,
        double widthDip,
        double heightDip,
        double topRadiusDip,
        double bottomRadiusDip,
        double dpiScale,
        OverlayShapeMode shapeMode = OverlayShapeMode.AsymmetricRounded)
    {
        var scale = double.IsFinite(dpiScale) && dpiScale > 0 ? dpiScale : 1;
        var width = DipToPixels(widthDip, scale);
        var height = DipToPixels(heightDip, scale);
        var maximumRadius = Math.Min(width / 2, height / 2);
        return new OverlayRegionSignature(
            leftPixels,
            topPixels,
            width,
            height,
            Math.Min(DipToPixels(topRadiusDip, scale), maximumRadius),
            Math.Min(DipToPixels(bottomRadiusDip, scale), maximumRadius),
            (int)Math.Clamp(Math.Round(scale * 1_000, MidpointRounding.AwayFromZero), 1, int.MaxValue),
            shapeMode);
    }

    public static OverlayRegionSignature Create(
        double widthDip,
        double heightDip,
        double topRadiusDip,
        double bottomRadiusDip,
        double dpiScale,
        OverlayShapeMode shapeMode = OverlayShapeMode.AsymmetricRounded) =>
        Create(0, 0, widthDip, heightDip, topRadiusDip, bottomRadiusDip, dpiScale, shapeMode);

    public bool IsEmpty =>
        ShapeMode == OverlayShapeMode.Empty ||
        WidthPixels <= 0 ||
        HeightPixels <= 0;

    private static int DipToPixels(double dip, double scale)
    {
        if (!double.IsFinite(dip) || dip <= 0)
        {
            return 0;
        }

        // Match the host's existing ToPixels policy, including half-pixel ties. Do not
        // invent a one-pixel radius when a positive subpixel radius rounds to zero.
        return (int)Math.Clamp(Math.Round(dip * scale, MidpointRounding.ToEven), 0, int.MaxValue);
    }
}

public sealed class OverlayRegionUpdatePolicy
{
    private OverlayRegionSignature? _lastApplied;

    public long ApplyAttempts { get; private set; }

    public long SkippedUpdates { get; private set; }

    public OverlayRegionSignature? LastApplied => _lastApplied;

    public bool ShouldApply(OverlayRegionSignature signature)
    {
        if (_lastApplied is { } last && last == signature)
        {
            SkippedUpdates++;
            return false;
        }

        ApplyAttempts++;
        _lastApplied = signature;
        return true;
    }

    public void Reset() => _lastApplied = null;
}
