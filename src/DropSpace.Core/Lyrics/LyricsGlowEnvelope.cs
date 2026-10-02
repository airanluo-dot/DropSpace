namespace DropSpace.Core.Lyrics;

/// <summary>One continuous glow state: audio envelope, six-band contour and drifting color phase.</summary>
public sealed class LyricsGlowEnvelope
{
    private readonly double[] _bands = new double[6];

    public double Brightness { get; private set; }
    public double Phase { get; private set; }
    public IReadOnlyList<double> Bands => _bands;

    public double Advance(bool eligible, double normalizedEnergy, TimeSpan elapsed,
        bool reducedMotion = false, IReadOnlyList<double>? bands = null)
    {
        var seconds = elapsed.TotalSeconds;
        if (!double.IsFinite(seconds) || seconds <= 0) return Brightness;
        // Freeze both contour motion and color travel during pause/off/fade-out, and
        // for reduced motion. The brightness can still settle gently in every mode.
        if (eligible && !reducedMotion)
        {
            var motionSeconds = Math.Min(seconds, 0.1);
            Phase += motionSeconds;
            var contourBlend = 1 - Math.Exp(-motionSeconds / 0.12);
            for (var index = 0; index < _bands.Length; index++)
            {
                var band = bands is not null && index < bands.Count && double.IsFinite(bands[index])
                    ? Math.Clamp(bands[index], 0, 1) : 0;
                _bands[index] += (band - _bands[index]) * contourBlend;
            }
        }
        var energy = double.IsFinite(normalizedEnergy) ? Math.Clamp(normalizedEnergy, 0, 1) : 0;
        // A soft baseline remains in music mode even during quiet passages. The mode policy,
        // rather than audio loudness, is responsible for deciding when the glow may exist.
        var target = eligible ? 0.08 + 0.38 * Math.Sqrt(energy) : 0;
        var timeConstant = !eligible ? 0.28 : target > Brightness ? 0.10 : 0.30;
        var blend = 1 - Math.Exp(-Math.Min(seconds, 5) / timeConstant);
        Brightness += (target - Brightness) * blend;
        if (!eligible && Brightness < 0.002) Brightness = 0;
        return Brightness;
    }
}
