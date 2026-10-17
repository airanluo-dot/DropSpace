namespace DropSpace.Core.Widgets;

/// <summary>Monotonic elapsed-time countdown; hiding a widget does not pause its time.</summary>
public sealed class WidgetCountdown
{
    private readonly TimeProvider _clock;
    private TimeSpan _elapsed;
    private long _startedAt;
    private bool _running;

    public WidgetCountdown(TimeSpan duration, TimeProvider? timeProvider = null)
    {
        _clock = timeProvider ?? TimeProvider.System;
        SetDuration(duration);
    }

    public TimeSpan Duration { get; private set; }
    public bool IsRunning => _running && Remaining > TimeSpan.Zero;
    public TimeSpan Remaining
    {
        get
        {
            var remaining = Duration - _elapsed;
            if (!_running) return remaining;
            var elapsed = _clock.GetElapsedTime(_startedAt);
            return elapsed < remaining ? remaining - elapsed : TimeSpan.Zero;
        }
    }

    /// <summary>Round up so a running timer never displays zero before its deadline.</summary>
    public long RemainingSeconds
    {
        get
        {
            var ticks = Remaining.Ticks;
            return ticks / TimeSpan.TicksPerSecond + (ticks % TimeSpan.TicksPerSecond == 0 ? 0 : 1);
        }
    }

    public void Toggle()
    {
        var remaining = Remaining;
        if (remaining == TimeSpan.Zero)
        {
            Reset();
        }
        else if (_running)
        {
            _elapsed = Duration - remaining;
            _running = false;
            return;
        }

        _startedAt = _clock.GetTimestamp();
        _running = Duration > TimeSpan.Zero;
    }

    /// <summary>Changing duration resets and pauses the current session.</summary>
    public void SetDuration(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        Duration = duration;
        Reset();
    }

    public void Reset()
    {
        _running = false;
        _elapsed = TimeSpan.Zero;
    }
}
