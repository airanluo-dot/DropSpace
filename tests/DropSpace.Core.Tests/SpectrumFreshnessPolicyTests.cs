using DropSpace.Core.Lyrics;
using DropSpace.Core.Media;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class SpectrumFreshnessPolicyTests
{
    private static SpectrumFrame Loud => new(AudioCaptureMode.ProcessLoopback, [1, .8, .6, .4, .2, .1], 17);

    [TestMethod]
    public void MissingPacketsFadeMeterAndHaloWithoutInventingSilencePacketsOrRevisions()
    {
        var frame = Loud;
        var envelope = new LyricsGlowEnvelope();
        var previous = 1d;
        for (var tick = 0; tick <= 300; tick++)
        {
            var current = SpectrumFreshnessPolicy.Apply(frame, TimeSpan.FromMilliseconds(tick * 33));
            Assert.AreEqual(frame.Revision, current.Revision);
            Assert.AreEqual(AudioCaptureMode.ProcessLoopback, current.CaptureMode);
            Assert.IsTrue(current.Bands[0] <= previous);
            previous = current.Bands[0];
            envelope.Advance(true, current.Bands.Average(), TimeSpan.FromMilliseconds(33), bands: current.Bands);
        }
        Assert.AreEqual(0d, previous);
        Assert.AreEqual(.08, envelope.Brightness, .000001);
        Assert.IsTrue(envelope.Bands.All(band => band < .000001));
        CollectionAssert.AreEqual(new[] { 1d, .8, .6, .4, .2, .1 }, frame.Bands.ToArray());
    }

    [TestMethod]
    public void FreshDeliveryResumesEvenWhenCaptureRestartsItsRevisionCounter()
    {
        var first = Loud;
        Assert.AreSame(first, SpectrumFreshnessPolicy.Apply(first, TimeSpan.FromMilliseconds(100)));
        Assert.IsTrue(SpectrumFreshnessPolicy.Apply(first, TimeSpan.FromSeconds(10)).Bands.All(band => band == 0));
        var restarted = new SpectrumFrame(AudioCaptureMode.ProcessLoopback, [.2, .1, .8, .3, .7, .1], 1);
        Assert.AreSame(restarted, SpectrumFreshnessPolicy.Apply(restarted, TimeSpan.Zero));
        Assert.AreSame(first, SpectrumFreshnessPolicy.Apply(first, TimeSpan.FromMilliseconds(149)));
    }

    [TestMethod]
    public void RealSilentPacketsAndUnavailableCaptureDoNotRetainOldEnergy()
    {
        var silent = new SpectrumFrame(AudioCaptureMode.ProcessLoopback, new double[6], 18);
        Assert.IsTrue(SpectrumFreshnessPolicy.Apply(silent, TimeSpan.Zero).Bands.All(band => band == 0));
        Assert.AreSame(SpectrumFrame.Empty, SpectrumFreshnessPolicy.Apply(SpectrumFrame.Empty, TimeSpan.FromDays(3)));
        Assert.IsTrue(SpectrumFreshnessPolicy.Apply(Loud, TimeSpan.FromMilliseconds(-1)).Bands.All(band => band == 0));
        var invalid = Loud with { Bands = [double.NaN, double.PositiveInfinity, -1, 2, .5, 0] };
        Assert.IsTrue(SpectrumFreshnessPolicy.Apply(invalid, TimeSpan.FromMilliseconds(300)).Bands.All(value =>
            double.IsFinite(value) && value is >= 0 and <= 1));
    }
}
