using System.Numerics;

namespace DropSpace.Core.Media;

public enum AudioCaptureMode { Stopped, ProcessLoopback, EndpointMeterFallback, Unavailable }
public sealed record SpectrumFrame(AudioCaptureMode CaptureMode, IReadOnlyList<double> Bands, long Revision)
{
    public static SpectrumFrame Empty { get; } = new(AudioCaptureMode.Stopped, new double[6], 0);
}

/// <summary>Hann-windowed radix-two FFT over actual mono PCM; no synthesized spectrum.</summary>
public sealed class SpectrumAnalyzer
{
    public const int SampleCount = 2_048;
    public const int BandCount = 6;
    // Six-band profile from apple-like-waves v1.0.0 (see THIRD_PARTY_NOTICES.md).
    private static readonly double[] Edges = [20, 81, 170, 527, 1500, 4500, 10000];
    private static readonly double[] Calibration = [1.828, 1.765, 1.324, 1.351, 1.369, 1.424];
    private static readonly double[] Gains = [.706, .706, .433, .433, .339, .300];
    private static readonly double[] Exponents = [1.15, .85, .75, .85, .70, .75];
    private static readonly double[] Punch = [3.45, 2.07, 3.45, 11.5, 1.38, 1.38];
    private static readonly float[] Hann = Enumerable.Range(0, SampleCount)
        .Select(i => (float)(.5 - .5 * Math.Cos(2 * Math.PI * i / SampleCount))).ToArray();
    private static readonly int[] Reversed = BitReversal();
    private static readonly Vector2[] Twiddles = Enumerable.Range(0, SampleCount / 2)
        .Select(i => new Vector2((float)Math.Cos(-2 * Math.PI * i / SampleCount),
            (float)Math.Sin(-2 * Math.PI * i / SampleCount))).ToArray();
    private readonly float[] _pcm = new float[SampleCount];
    private readonly Vector2[] _samples = new Vector2[SampleCount];
    private readonly double[] _slow = new double[BandCount];
    private readonly double[] _previous = new double[BandCount], _older = new double[BandCount], _oldest = new double[BandCount];
    private readonly int _hop;
    private readonly double _binWidth, _slowAlpha;
    private int _write, _pending;
    private long _revision;

    public SpectrumAnalyzer(int sampleRate)
    {
        _binWidth = (double)sampleRate / SampleCount;
        _hop = Math.Max(1, (int)Math.Round(sampleRate / 60d));
        _slowAlpha = 1 - Math.Exp(-(double)_hop / sampleRate / .8);
    }

    private static int[] BitReversal()
    {
        var result = new int[SampleCount];
        for (var i = 1; i < SampleCount; i++)
            result[i] = (result[i >> 1] >> 1) | ((i & 1) * (SampleCount / 2));
        return result;
    }

    public SpectrumFrame? AddSample(double sample)
    {
        _pcm[_write] = (float)Math.Clamp(double.IsFinite(sample) ? sample : 0, -1, 1);
        _write = (_write + 1) % SampleCount;
        if (++_pending < _hop) return null;
        _pending = 0;
        for (var i = 0; i < SampleCount; i++)
            _samples[Reversed[i]] = new Vector2(_pcm[(_write + i) % SampleCount] * Hann[i], 0);
        for (var length = 2; length <= SampleCount; length <<= 1)
        {
            var half = length / 2;
            for (var j = 0; j < half; j++)
            {
                var w = Twiddles[j * (SampleCount / length)];
                for (var i = j; i < SampleCount; i += length)
                {
                    var even = _samples[i];
                    var odd = _samples[i + half];
                    var rotated = new Vector2(w.X * odd.X - w.Y * odd.Y, w.X * odd.Y + w.Y * odd.X);
                    _samples[i] = even + rotated;
                    _samples[i + half] = even - rotated;
                }
            }
        }

        // Upstream's coarse six-band path packs DC/Nyquist together and weights
        // the first bin crossing each edge by the width of the preceding band.
        var levels = new double[BandCount];
        var band = 0;
        for (var bin = 0; bin < SampleCount / 2; bin++)
        {
            var hz = bin * _binWidth;
            double magnitude = bin == 0
                ? float.Hypot(_samples[0].X, _samples[SampleCount / 2].X)
                : float.Hypot(_samples[bin].X, _samples[bin].Y);
            var low = Edges[band];
            var high = Edges[band + 1];
            if (hz < low || hz > high)
            {
                while (band < BandCount - 1 && hz >= Edges[band + 1]) band++;
                magnitude /= high - low;
            }
            levels[band] += magnitude;
        }

        var scale = 4d / SampleCount * 2 * Math.Sqrt(8d / 3);
        for (var i = 0; i < BandCount; i++)
        {
            var value = Math.Min(1, Math.Pow(levels[i] * scale * Gains[i] / Calibration[i], Exponents[i]));
            _slow[i] += (value - _slow[i]) * _slowAlpha;
            var reference = .5 * _previous[i] + .3 * _older[i] + .2 * _oldest[i];
            var rise = Math.Max(0, value - reference);
            var gate = Math.Clamp(1 - _slow[i] / .35, 0, 1);
            var shape = Math.Clamp((rise - .01) / .05, 0, 1);
            levels[i] = (float)Math.Min(1, value + (Punch[i] * gate + 1.05) * rise * shape);
            _oldest[i] = _older[i];
            _older[i] = _previous[i];
            _previous[i] = value;
        }
        return new(AudioCaptureMode.ProcessLoopback, levels, ++_revision);
    }
}
