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
        for (var i = 0; i < 60; i++) envelope.Advance(false, 1, TimeSpan.FromSeconds(1d / 60));
        Assert.AreEqual(0d, envelope.Brightness);
    }

    [TestMethod]
    public void FrameRateDoesNotChangeEnvelopeForConstantInput()
    {
        var sixty = new LyricsGlowEnvelope();
        var thirty = new LyricsGlowEnvelope();
        for (var i = 0; i < 60; i++) sixty.Advance(true, 0.4, TimeSpan.FromSeconds(1d / 60));
        for (var i = 0; i < 30; i++) thirty.Advance(true, 0.4, TimeSpan.FromSeconds(1d / 30));
        Assert.AreEqual(sixty.Brightness, thirty.Brightness, 0.000001);
    }

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
