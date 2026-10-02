using DropSpace.Core.Lyrics;
using DropSpace.Core.Media;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class LyricsGlowHandoffTests
{
    private static MediaSessionSnapshot Session => MediaSessionSnapshot.Empty with
    {
        SessionId = "player-session", SourceAppUserModelId = "player", TrackTitle = "Song A",
        PlaybackState = MediaPlaybackState.Playing,
        Timeline = new(TimeSpan.FromSeconds(5), TimeSpan.Zero, TimeSpan.FromSeconds(180), 1, DateTimeOffset.UtcNow),
    };

    private static LyricsGlowEnvelope Lit()
    {
        var envelope = new LyricsGlowEnvelope();
        for (var i = 0; i < 60; i++)
            envelope.Advance(true, .6, TimeSpan.FromMilliseconds(33), bands: [.8, .2, .1, .9, .5, .4], simplifiedGlow: true);
        return envelope;
    }

    [TestMethod]
    public void NewDpiEnvelopeRetainsDeepValuesWithoutOwningOldWindowState()
    {
        var old = Lit();
        var snapshot = old.Capture();
        var handoff = new LyricsGlowHandoff("monitor", Session, snapshot);
        old.Advance(false, 0, TimeSpan.FromSeconds(5)); // The old controller's disposal path.
        Assert.AreEqual(0d, old.Brightness);
        Assert.IsNull(handoff.Evaluate("monitor", Session, true, false, false, TimeSpan.FromMilliseconds(33)));
        var continued = handoff.Evaluate("monitor", Session with
            { Artwork = [1, 2], Timeline = Session.Timeline with { Position = TimeSpan.FromSeconds(7) } },
            true, true, true, TimeSpan.FromMilliseconds(99));
        Assert.AreEqual(snapshot, continued);
        var rebuilt = new LyricsGlowEnvelope();
        rebuilt.Restore(continued!.Value);
        Assert.AreEqual(snapshot, rebuilt.Capture());
        rebuilt.Advance(true, 0, TimeSpan.FromMilliseconds(33), true, [0, 0, 0, 0, 0, 0], simplifiedGlow: false);
        Assert.AreEqual(snapshot.Phase, rebuilt.Phase, "Current reduced-motion preference controls the new frame.");
        Assert.IsTrue(rebuilt.Brightness < snapshot.Brightness);
        Assert.IsTrue(rebuilt.Simplification < snapshot.Simplification);
        Assert.IsNull(handoff.Evaluate("monitor", Session, true, true, true, TimeSpan.FromMilliseconds(120)));
    }

    [TestMethod]
    public void OffPauseHighContrastOrTrackChangesCannotReviveATransferWhenReversed()
    {
        foreach (var changed in new[] { Session with { PlaybackState = MediaPlaybackState.Paused },
            Session with { TrackTitle = "Song B" }, Session with { SessionId = "different-player" },
            Session with { Timeline = Session.Timeline with { End = TimeSpan.FromSeconds(200) } } })
        {
            var handoff = new LyricsGlowHandoff("monitor", Session, Lit().Capture());
            Assert.IsNull(handoff.Evaluate("monitor", changed, true, false, false, TimeSpan.FromMilliseconds(33)));
            Assert.IsNull(handoff.Evaluate("monitor", Session, true, true, true, TimeSpan.FromMilliseconds(66)));
        }
        var off = new LyricsGlowHandoff("monitor", Session, Lit().Capture());
        Assert.IsNull(off.Evaluate("monitor", Session, false, false, false, TimeSpan.FromMilliseconds(33)));
        Assert.IsNull(off.Evaluate("monitor", Session, true, true, true, TimeSpan.FromMilliseconds(66)));
    }

    [TestMethod]
    public void IneligibleReadyLayoutWrongMonitorAndExpiredTransferAreFinalDecisions()
    {
        foreach (var test in new[] { ("monitor", true, false, .1), ("other-monitor", false, true, .1), ("monitor", false, true, 3d) })
        {
            var handoff = new LyricsGlowHandoff("monitor", Session, Lit().Capture());
            Assert.IsNull(handoff.Evaluate(test.Item1, Session, true, test.Item2, test.Item3, TimeSpan.FromSeconds(test.Item4)));
            Assert.IsNull(handoff.Evaluate("monitor", Session, true, true, true, TimeSpan.FromMilliseconds(120)));
        }
    }
}
