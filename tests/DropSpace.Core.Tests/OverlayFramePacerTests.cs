using DropSpace.Core.Overlay;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class OverlayFramePacerTests
{
    private const long Frequency = 10_000_000;

    [TestMethod]
    public void FractionalRatesKeepTheirPhaseAcrossTwoMinuteStreams()
    {
        foreach (var sourceHz in new[] { 240d, 360d })
        foreach (var targetHz in new[] { 59.94d, 60_000d / 1_001 })
        {
            var frames = Simulate(sourceHz, targetHz, seconds: 120);
            Assert.IsTrue(Math.Abs(frames.Count - targetHz * 120) <= 1,
                $"Frame count drift for {sourceHz} -> {targetHz}: {frames.Count}");
            var period = Frequency / targetHz;
            var phaseError = frames[^1] - (frames.Count - 1) * period;
            Assert.IsTrue(phaseError >= -0.24 * period - 1);
            Assert.IsTrue(phaseError <= Frequency / sourceHz + 1);
            Assert.AreEqual(targetHz, new OverlayFramePacer(targetHz, Frequency).RefreshRateHz);
        }
    }

    [TestMethod]
    public void IntegerRatiosKeepSteadyIntervalsAfterTheInitialCallback()
    {
        foreach (var rates in new[] { (240d, 60d), (240d, 120d), (240d, 240d), (360d, 60d), (360d, 120d) })
        {
            var frames = Simulate(rates.Item1, rates.Item2, seconds: 120);
            var period = Frequency / rates.Item2;
            // The first deadline may use the early allowance; later integer-ratio gaps stay regular.
            for (var index = 2; index < frames.Count; index++)
                Assert.AreEqual(period, frames[index] - (double)frames[index - 1], 1,
                    $"Irregular steady gap for {rates.Item1} -> {rates.Item2}, frame {index}");
        }
    }

    [TestMethod]
    public void EarlyCallbackJitterDoesNotHalveTheFrameRate()
    {
        var pacer = new OverlayFramePacer(240, Frequency);
        Assert.IsTrue(pacer.ShouldRender(0));
        for (var index = 1; index <= 2_400; index++)
        {
            var jitter = index % 2 == 0 ? -0.20 : 0.20;
            var timestamp = (long)Math.Round((index + jitter) * Frequency / 240d);
            Assert.IsTrue(pacer.ShouldRender(timestamp), $"Skipped jittered callback {index}");
            Assert.IsFalse(pacer.ShouldRender(timestamp));
        }
    }

    [TestMethod]
    public void LongStallsCoalesceAndReturnToTheOriginalPhase()
    {
        var pacer = new OverlayFramePacer(120, Frequency);
        Assert.IsTrue(pacer.ShouldRender(0));
        Assert.IsTrue(pacer.ShouldRender(Frequency));
        Assert.IsFalse(pacer.ShouldRender(Frequency));
        Assert.IsFalse(pacer.ShouldRender(Frequency + 1));

        var nextEarlyBoundary = Frequency + (long)Math.Floor(Frequency / 120d * 0.76);
        Assert.IsFalse(pacer.ShouldRender(nextEarlyBoundary - 1));
        Assert.IsTrue(pacer.ShouldRender(nextEarlyBoundary + 2));
        Assert.IsFalse(pacer.ShouldRender(nextEarlyBoundary + 2));
    }

    [TestMethod]
    public void RateChangesRestartOnceAndSameRateRetainsItsDeadline()
    {
        var pacer = new OverlayFramePacer(60, Frequency);
        Assert.IsTrue(pacer.ShouldRender(0));
        Assert.IsFalse(pacer.ShouldRender(Frequency / 240));
        pacer.SetRefreshRate(60);
        Assert.IsFalse(pacer.ShouldRender(Frequency / 120));

        pacer.SetRefreshRate(120);
        Assert.AreEqual(120d, pacer.RefreshRateHz);
        Assert.IsTrue(pacer.ShouldRender(Frequency / 120));
        Assert.IsFalse(pacer.ShouldRender(Frequency / 120));
    }

    [TestMethod]
    public void ResetAndClockRegressionStartFreshEpochs()
    {
        var pacer = new OverlayFramePacer(240, Frequency);
        Assert.IsTrue(pacer.ShouldRender(Frequency));
        Assert.IsFalse(pacer.ShouldRender(Frequency + 1));
        pacer.Reset();
        Assert.IsTrue(pacer.ShouldRender(0));
        Assert.IsFalse(pacer.ShouldRender(0));
        Assert.IsTrue(pacer.ShouldRender(Frequency));
        Assert.IsTrue(pacer.ShouldRender(0));
        Assert.IsFalse(pacer.ShouldRender(0));
    }

    [TestMethod]
    public void LargeTimestampsKeepRelativeClockPrecision()
    {
        var pacer = new OverlayFramePacer(240, Frequency);
        const long epoch = 9_007_199_254_740_992;
        Assert.IsTrue(pacer.ShouldRender(epoch));
        for (var index = 1; index <= 2_400; index++)
            Assert.IsTrue(pacer.ShouldRender(epoch + (long)Math.Round(index * Frequency / 240d)));
    }

    [TestMethod]
    public void UnknownAndUnrepresentableRatesFallBackToSixtyHz()
    {
        foreach (var rate in new double?[] { null, 0, -1, double.NaN, double.PositiveInfinity,
                     double.NegativeInfinity, double.Epsilon, double.MaxValue })
        {
            var pacer = new OverlayFramePacer(rate, Frequency);
            Assert.AreEqual(60d, pacer.RefreshRateHz);
            Assert.IsTrue(pacer.ShouldRender(0));
            Assert.IsFalse(pacer.ShouldRender(Frequency / 120));
            Assert.IsTrue(pacer.ShouldRender(Frequency / 60));
        }
    }

    [TestMethod]
    public void TimestampFrequencyMustBePositive()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new OverlayFramePacer(60, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new OverlayFramePacer(60, -1));
    }

    private static List<long> Simulate(double sourceHz, double targetHz, int seconds)
    {
        var pacer = new OverlayFramePacer(targetHz, Frequency);
        var frames = new List<long>();
        for (var index = 0; index < sourceHz * seconds; index++)
        {
            var timestamp = (long)Math.Round(index * Frequency / sourceHz);
            if (pacer.ShouldRender(timestamp)) frames.Add(timestamp);
        }
        return frames;
    }
}
