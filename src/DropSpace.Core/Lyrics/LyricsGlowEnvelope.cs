namespace DropSpace.Core.Lyrics;

/// <summary>One continuous glow state: audio envelope, six-band contour and drifting color phase.</summary>
public sealed class LyricsGlowEnvelope
{
    private readonly double[] _bands = new double[6];

    public double Brightness { get; private set; }
    public double Phase { get; private set; }
    public double Simplification { get; private set; }
    public IReadOnlyList<double> Bands => _bands;

    public LyricsGlowVisualState Capture() => new(Brightness, Phase, Simplification,
        _bands[0], _bands[1], _bands[2], _bands[3], _bands[4], _bands[5]);

    public void Restore(LyricsGlowVisualState state)
    {
        static double Unit(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
        Brightness = Math.Min(.46, Unit(state.Brightness));
        Phase = double.IsFinite(state.Phase) ? Math.Max(0, state.Phase) : 0;
        Simplification = Unit(state.Simplification);
        double[] values = [state.Band0, state.Band1, state.Band2, state.Band3, state.Band4, state.Band5];
        for (var i = 0; i < _bands.Length; i++) _bands[i] = Unit(values[i]);
    }

    public double Advance(bool eligible, double normalizedEnergy, TimeSpan elapsed,
        bool reducedMotion = false, IReadOnlyList<double>? bands = null, bool simplifiedGlow = false)
    {
        var seconds = elapsed.TotalSeconds;
        if (!double.IsFinite(seconds) || seconds <= 0) return Brightness;
        var shapeBlend = 1 - Math.Exp(-Math.Min(seconds, 5) / (reducedMotion ? 0.028 : 0.12));
        Simplification += ((simplifiedGlow ? 1 : 0) - Simplification) * shapeBlend;
        // Freeze both contour motion and color travel during pause/off/fade-out, and
        // for reduced motion. The brightness can still settle gently in every mode.
        if (eligible && !reducedMotion)
        {
            var motionSeconds = Math.Min(seconds, 0.1);
            Phase += motionSeconds;
            for (var index = 0; index < _bands.Length; index++)
            {
                var band = bands is not null && index < bands.Count && double.IsFinite(bands[index])
                    ? Math.Clamp(bands[index], 0, 1) : 0;
                // Follow transients promptly without snapping back between FFT frames.
                var contourBlend = 1 - Math.Exp(-motionSeconds / (band > _bands[index] ? 0.06 : 0.12));
                _bands[index] += (band - _bands[index]) * contourBlend;
            }
        }
        var energy = double.IsFinite(normalizedEnergy) ? Math.Clamp(normalizedEnergy, 0, 1) : 0;
        // Reduced motion retains low static light; only state changes fade. A soft
        // baseline remains during quiet passages. The mode policy,
        // rather than audio loudness, is responsible for deciding when the glow may exist.
        var target = eligible ? 0.08 + (reducedMotion ? 0 : 0.38 * Math.Sqrt(energy)) : 0;
        var timeConstant = !eligible ? 0.28 : target > Brightness ? 0.10 : 0.30;
        var blend = 1 - Math.Exp(-Math.Min(seconds, 5) / timeConstant);
        Brightness += (target - Brightness) * blend;
        if (!eligible && Brightness < 0.002) Brightness = 0;
        return Brightness;
    }
}

/// <summary>Deep value snapshot only; no native resources, clocks or eligibility travel across DPI rebuilds.</summary>
public readonly record struct LyricsGlowVisualState(double Brightness, double Phase, double Simplification,
    double Band0, double Band1, double Band2, double Band3, double Band4, double Band5);
