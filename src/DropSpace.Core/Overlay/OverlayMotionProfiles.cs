using DropSpace.Core.Models;

namespace DropSpace.Core.Overlay;

public static class OverlayMotionTokens
{
    public const double InstantMilliseconds = 0;
    public const double FasterMilliseconds = 83;
    public const double FastMilliseconds = 167;
    public const double NormalMilliseconds = 250;
    public const double SlowMilliseconds = 333;
    public const double MicroDip = 2;
    public const double SmallDip = 4;
    public const double MediumDip = 6;
    public const double PressScale = 0.985;
    public const double DropConfirmationScale = 0.97;
}

public enum OverlayMotionChannel
{
    Geometry,
    SurfaceOpacity,
    ContentTransition,
    InteractionFeedback,
    ShadowElevation,
}

/// <summary>
/// Parameters for one bounded, interruptible spring channel. A profile never owns a timer;
/// the window requests frames only while the controller reports that work remains.
/// </summary>
public readonly record struct OverlaySpringProfile(
    double AngularFrequency,
    double DampingRatio,
    double RestDistance,
    double RestSpeed,
    bool IsInstant = false)
{
    public static OverlaySpringProfile Geometry { get; } = new(20, 0.92, 0.025, 0.025);

    public static OverlaySpringProfile SurfaceOpacity { get; } = new(26, 1, 0.018, 0.025);

    public static OverlaySpringProfile ContentTransition { get; } = new(24, 1, 0.012, 0.025);

    public static OverlaySpringProfile InteractionFeedback { get; } = new(24, 0.85, 0.012, 0.025);

    public static OverlaySpringProfile ShadowElevation { get; } = new(18, 1, 0.018, 0.025);

    public static OverlaySpringProfile Reduced(double durationMilliseconds = OverlayMotionTokens.FasterMilliseconds) =>
        durationMilliseconds <= OverlayMotionTokens.InstantMilliseconds
            ? new OverlaySpringProfile(0, 1, 0, 0, true)
            : new OverlaySpringProfile(42, 1, 0.025, 0.025);

    public bool IsValid =>
        (IsInstant && AngularFrequency == 0) ||
        (!IsInstant && AngularFrequency > 0 && DampingRatio > 0 && RestDistance >= 0 && RestSpeed >= 0);
}

public readonly record struct GeometryMorphProfile(
    OverlaySpringProfile Normal,
    OverlaySpringProfile Reduced)
{
    public static GeometryMorphProfile Default { get; } = new(
        OverlaySpringProfile.Geometry,
        OverlaySpringProfile.Reduced(0));
}

public readonly record struct SurfaceOpacityProfile(
    OverlaySpringProfile Normal,
    OverlaySpringProfile Reduced)
{
    public static SurfaceOpacityProfile Default { get; } = new(
        OverlaySpringProfile.SurfaceOpacity,
        OverlaySpringProfile.Reduced(0));
}

public readonly record struct ContentTransitionProfile(
    OverlaySpringProfile Normal,
    OverlaySpringProfile Reduced,
    double IncomingDelayMilliseconds = 42,
    double OutgoingDurationMilliseconds = OverlayMotionTokens.FasterMilliseconds,
    double IncomingDurationMilliseconds = OverlayMotionTokens.FastMilliseconds,
    double IncomingOffsetDip = OverlayMotionTokens.SmallDip)
{
    public static ContentTransitionProfile Default { get; } = new(
        OverlaySpringProfile.ContentTransition,
        OverlaySpringProfile.Reduced(0),
        IncomingDelayMilliseconds: 42,
        OutgoingDurationMilliseconds: OverlayMotionTokens.FasterMilliseconds,
        IncomingDurationMilliseconds: OverlayMotionTokens.FastMilliseconds,
        IncomingOffsetDip: OverlayMotionTokens.SmallDip);
}

