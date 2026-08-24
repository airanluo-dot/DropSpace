namespace DropSpace.Core.Overlay;

public readonly record struct OverlayMotionValues(
    double Width,
    double Height,
    double TopOffset,
    double TopRadius,
    double BottomRadius,
    double Opacity,
    double CompactContent,
    double DragContent,
    double ExpandedContent,
    double DropTargetScale,
    double ShadowOpacity = 0)
{
    public const double MinimumDimension = 1;
    public const double MinimumDropTargetScale = 0.75;
    public const double MaximumDropTargetScale = 1.25;

    public static OverlayMotionValues Hidden { get; } = new(
        120,
        12,
        0,
        6,
        6,
        0,
        0,
        0,
        0,
        0.92,
        0);

    /// <summary>
    /// Projects spring output into the semantic range accepted by WinUI and Win32 geometry APIs.
    /// The spring channels intentionally retain their unconstrained velocity so a target reversal
    /// remains fluid; only the values exposed to rendering are clamped.
    /// </summary>
    public OverlayMotionValues ProjectToSafeRange()
    {
        var width = Math.Max(MinimumDimension, FiniteOr(Width, Hidden.Width));
        var height = Math.Max(MinimumDimension, FiniteOr(Height, Hidden.Height));
        var maximumRadius = Math.Min(width, height) / 2;
        return new OverlayMotionValues(
            width,
            height,
            Math.Max(0, FiniteOr(TopOffset, Hidden.TopOffset)),
            Math.Clamp(FiniteOr(TopRadius, 0), 0, maximumRadius),
            Math.Clamp(FiniteOr(BottomRadius, 0), 0, maximumRadius),
            Math.Clamp(FiniteOr(Opacity, 0), 0, 1),
            Math.Clamp(FiniteOr(CompactContent, 0), 0, 1),
            Math.Clamp(FiniteOr(DragContent, 0), 0, 1),
            Math.Clamp(FiniteOr(ExpandedContent, 0), 0, 1),
            Math.Clamp(
                FiniteOr(DropTargetScale, 1),
                MinimumDropTargetScale,
                MaximumDropTargetScale),
            Math.Clamp(FiniteOr(ShadowOpacity, 0), 0, 1));
    }

    public bool IsApiSafe()
    {
        var values = new[]
        {
            Width,
            Height,
            TopOffset,
            TopRadius,
            BottomRadius,
            Opacity,
            CompactContent,
            DragContent,
            ExpandedContent,
            DropTargetScale,
            ShadowOpacity,
        };
        var maximumRadius = Math.Min(Width, Height) / 2;
        return values.All(double.IsFinite) &&
               Width >= MinimumDimension &&
               Height >= MinimumDimension &&
               TopOffset >= 0 &&
               TopRadius is >= 0 && TopRadius <= maximumRadius &&
               BottomRadius is >= 0 && BottomRadius <= maximumRadius &&
               Opacity is >= 0 and <= 1 &&
               CompactContent is >= 0 and <= 1 &&
               DragContent is >= 0 and <= 1 &&
               ExpandedContent is >= 0 and <= 1 &&
               ShadowOpacity is >= 0 and <= 1 &&
               DropTargetScale is >= MinimumDropTargetScale and <= MaximumDropTargetScale;
    }

    private static double FiniteOr(double value, double fallback) => double.IsFinite(value) ? value : fallback;
}

/// <summary>
/// A real-time, interruptible damped-spring controller. It owns no timer: callers request frames
/// only while <see cref="IsAnimating"/> is true and may replace the target at any point.
/// </summary>
public sealed class OverlayMotionController
{
    private const double ValueEpsilon = 0.025;
    private const double VelocityEpsilon = 0.025;
    private readonly SpringChannel[] _channels;
    private OverlayMotionValues _target;

    public OverlayMotionController(OverlayMotionValues initial)
    {
        Validate(initial);
        Current = initial.ProjectToSafeRange();
        _target = Current;
        _channels = CreateChannels(Current);
    }

    public OverlayMotionValues Current { get; private set; }

    public OverlayMotionValues Target => _target;

    public bool IsAnimating => _channels.Any(channel => !channel.IsSettled);

    public void SetTarget(OverlayMotionValues target, bool reducedMotion)
    {
        Validate(target);
        _target = target;
        var values = ToArray(target);
        for (var index = 0; index < _channels.Length; index++)
        {
            _channels[index].Target = values[index];
            _channels[index].ReducedMotion = reducedMotion;
        }
    }

