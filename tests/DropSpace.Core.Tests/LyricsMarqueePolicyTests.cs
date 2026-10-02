using DropSpace.Core.Lyrics;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class LyricsMarqueePolicyTests
{
    private static LyricsMarqueeLayout LongLine => new("song", 0, "A complete long translation", 14, 1, 400, 160, true, false);

    [TestMethod]
    public void LongTranslationReachesItsTailAndHoldsBothEnds()
    {
        var session = new LyricsMarqueeSession();
        Assert.AreEqual(0d, session.Update(LongLine, TimeSpan.FromSeconds(30)));
        Assert.AreEqual(0d, session.Update(LongLine, TimeSpan.FromSeconds(31)));
        Assert.AreEqual(24d, session.Update(LongLine, TimeSpan.FromSeconds(32.2)), 0.0001);
        Assert.AreEqual(240d, session.Update(LongLine, TimeSpan.FromSeconds(41.5)), 0.0001);
        Assert.AreEqual(240d, session.Update(LongLine, TimeSpan.FromSeconds(42)), 0.0001);
        Assert.AreEqual(0d, session.Update(LongLine, TimeSpan.FromSeconds(42.5)));
    }

    [TestMethod]
    public void PrimaryAndSecondaryOverflowAreIndependentForAllLengthCombinations()
    {
        foreach (var primaryWidth in new[] { 80d, 400d })
        foreach (var secondaryWidth in new[] { 80d, 400d })
        {
            var primary = new LyricsMarqueeSession();
            var secondary = new LyricsMarqueeSession();
            var original = LongLine with { Text = "Original", TextWidth = primaryWidth };
            var translated = LongLine with { TextWidth = secondaryWidth };
            primary.Update(original, TimeSpan.Zero);
            secondary.Update(translated, TimeSpan.Zero);
            Assert.AreEqual(primaryWidth > 160 ? 67.2 : 0, primary.Update(original, TimeSpan.FromSeconds(4)), .0001);
            Assert.AreEqual(secondaryWidth > 160 ? 67.2 : 0, secondary.Update(translated, TimeSpan.FromSeconds(4)), .0001);
        }
    }

    [TestMethod]
    public void ContentTrackLineSizeDpiWidthAndMotionChangesEachRestartLeadingHold()
    {
        var changes = new[]
        {
            LongLine with { TrackIdentity = "next" },
            LongLine with { LineStartTicks = TimeSpan.FromSeconds(12).Ticks },
            LongLine with { Text = "AI · A complete long translation" },
            LongLine with { Text = "New progressive translation", TextWidth = 420 },
            LongLine with { FontSize = 24.5 },
            LongLine with { RasterizationScale = 1.5 },
            LongLine with { ViewportWidth = 180 },
            LongLine with { Enabled = false },
            LongLine with { ReducedMotion = true },
        };
        foreach (var changed in changes)
        {
            var session = new LyricsMarqueeSession();
            session.Update(LongLine, TimeSpan.Zero);
            Assert.IsTrue(session.Update(LongLine, TimeSpan.FromSeconds(4)) > 0);
            Assert.AreEqual(0d, session.Update(changed, TimeSpan.FromSeconds(4)));
            Assert.AreEqual(0d, session.Update(changed, TimeSpan.FromSeconds(5)));
        }
    }

    [TestMethod]
    public void PauseFreezesAndBackwardSeekOrReloadRestarts()
    {
        var session = new LyricsMarqueeSession();
        session.Update(LongLine, TimeSpan.Zero);
        var paused = session.Update(LongLine, TimeSpan.FromSeconds(5));
        Assert.AreEqual(paused, session.Update(LongLine, TimeSpan.FromSeconds(5)));
        Assert.AreEqual(0d, session.Update(LongLine, TimeSpan.FromSeconds(2)));
        Assert.IsTrue(session.Update(LongLine, TimeSpan.FromSeconds(5)) > 0);
        session.Reset();
        Assert.AreEqual(0d, session.Update(LongLine, TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public void FontAndDpiMatrixPreservesDipBoundsAndReachesEveryTail()
    {
        // Geometry contract only: native glyph measurement at these scales is a Windows smoke check.
        foreach (var size in new[] { 12d, 16d, 17.375, 28d })
        foreach (var scale in new[] { 1d, 1.25, 1.5, 2d })
        {
            var settings = new DropSpace.Core.Models.AppSettings { Lyrics = new() { FontSize = size } }.Validate();
            Assert.AreEqual(size * .875, settings.Lyrics.TranslationFontSize);
            var layout = LongLine with { FontSize = settings.Lyrics.TranslationFontSize, RasterizationScale = scale };
            var session = new LyricsMarqueeSession();
            session.Update(layout, TimeSpan.Zero);
            var maximum = 0d;
            for (var tick = 0; tick <= 124; tick++)
            {
                var offset = session.Update(layout, TimeSpan.FromSeconds(tick / 10d));
                Assert.IsTrue(offset >= 0 && offset <= layout.TextWidth - layout.ViewportWidth);
                maximum = Math.Max(maximum, offset);
            }
            Assert.AreEqual(layout.TextWidth - layout.ViewportWidth, maximum);
        }
    }

    [TestMethod]
    public void DisabledReducedMotionShortOrUnmeasuredTextDoesNotScroll()
    {
        foreach (var layout in new[]
        {
            LongLine with { Enabled = false }, LongLine with { ReducedMotion = true },
            LongLine with { TextWidth = 80 }, LongLine with { TextWidth = 160 },
            LongLine with { ViewportWidth = 0 }, LongLine with { TextWidth = double.NaN },
            LongLine with { ViewportWidth = double.PositiveInfinity },
        })
        {
            var session = new LyricsMarqueeSession();
            session.Update(layout, TimeSpan.Zero);
            Assert.AreEqual(0d, session.Update(layout, TimeSpan.FromSeconds(8)));
        }
    }
}
