namespace DropSpace.Core.Media;

/// <summary>Expire a stopped packet stream; this never synthesizes new audio energy.</summary>
public static class SpectrumFreshnessPolicy
{
    private static readonly double[] SilentBands = new double[SpectrumAnalyzer.BandCount];

    public static SpectrumFrame Apply(SpectrumFrame frame, TimeSpan age)
    {
        if (frame.CaptureMode != AudioCaptureMode.ProcessLoopback) return frame;
        var seconds = age.TotalSeconds;
        // Several normal 2048-sample packets may elapse before UI delivery. Only
        // a continued absence starts the smooth decline shared by meter and halo.
        if (seconds >= 0 && seconds <= 0.15) return frame;
        if (!double.IsFinite(seconds) || seconds < 0 || seconds >= 0.6)
            return frame with { Bands = SilentBands };
        var t = (seconds - 0.15) / 0.45;
        var gain = 1 - t * t * (3 - 2 * t);
        return frame with { Bands = frame.Bands.Select(value =>
            double.IsFinite(value) ? Math.Clamp(value, 0, 1) * gain : 0).ToArray() };
    }
}
