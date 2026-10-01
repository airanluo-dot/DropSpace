namespace DropSpace.Core.Lyrics;

/// <summary>Frame-rate independent brightness smoothing. Losing eligibility always converges to zero.</summary>
public sealed class LyricsGlowEnvelope
{
    public double Brightness { get; private set; }

    public double Advance(bool eligible, double normalizedEnergy, TimeSpan elapsed)
    {
        var seconds = elapsed.TotalSeconds;
        if (!double.IsFinite(seconds) || seconds <= 0) return Brightness;
        var energy = double.IsFinite(normalizedEnergy) ? Math.Clamp(normalizedEnergy, 0, 1) : 0;
        // A soft baseline remains in music mode even during quiet passages. The mode policy,
        // rather than audio loudness, is responsible for deciding when the glow may exist.
        var target = eligible ? 0.12 + 0.68 * Math.Sqrt(energy) : 0;
        var timeConstant = !eligible ? 0.05 : target > Brightness ? 0.10 : 0.30;
        var blend = 1 - Math.Exp(-Math.Min(seconds, 5) / timeConstant);
        Brightness += (target - Brightness) * blend;
        if (!eligible && Brightness < 0.002) Brightness = 0;
        return Brightness;
    }
}
