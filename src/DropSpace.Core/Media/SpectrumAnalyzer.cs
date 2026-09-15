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
    private readonly Complex[] _samples = new Complex[SampleCount];
    private readonly double[] _smoothed = new double[BandCount];
    private readonly int _sampleRate;
    private int _count;
    private long _revision;
    public SpectrumAnalyzer(int sampleRate) => _sampleRate = sampleRate;

    public SpectrumFrame? AddSample(double sample)
    {
        _samples[_count] = new Complex(Math.Clamp(double.IsFinite(sample) ? sample : 0, -1, 1) *
            (0.5 - 0.5 * Math.Cos(2 * Math.PI * _count / (SampleCount - 1))), 0);
        if (++_count < SampleCount) return null;
        _count = 0;
        for (int index = 1, reverse = 0; index < SampleCount; index++)
        {
            var bit = SampleCount >> 1;
            for (; (reverse & bit) != 0; bit >>= 1) reverse ^= bit;
            reverse ^= bit;
            if (index < reverse) (_samples[index], _samples[reverse]) = (_samples[reverse], _samples[index]);
        }
        for (var length = 2; length <= SampleCount; length <<= 1)
        {
            var step = Complex.FromPolarCoordinates(1, -2 * Math.PI / length);
            for (var offset = 0; offset < SampleCount; offset += length)
            {
                var phase = Complex.One;
                for (var index = 0; index < length / 2; index++)
                {
                    var even = _samples[offset + index];
                    var odd = _samples[offset + index + length / 2] * phase;
                    _samples[offset + index] = even + odd;
                    _samples[offset + index + length / 2] = even - odd;
                    phase *= step;
                }
            }
        }
        for (var band = 0; band < BandCount; band++)
        {
            var low = 60 * Math.Pow(250, (double)band / BandCount);
            var high = 60 * Math.Pow(250, (double)(band + 1) / BandCount);
            var begin = Math.Clamp((int)(low * SampleCount / _sampleRate), 1, SampleCount / 2 - 1);
            var end = Math.Clamp((int)Math.Ceiling(high * SampleCount / _sampleRate), begin + 1, SampleCount / 2);
            double peak = 0;
            for (var bin = begin; bin < end; bin++) peak = Math.Max(peak, _samples[bin].Magnitude * 4 / SampleCount);
            var level = Math.Clamp((20 * Math.Log10(Math.Max(peak, 0.00001)) + 70) / 70, 0, 1);
            _smoothed[band] += (level - _smoothed[band]) * (level > _smoothed[band] ? 0.8 : 0.25);
        }
        return new(AudioCaptureMode.ProcessLoopback, _smoothed.ToArray(), ++_revision);
    }
}
