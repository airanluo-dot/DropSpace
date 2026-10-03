namespace DropSpace.Core.Lyrics;

/// <summary>Glow-only local contrast for real audio, leaving the spectrum meter unchanged.</summary>
public sealed class LyricsGlowAudioResponse
{
    private readonly double[] _mean = new double[7];
    private readonly double[] _bands = new double[6];
    private bool _initialized;
    public double Energy { get; private set; }
    public IReadOnlyList<double> Bands => _bands;

    public void Reset()
    {
        _initialized = false;
        Energy = 0;
        Array.Clear(_mean);
        Array.Clear(_bands);
    }

    public void Advance(double energy, IReadOnlyList<double>? bands, TimeSpan elapsed)
    {
        var seconds = elapsed.TotalSeconds;
        if (!double.IsFinite(seconds) || seconds <= 0) return;
        var blend = 1 - Math.Exp(-Math.Min(seconds, .1) / 1.5);
        Energy = Map(0, energy, blend);
        for (var i = 0; i < _bands.Length; i++)
            _bands[i] = Map(i + 1, bands is not null && i < bands.Count ? bands[i] : 0, blend);
        _initialized = true;
    }

    private double Map(int index, double input, double blend)
    {
        var level = double.IsFinite(input) ? Math.Clamp(input, 0, 1) : 0;
        if (!_initialized) _mean[index] = level;
        // Reserve headroom for real changes instead of feeding a permanently loud
        // dB-normalized signal through another saturating gain. The slow reference
        // follows passages; it never invents energy in absent/silent bands.
        var response = level <= .015 ? 0 : Math.Clamp(.55 * level + 4 * (level - _mean[index]), 0, 1);
        _mean[index] += (level - _mean[index]) * blend;
        return response;
    }
}