public readonly record struct InteractionFeedbackProfile(
    OverlaySpringProfile Normal,
    OverlaySpringProfile Reduced,
    double HoverDurationMilliseconds = OverlayMotionTokens.FasterMilliseconds,
    double PressScale = OverlayMotionTokens.PressScale,
    double DropConfirmationScale = OverlayMotionTokens.DropConfirmationScale)
{
    public static InteractionFeedbackProfile Default { get; } = new(
        OverlaySpringProfile.InteractionFeedback,
        OverlaySpringProfile.Reduced(0),
        HoverDurationMilliseconds: OverlayMotionTokens.FasterMilliseconds,
        PressScale: OverlayMotionTokens.PressScale,
        DropConfirmationScale: OverlayMotionTokens.DropConfirmationScale);
}

public readonly record struct ShadowElevationProfile(
    OverlaySpringProfile Normal,
    OverlaySpringProfile Reduced)
{
    public static ShadowElevationProfile Default { get; } = new(
        OverlaySpringProfile.ShadowElevation,
        OverlaySpringProfile.Reduced(0));
}

public sealed record OverlayMotionProfileSet(
    GeometryMorphProfile Geometry,
    SurfaceOpacityProfile SurfaceOpacity,
    ContentTransitionProfile ContentTransition,
    InteractionFeedbackProfile InteractionFeedback,
    ShadowElevationProfile ShadowElevation)
{
    public static OverlayMotionProfileSet Default { get; } = new(
        GeometryMorphProfile.Default,
        SurfaceOpacityProfile.Default,
        ContentTransitionProfile.Default,
        InteractionFeedbackProfile.Default,
        ShadowElevationProfile.Default);

    public OverlaySpringProfile GetSpring(OverlayMotionChannel channel, bool reducedMotion) => channel switch
    {
        OverlayMotionChannel.Geometry => reducedMotion ? Geometry.Reduced : Geometry.Normal,
        OverlayMotionChannel.SurfaceOpacity => reducedMotion ? SurfaceOpacity.Reduced : SurfaceOpacity.Normal,
        OverlayMotionChannel.ContentTransition => reducedMotion ? ContentTransition.Reduced : ContentTransition.Normal,
        OverlayMotionChannel.InteractionFeedback => reducedMotion ? InteractionFeedback.Reduced : InteractionFeedback.Normal,
        OverlayMotionChannel.ShadowElevation => reducedMotion ? ShadowElevation.Reduced : ShadowElevation.Normal,
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel, null),
    };

    public bool IsValid =>
        Geometry.Normal.IsValid && Geometry.Reduced.IsValid &&
        SurfaceOpacity.Normal.IsValid && SurfaceOpacity.Reduced.IsValid &&
        ContentTransition.Normal.IsValid && ContentTransition.Reduced.IsValid &&
        InteractionFeedback.Normal.IsValid && InteractionFeedback.Reduced.IsValid &&
        ShadowElevation.Normal.IsValid && ShadowElevation.Reduced.IsValid &&
        ContentTransition.IncomingDelayMilliseconds is >= 0 and <= OverlayMotionTokens.FastMilliseconds &&
        ContentTransition.IncomingOffsetDip is >= 0 and <= OverlayMotionTokens.MediumDip &&
        InteractionFeedback.PressScale is > 0 and <= 1 &&
        InteractionFeedback.DropConfirmationScale is > 0 and <= 1;
}

public enum OverlayVisualPreferenceMode
{
    Full,
    Reduced,
}

public readonly record struct OverlayVisualPreferences(
    OverlayVisualPreferenceMode Motion,
    bool AdvancedEffectsEnabled,
    bool HighContrast,
    bool IsWindows11OrLater)
{
    public bool ReducedMotion => Motion == OverlayVisualPreferenceMode.Reduced;

    public bool CanUseDesktopAcrylic =>
        IsWindows11OrLater && AdvancedEffectsEnabled && !HighContrast;
}
