using DropSpace.Core.Lyrics;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class LyricsGlowEnvelopeTests
{
    [TestMethod]
    public void FadeCanReverseWithoutResettingBrightness()
    {
        var envelope = new LyricsGlowEnvelope();
        var on = envelope.Advance(true, 1, TimeSpan.FromMilliseconds(100));
        var fading = envelope.Advance(false, 1, TimeSpan.FromMilliseconds(16));
        Assert.IsTrue(fading > 0 && fading < on);
        var resumed = envelope.Advance(true, 1, TimeSpan.FromMilliseconds(16));
        Assert.IsTrue(resumed > fading && resumed < 0.8);
    }

    [TestMethod]
    public void OffEventuallyHasNoResidualColor()
    {
        var envelope = new LyricsGlowEnvelope();
        envelope.Advance(true, 1, TimeSpan.FromSeconds(1));
        // A retained line disappearing is a gradual dimming, not a one-frame cut.
        var lit = envelope.Brightness;
        var firstFrame = envelope.Advance(false, 1, TimeSpan.FromSeconds(1d / 60));
        Assert.IsTrue(firstFrame > lit * 0.9 && firstFrame < lit);
        for (var i = 0; i < 120; i++) envelope.Advance(false, 1, TimeSpan.FromSeconds(1d / 60));
        Assert.AreEqual(0d, envelope.Brightness);
    }

    [TestMethod]
    public void SilenceDimsSmoothlyToRestrainedBaselineWhileMusicRemainsEligible()
    {
        var envelope = new LyricsGlowEnvelope();
        envelope.Advance(true, 1, TimeSpan.FromSeconds(1));
        var loud = envelope.Brightness;
        var dimming = envelope.Advance(true, 0, TimeSpan.FromMilliseconds(33));
        Assert.IsTrue(dimming < loud && dimming > loud * 0.8);
        for (var index = 0; index < 120; index++) envelope.Advance(true, 0, TimeSpan.FromMilliseconds(33));
        Assert.AreEqual(0.08, envelope.Brightness, 0.00001);
        Assert.IsTrue(loud < 0.5, "The maximum envelope must remain restrained.");
    }

    [TestMethod]
    public void PauseAndReducedMotionFreezeShapeAndPhaseButKeepOpacityContinuous()
    {
        var envelope = new LyricsGlowEnvelope();
        envelope.Advance(true, 1, TimeSpan.FromMilliseconds(100), bands: [1, 0.2, 0.4, 0.3, 0.8, 0.1]);
        var phase = envelope.Phase;
        var bands = envelope.Bands.ToArray();
        var brightness = envelope.Brightness;
        envelope.Advance(false, 0, TimeSpan.FromMilliseconds(33), bands: [0, 1, 0, 1, 0, 1]);
        Assert.AreEqual(phase, envelope.Phase);
        CollectionAssert.AreEqual(bands, envelope.Bands.ToArray());
        Assert.IsTrue(envelope.Brightness > 0 && envelope.Brightness < brightness);
        envelope.Advance(true, 0.5, TimeSpan.FromMilliseconds(33), reducedMotion: true, bands: [1, 1, 1, 1, 1, 1]);
        Assert.AreEqual(phase, envelope.Phase);
        CollectionAssert.AreEqual(bands, envelope.Bands.ToArray());
        envelope.Advance(true, 1, TimeSpan.FromMilliseconds(33), bands: [1, 1, 1, 1, 1, 1]);
        Assert.IsTrue(envelope.Phase > phase);
        Assert.IsTrue(envelope.Bands[1] > bands[1]);
    }

    [TestMethod]
    public void SixBandMotionUsesRealInputAndSanitizesMissingOrInvalidBands()
    {
        var envelope = new LyricsGlowEnvelope();
        envelope.Advance(true, 0.5, TimeSpan.FromMilliseconds(100), bands: [1, double.NaN, -1, 2, double.PositiveInfinity]);
        Assert.IsTrue(envelope.Bands[0] > 0 && envelope.Bands[0] < 1);
        Assert.AreEqual(envelope.Bands[0], envelope.Bands[3]);
        foreach (var index in new[] { 1, 2, 4, 5 }) Assert.AreEqual(0d, envelope.Bands[index]);
        var loud = envelope.Bands[0];
        envelope.Advance(true, 0, TimeSpan.FromMilliseconds(100));
        Assert.IsTrue(envelope.Bands[0] > 0 && envelope.Bands[0] < loud);
    }

    [TestMethod]
    public void FrameRateDoesNotChangeEnvelopeForConstantInput()
    {
        var sixty = new LyricsGlowEnvelope();
        var thirty = new LyricsGlowEnvelope();
        var bands = new[] { 0.8, 0.3, 0.1, 0.6, 0.9, 0.2 };
        for (var i = 0; i < 60; i++) sixty.Advance(true, 0.4, FrameDuration(i, 60), bands: bands);
        for (var i = 0; i < 30; i++) thirty.Advance(true, 0.4, FrameDuration(i, 30), bands: bands);
        Assert.AreEqual(sixty.Brightness, thirty.Brightness, 0.000001);
        Assert.AreEqual(sixty.Phase, thirty.Phase, 0.000001);
        for (var index = 0; index < bands.Length; index++)
            Assert.AreEqual(sixty.Bands[index], thirty.Bands[index], 0.000001);
    }

    private static TimeSpan FrameDuration(int index, int frameRate) => TimeSpan.FromTicks(
        (index + 1L) * TimeSpan.TicksPerSecond / frameRate - index * TimeSpan.TicksPerSecond / frameRate);

    [TestMethod]
    public void InvalidAudioCannotProduceInvalidBrightness()
    {
        var envelope = new LyricsGlowEnvelope();
        foreach (var energy in new[] { double.NaN, double.PositiveInfinity, -1, 2 })
        {
            var value = envelope.Advance(true, energy, TimeSpan.FromMilliseconds(16));
            Assert.IsTrue(double.IsFinite(value) && value is >= 0 and <= 0.8);
        }
        var before = envelope.Brightness;
        Assert.AreEqual(before, envelope.Advance(false, 0, TimeSpan.FromSeconds(-1)));
    }
}
