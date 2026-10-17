using DropSpace.Core.Widgets;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class WidgetCountdownTests
{
    [TestMethod]
    public void DisplayRoundsUpUntilTheActualDeadlineAndThenRestarts()
    {
        var clock = new ManualTime();
        var timer = new WidgetCountdown(TimeSpan.FromMinutes(5), clock);
        timer.Toggle();
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(300L, timer.RemainingSeconds);

        clock.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromMilliseconds(2));
        Assert.AreEqual(1L, timer.RemainingSeconds);
        Assert.IsTrue(timer.IsRunning);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.AreEqual(0L, timer.RemainingSeconds);
        Assert.IsFalse(timer.IsRunning);

        clock.Advance(TimeSpan.FromDays(1));
        Assert.AreEqual(TimeSpan.Zero, timer.Remaining);
        timer.Toggle();
        Assert.IsTrue(timer.IsRunning);
        Assert.AreEqual(300L, timer.RemainingSeconds);
    }

    [TestMethod]
    public void PausePreservesFractionalTimeAndHiddenElapsedTimeIsNotLost()
    {
        var clock = new ManualTime();
        var timer = new WidgetCountdown(TimeSpan.FromMinutes(1), clock);
        timer.Toggle();
        clock.Advance(TimeSpan.FromMilliseconds(1500));
        timer.Toggle();
        clock.Advance(TimeSpan.FromHours(1));
        Assert.AreEqual(TimeSpan.FromMilliseconds(58500), timer.Remaining);
        Assert.AreEqual(59L, timer.RemainingSeconds);

        timer.Toggle();
        // No reads, renders or callbacks while the surface is hidden.
        clock.Advance(TimeSpan.FromSeconds(58));
        Assert.AreEqual(TimeSpan.FromMilliseconds(500), timer.Remaining);
        Assert.AreEqual(1L, timer.RemainingSeconds);
        timer.Reset();
        Assert.IsFalse(timer.IsRunning);
        Assert.AreEqual(60L, timer.RemainingSeconds);
    }

    [TestMethod]
    public void ChangingDurationPausesAndResetsOnlyTheSelectedTimer()
    {
        var clock = new ManualTime();
        var focus = new WidgetCountdown(TimeSpan.FromMinutes(25), clock);
        var countdown = new WidgetCountdown(TimeSpan.FromMinutes(5), clock);
        focus.Toggle(); countdown.Toggle();
        clock.Advance(TimeSpan.FromMinutes(1));

        countdown.SetDuration(TimeSpan.FromMinutes(10));
        Assert.AreEqual(TimeSpan.FromMinutes(10), countdown.Duration);
        Assert.AreEqual(600L, countdown.RemainingSeconds);
        Assert.IsFalse(countdown.IsRunning);
        Assert.IsTrue(focus.IsRunning);
        Assert.AreEqual(TimeSpan.FromMinutes(24), focus.Remaining);

        countdown.Toggle();
        clock.Advance(TimeSpan.FromSeconds(1));
        countdown.Reset();
        Assert.AreEqual(600L, countdown.RemainingSeconds);
        Assert.IsFalse(countdown.IsRunning);
    }

    [TestMethod]
    public void InvalidDurationPreservesTheSessionAndZeroNeverRuns()
    {
        var clock = new ManualTime();
        var timer = new WidgetCountdown(TimeSpan.FromMinutes(5), clock);
        timer.Toggle();
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => timer.SetDuration(TimeSpan.FromTicks(-1)));
        Assert.AreEqual(TimeSpan.FromMinutes(5), timer.Duration);
        Assert.AreEqual(299L, timer.RemainingSeconds);
        Assert.IsTrue(timer.IsRunning);

        timer.SetDuration(TimeSpan.Zero);
        timer.Toggle();
        Assert.IsFalse(timer.IsRunning);
        Assert.AreEqual(0L, timer.RemainingSeconds);
        timer.SetDuration(TimeSpan.MaxValue);
        Assert.AreEqual(922337203686L, timer.RemainingSeconds);
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan elapsed) => _ticks += elapsed.Ticks;
    }
}
