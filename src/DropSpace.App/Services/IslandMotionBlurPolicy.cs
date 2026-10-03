namespace DropSpace.App.Services;

/// <summary>
/// Continuous physical-pixel coordinates in one monitor/coordinate space, before region rounding.
/// The caller projects its motion/DIP values into physical coordinates; no DPI conversion occurs here.
/// </summary>
internal readonly record struct IslandMotionGeometry(double Left, double Top, double Width, double Height)
{
    internal bool HasArea => double.IsFinite(Left) && double.IsFinite(Top) &&
        double.IsFinite(Width) && double.IsFinite(Height) && Width > 0 && Height > 0;
}

internal enum IslandMotionPhase { Stable, Opening, Closing }

/// <summary>
/// Maps the current outline to a shutter outline in current mask-local physical pixels:
/// x' = ScaleX * x + OffsetX; y' = ScaleY * y + OffsetY.
/// The rendering graph must clip all samples with the current antialiased coverage mask.
/// </summary>
internal readonly record struct IslandMotionBlurSample(double ScaleX, double ScaleY, double OffsetX, double OffsetY)
{
    internal static readonly IslandMotionBlurSample Identity = new(1, 1, 0, 0);
}

internal readonly record struct IslandMotionBlurFrame(
    double Strength,
    double ExposureSeconds,
    double CornerSpeedPixelsPerSecond,
    IslandMotionBlurSample Sample0,
    IslandMotionBlurSample Sample1,
    IslandMotionBlurSample Sample2)
{
    internal const int SampleCount = 3;
    internal bool IsActive => Strength > 0;
    /// <summary>Full-exposure past transform; Sample1 uses half its delta and Sample2 the opposite half.</summary>
    internal IslandMotionBlurSample ShutterDelta => new(
        1 + 2 * (Sample1.ScaleX - 1), 1 + 2 * (Sample1.ScaleY - 1),
        2 * Sample1.OffsetX, 2 * Sample1.OffsetY);
    internal static readonly IslandMotionBlurFrame None = new(0, 0, 0,
        IslandMotionBlurSample.Identity, IslandMotionBlurSample.Identity, IslandMotionBlurSample.Identity);

    internal IslandMotionBlurSample GetSample(int index) => index switch
    {
        0 => Sample0,
        1 => Sample1,
        2 => Sample2,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };
}

/// <summary>
/// A symmetric three-sample affine shutter policy, not a Gaussian blur. Sample0 is current;
/// Sample1 is a past half-exposure and Sample2 extrapolates a future half-exposure at the measured
/// velocity. Exposure follows actual frame duration and is shortened uniformly to keep each
/// sample corner within 2.5 physical pixels of its current position. All results are value types.
/// </summary>
internal sealed class IslandMotionBlurPolicy
{
    internal const double MaximumHalfSpanPixels = 2.5;
    internal const double FullStrengthSpeedPixelsPerSecond = 1_000;
    internal const double MaximumExposureSeconds = 1d / 60;
    internal const double MaximumFrameSeconds = .125;
    internal const double MinimumSampleScale = .05;

    private bool _hasHistory;
    private IslandMotionGeometry _lastGeometry;
    private int _coordinateSpaceToken;

    /// <summary>
    /// Change coordinateSpaceToken, or call Reset, after a monitor/DPI/history change. Do not use
    /// per-frame SurfaceScale as the token: both geometries already describe physical pixels.
    /// The first eligible frame after any reset only establishes history and returns no trail.
    /// </summary>
    internal IslandMotionBlurFrame Update(
        IslandMotionGeometry previous,
        IslandMotionGeometry current,
        TimeSpan elapsed,
        IslandMotionPhase phase,
        bool enabled,
        bool visible,
        bool animated,
        bool reducedMotion,
        int coordinateSpaceToken = 0)
    {
        var seconds = elapsed.TotalSeconds;
        if (!enabled || !visible || !animated || reducedMotion ||
            phase is not (IslandMotionPhase.Opening or IslandMotionPhase.Closing) ||
            !previous.HasArea || !current.HasArea || seconds <= 0 || seconds > MaximumFrameSeconds)
        {
            Reset();
            return IslandMotionBlurFrame.None;
        }

        if (!_hasHistory || _coordinateSpaceToken != coordinateSpaceToken || _lastGeometry != previous)
        {
            _hasHistory = true;
            _coordinateSpaceToken = coordinateSpaceToken;
            _lastGeometry = current;
            return IslandMotionBlurFrame.None;
        }
        _lastGeometry = current;

        // Preserve subpixel movement at high refresh rates; integer region geometry still belongs
        // to the rendering/input owner and is not used to estimate this velocity.
        var deltaLeft = previous.Left - current.Left;
        var deltaTop = previous.Top - current.Top;
        var deltaWidth = previous.Width - current.Width;
        var deltaHeight = previous.Height - current.Height;
        var maximumX = Math.Max(Math.Abs(deltaLeft), Math.Abs(deltaLeft + deltaWidth));
        var maximumY = Math.Max(Math.Abs(deltaTop), Math.Abs(deltaTop + deltaHeight));
        var cornerDistance = Math.Sqrt(maximumX * maximumX + maximumY * maximumY);
        if (!double.IsFinite(cornerDistance))
        {
            Reset();
            return IslandMotionBlurFrame.None;
        }
        if (cornerDistance == 0) return IslandMotionBlurFrame.None;

        var speed = cornerDistance / seconds;
        var strength = Math.Min(1, speed / FullStrengthSpeedPixelsPerSecond);
        var exposure = Math.Min(Math.Min(seconds * .5, MaximumExposureSeconds), 2 * MaximumHalfSpanPixels / speed);
        // A large closing step must not extrapolate a negative-size future outline. Shorten
        // the same shutter duration on both axes, rather than independently distorting taps.
        if (deltaWidth != 0)
            exposure = Math.Min(exposure, 2 * (1 - MinimumSampleScale) * current.Width * seconds / Math.Abs(deltaWidth));
        if (deltaHeight != 0)
            exposure = Math.Min(exposure, 2 * (1 - MinimumSampleScale) * current.Height * seconds / Math.Abs(deltaHeight));
        if (exposure <= 0)
        {
            Reset();
            return IslandMotionBlurFrame.None;
        }
        var ageFraction = exposure / seconds;
        var tail = new IslandMotionBlurSample(
            1 + deltaWidth * ageFraction / current.Width,
            1 + deltaHeight * ageFraction / current.Height,
            deltaLeft * ageFraction,
            deltaTop * ageFraction);
        var past = new IslandMotionBlurSample(
            1 + (tail.ScaleX - 1) * .5,
            1 + (tail.ScaleY - 1) * .5,
            tail.OffsetX * .5,
            tail.OffsetY * .5);
        var future = new IslandMotionBlurSample(
            1 - (tail.ScaleX - 1) * .5,
            1 - (tail.ScaleY - 1) * .5,
            -tail.OffsetX * .5,
            -tail.OffsetY * .5);
        return new IslandMotionBlurFrame(strength, exposure, speed,
            IslandMotionBlurSample.Identity, past, future);
    }

    internal void Reset()
    {
        _hasHistory = false;
        _lastGeometry = default;
        _coordinateSpaceToken = 0;
    }
}
