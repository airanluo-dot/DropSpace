namespace DropSpace.Core.Overlay;

/// <summary>Continuous choreography driven by the same interruptible spring as opacity.</summary>
public readonly record struct OverlayContentPose(double OffsetY, double Scale)
{
    public static OverlayContentPose FromProgress(double progress, bool reducedMotion, double offsetDip = 4)
    {
        var p = double.IsFinite(progress) ? Math.Clamp(progress, 0, 1) : 0;
        if (reducedMotion) return new(0, 1);
        // No direction or target-dependent reset: reversal retraces the current pose.
        return new((1 - p) * offsetDip, 0.985 + 0.015 * p);
    }
}
