namespace DropSpace.Core.Audio;

public enum SpectrumCaptureMode
{
    Unavailable,
    ProcessLoopback,
    SystemFallback,
}

public sealed record SpectrumFrame(
    IReadOnlyList<float> Bars,
    SpectrumCaptureMode CaptureMode,
    DateTimeOffset Timestamp)
{
    public static SpectrumFrame Empty { get; } = new([0, 0, 0, 0, 0, 0], SpectrumCaptureMode.Unavailable, DateTimeOffset.MinValue);
}

public interface IAudioSpectrumService : IAsyncDisposable
{
    event EventHandler<SpectrumFrame>? FrameChanged;

    SpectrumFrame Current { get; }

    Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
}

public sealed class SpectrumAnalyzer
{
    private readonly float[] _levels;

    public SpectrumAnalyzer(int barCount = 32)
    {
        if (barCount is < 4 or > 48) throw new ArgumentOutOfRangeException(nameof(barCount));
        _levels = new float[barCount];
    }

    public IReadOnlyList<float> Analyze(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty) return _levels;
        var rms = 0d;
        foreach (var sample in samples) rms += sample * sample;
        rms = Math.Sqrt(rms / samples.Length);
        var peak = Math.Clamp((float)(rms * 8), 0, 1);
        for (var index = 0; index < _levels.Length; index++)
        {
            var response = peak * (0.78f + 0.22f * MathF.Sin(index * 0.7f + 1.2f));
            _levels[index] = Math.Clamp(_levels[index] * 0.72f + response * 0.28f, 0, 1);
        }

        return _levels;
    }
}
