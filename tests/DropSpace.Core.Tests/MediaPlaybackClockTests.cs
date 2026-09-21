using DropSpace.Core.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class MediaPlaybackClockTests
{
    [TestMethod]
    public void ResumeWithOldNativeTimestampDoesNotCountPausedTimeOrRewindOnNextPosition()
    {
        var time = new ManualTime();
        var clock = new MediaPlaybackClock(time);
        var paused = MediaSessionSnapshot.Empty with
        {
            TrackTitle = "Apple track", PlaybackState = MediaPlaybackState.Paused,
            Timeline = new(TimeSpan.FromSeconds(95), TimeSpan.Zero, TimeSpan.FromSeconds(227), 1, time.GetUtcNow()),
            LastUpdated = time.GetUtcNow(),
        };
        clock.Update(paused);
        time.Advance(28);
        var resumed = paused with { PlaybackState = MediaPlaybackState.Playing, LastUpdated = time.GetUtcNow() };
        clock.Update(resumed);
        Assert.AreEqual(95, clock.Position.TotalSeconds, 0.001);
        time.Advance(0.5);
        clock.Update(resumed with { LastUpdated = time.GetUtcNow() });
        Assert.AreEqual(95.5, clock.Position.TotalSeconds, 0.001);
        time.Advance(0.5);
        clock.Update(resumed with
        {
            LastUpdated = time.GetUtcNow(),
            Timeline = resumed.Timeline with { Position = TimeSpan.FromSeconds(96), LastUpdated = time.GetUtcNow() },
        });
        Assert.AreEqual(96, clock.Position.TotalSeconds, 0.001);
    }

    [TestMethod]
    public void DelayedResumeDeliveryOnlyInterpolatesAfterResumeObservation()
    {
        var time = new ManualTime();
        var clock = new MediaPlaybackClock(time);
        var session = MediaSessionSnapshot.Empty with
        {
            TrackTitle = "Apple track", PlaybackState = MediaPlaybackState.Paused,
            Timeline = new(TimeSpan.FromSeconds(95), TimeSpan.Zero, TimeSpan.FromSeconds(227), 1, time.GetUtcNow()),
            LastUpdated = time.GetUtcNow(),
        };
        clock.Update(session);
        time.Advance(28);
        var observedAt = time.GetUtcNow();
        time.Advance(2);
        clock.Update(session with { PlaybackState = MediaPlaybackState.Playing, LastUpdated = observedAt });
        Assert.AreEqual(97, clock.Position.TotalSeconds, 0.001);
    }

    [TestMethod]
    public void EstimatedPlaybackWithVisibleFramesDoesNotRewindAfterSparseMetadataEvents()
    {
        var time = new ManualTime();
        var clock = new MediaPlaybackClock(time);
        var session = MediaSessionSnapshot.Empty with
        {
            TrackTitle = "NetEase track", PlaybackState = MediaPlaybackState.Playing,
            Timeline = new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 1, DateTimeOffset.FromFileTime(0)),
            LastUpdated = time.GetUtcNow(),
        };
        clock.Update(session);
        for (var second = 1; second <= 20; second++)
        {
            time.Advance(1);
            Assert.AreEqual(second, clock.Position.TotalSeconds, 0.001);
        }
        clock.Update(session with { PlaybackState = MediaPlaybackState.Paused });
        Assert.AreEqual(20, clock.Position.TotalSeconds, 0.001);
        time.Advance(10);
        clock.Update(session);
        Assert.AreEqual(20, clock.Position.TotalSeconds, 0.001);
    }

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
    [TestMethod]
    public void DelayedDeliveryAndRepeatedTrackChangesDoNotAccumulateLag()
    {
        var time = new ManualTime(); var clock = new MediaPlaybackClock(time);
        for (var index = 0; index < 20; index++)
        {
            var observed = time.GetUtcNow(); time.Advance(2);
            clock.Update(MediaSessionSnapshot.Empty with { TrackTitle = "Track " + index, LastUpdated = observed,
                PlaybackState = MediaPlaybackState.Playing,
                Timeline = new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 1, DateTimeOffset.FromFileTime(0)) });
            Assert.AreEqual(2, clock.Position.TotalSeconds, 0.001);
            time.Advance(3); Assert.AreEqual(5, clock.Position.TotalSeconds, 0.001);
        }
    }

    [TestMethod]
    public void QuantizedAppleMusicPositionDoesNotRewindOnTimestampOnlyUpdates()
    {
        var time = new ManualTime(); var clock = new MediaPlaybackClock(time);
        var session = MediaSessionSnapshot.Empty with { TrackTitle = "Track", PlaybackState = MediaPlaybackState.Playing,
            Timeline = new(TimeSpan.FromSeconds(10), TimeSpan.Zero, TimeSpan.FromSeconds(120), 1, time.GetUtcNow()) };
        clock.Update(session);
        for (var quarter = 1; quarter <= 12; quarter++)
        {
            time.Advance(0.25);
            session = session with { Timeline = session.Timeline with { Position = TimeSpan.FromSeconds(10 + quarter / 4), LastUpdated = time.GetUtcNow() } };
            clock.Update(session);
            Assert.AreEqual(10 + quarter * 0.25, clock.Position.TotalSeconds, 0.001);
        }
        clock.Update(session with { Timeline = session.Timeline with { Position = TimeSpan.FromSeconds(2), LastUpdated = time.GetUtcNow() } });
        Assert.AreEqual(2, clock.Position.TotalSeconds, 0.001);
    }

    [TestMethod]
    public void LateWholeSecondObservationDoesNotReverseButARealSeekStillDoes()
    {
        var time = new ManualTime(); var clock = new MediaPlaybackClock(time);
        var session = MediaSessionSnapshot.Empty with { TrackTitle = "Track", PlaybackState = MediaPlaybackState.Playing,
            Timeline = new(TimeSpan.FromSeconds(10), TimeSpan.Zero, TimeSpan.FromSeconds(120), 1, time.GetUtcNow()) };
        clock.Update(session); time.Advance(1.2);
        clock.Update(session with { Timeline = session.Timeline with { Position = TimeSpan.FromSeconds(11), LastUpdated = time.GetUtcNow() } });
        Assert.AreEqual(11.2, clock.Position.TotalSeconds, 0.001);
        clock.Update(session with { Timeline = session.Timeline with { Position = TimeSpan.FromSeconds(2), LastUpdated = time.GetUtcNow() } });
        Assert.AreEqual(2, clock.Position.TotalSeconds, 0.001);
    }

    [TestMethod]
    public void MissingTimelineAfterLongGapDoesNotBecomeAFalseTrackChange()
    {
        var time = new ManualTime(); var clock = new MediaPlaybackClock(time);
        var session = MediaSessionSnapshot.Empty with
        {
            SessionId = "apple-session",
            TrackTitle = "Track",
            Artist = "Artist",
            PlaybackState = MediaPlaybackState.Playing,
            Timeline = new(TimeSpan.FromSeconds(30), TimeSpan.Zero, TimeSpan.FromSeconds(120), 1, time.GetUtcNow()),
        };

        clock.Update(session);
        time.Advance(20);
        var missingTimeline = session with
        {
            Timeline = new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 1, DateTimeOffset.FromFileTime(0)),
        };
        clock.Update(missingTimeline);

        Assert.IsTrue(clock.IsEstimated);
        Assert.AreEqual(30, clock.Position.TotalSeconds, 0.001);
        time.Advance(1);
        Assert.AreEqual(31, clock.Position.TotalSeconds, 0.001);
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero).AddTicks(_ticks);
        public void Advance(double seconds) => _ticks += (long)(seconds * TimeSpan.TicksPerSecond);
    }
}