    public bool Step(TimeSpan elapsed)
    {
        var seconds = Math.Clamp(elapsed.TotalSeconds, 1d / 1_000d, 1d / 30d);
        foreach (var channel in _channels)
        {
            channel.Step(seconds);
        }

        Current = FromChannels(_channels).ProjectToSafeRange();
        return IsAnimating;
    }

    public void SnapTo(OverlayMotionValues values)
    {
        Validate(values);
        _target = values;
        var raw = ToArray(values);
        for (var index = 0; index < _channels.Length; index++)
        {
            _channels[index].Value = raw[index];
            _channels[index].Target = raw[index];
            _channels[index].Velocity = 0;
        }

        Current = values.ProjectToSafeRange();
    }

    public void PulseDropTarget(double scale)
    {
        if (!double.IsFinite(scale) ||
            scale is < OverlayMotionValues.MinimumDropTargetScale or > OverlayMotionValues.MaximumDropTargetScale)
        {
            throw new ArgumentOutOfRangeException(nameof(scale));
        }

        var channel = _channels[9];
        channel.Value = scale;
        channel.Velocity = 0;
    }

    private static SpringChannel[] CreateChannels(OverlayMotionValues values) =>
        ToArray(values).Select(value => new SpringChannel(value)).ToArray();

    private static double[] ToArray(OverlayMotionValues values) =>
    [
        values.Width,
        values.Height,
        values.TopOffset,
        values.TopRadius,
        values.BottomRadius,
        values.Opacity,
        values.CompactContent,
        values.DragContent,
        values.ExpandedContent,
        values.DropTargetScale,
        values.ShadowOpacity,
    ];

    private static OverlayMotionValues FromChannels(SpringChannel[] channels) => new(
        channels[0].Value,
        channels[1].Value,
        channels[2].Value,
        channels[3].Value,
        channels[4].Value,
        channels[5].Value,
        channels[6].Value,
        channels[7].Value,
        channels[8].Value,
        channels[9].Value,
        channels[10].Value);

    private static void Validate(OverlayMotionValues values)
    {
        if (ToArray(values).Any(value => !double.IsFinite(value)) ||
            values.Width <= 0 ||
            values.Height <= 0 ||
            values.TopOffset < 0 ||
            values.TopRadius < 0 ||
            values.BottomRadius < 0 ||
            values.Opacity is < 0 or > 1 ||
            values.CompactContent is < 0 or > 1 ||
            values.DragContent is < 0 or > 1 ||
            values.ExpandedContent is < 0 or > 1 ||
            values.ShadowOpacity is < 0 or > 1 ||
            values.DropTargetScale is < OverlayMotionValues.MinimumDropTargetScale or > OverlayMotionValues.MaximumDropTargetScale ||
            values.TopRadius > Math.Min(values.Width, values.Height) / 2 ||
            values.BottomRadius > Math.Min(values.Width, values.Height) / 2)
        {
            throw new ArgumentOutOfRangeException(nameof(values));
        }
    }

    private sealed class SpringChannel(double value)
    {
        public double Value { get; set; } = value;

        public double Target { get; set; } = value;

        public double Velocity { get; set; }

        public bool ReducedMotion { get; set; }

        public bool IsSettled =>
            Math.Abs(Target - Value) <= ValueEpsilon &&
            Math.Abs(Velocity) <= VelocityEpsilon;

        public void Step(double seconds)
        {
            if (IsSettled)
            {
                Value = Target;
                Velocity = 0;
                return;
            }

            // Semi-implicit Euler is stable for this bounded dt and lets a new target retain the
            // current velocity, which is what makes a mid-transition reversal visually continuous.
            var angularFrequency = ReducedMotion ? 30d : 18d;
            var dampingRatio = ReducedMotion ? 1d : 0.82d;
            var acceleration =
                angularFrequency * angularFrequency * (Target - Value) -
                2d * dampingRatio * angularFrequency * Velocity;
            Velocity += acceleration * seconds;
            Value += Velocity * seconds;

            if (!double.IsFinite(Value) || !double.IsFinite(Velocity))
            {
                Value = Target;
                Velocity = 0;
            }

            if (IsSettled)
            {
                Value = Target;
                Velocity = 0;
            }
        }
    }
}

public enum OverlayVisualPhase
{
    Invisible,
    Entering,
    Visible,
    Exiting,
    Reversing,
}
