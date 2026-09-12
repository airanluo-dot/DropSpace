using DropSpace.Core.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class MediaPlaybackClockTests
{
    [TestMethod]
    public void MissingWindowsEpochTimelineDoesNotJumpToEndAndMetadataDoesNotRewind()
    {
        var time = new ManualTime(); var clock = new MediaPlaybackClock(time);
        var session = MediaSessionSnapshot.Empty with { TrackTitle = "Test", PlaybackState = MediaPlaybackState.Playing,
            Timeline = new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 1, DateTimeOffset.FromFileTime(0)) };
        clock.Update(session); time.Advance(4); clock.Update(session);
        Assert.AreEqual(4, clock.Position.TotalSeconds, 0.001);
        Assert.IsTrue(clock.IsEstimated);
        time.Advance(2); Assert.AreEqual(6, clock.Position.TotalSeconds, 0.001);
        clock.Update(session with { PlaybackState = MediaPlaybackState.Paused }); time.Advance(5);
        Assert.AreEqual(6, clock.Position.TotalSeconds, 0.001);
        clock.Update(session); time.Advance(1); Assert.AreEqual(7, clock.Position.TotalSeconds, 0.001);
    }

    [TestMethod]
    public void NativeSeekAndTrackChangeReplaceTheInterpolationAnchor()
    {
        var time = new ManualTime(); var clock = new MediaPlaybackClock(time);
        var session = MediaSessionSnapshot.Empty with { TrackTitle = "First", PlaybackState = MediaPlaybackState.Playing,
            Timeline = new(TimeSpan.FromSeconds(10), TimeSpan.Zero, TimeSpan.FromSeconds(120), 1, time.GetUtcNow()) };
        clock.Update(session); time.Advance(2);
        Assert.AreEqual(12, clock.Position.TotalSeconds, 0.001);
        Assert.IsFalse(clock.IsEstimated);
        clock.Update(session with { Timeline = session.Timeline with { Position = TimeSpan.FromSeconds(60), LastUpdated = time.GetUtcNow() } });
        Assert.AreEqual(60, clock.Position.TotalSeconds, 0.001);
        clock.Update(session with { TrackTitle = "Second", Timeline = session.Timeline with { Position = TimeSpan.Zero, LastUpdated = time.GetUtcNow() } });
        Assert.AreEqual(0, clock.Position.TotalSeconds, 0.001);
    }
    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero).AddTicks(_ticks);
        public void Advance(int seconds) => _ticks += seconds * TimeSpan.TicksPerSecond;
    }
}
