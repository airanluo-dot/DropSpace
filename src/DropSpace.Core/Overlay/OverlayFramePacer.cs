using System;
using System.Diagnostics;

namespace DropSpace.Core.Overlay;

/// <summary>
/// Gates a shared rendering callback to one overlay's configured display rate.
/// It schedules updates; it does not guarantee presentation or measure instantaneous VRR.
/// </summary>
public sealed class OverlayFramePacer
{
    private const double DefaultRefreshRateHz = 60;
    private const double EarlyTolerancePeriods = 0.24;
    private readonly long _timestampFrequency;
    private double _periodTicks;
    private long _epochTimestamp;
    private double _nextFrameIndex;
    private long _lastTimestamp;
    private bool _hasDeadline;

    public OverlayFramePacer(double? refreshRateHz = null, long? timestampFrequency = null)
    {
        _timestampFrequency = timestampFrequency ?? Stopwatch.Frequency;
        if (_timestampFrequency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timestampFrequency));
        }

        SetRefreshRate(refreshRateHz);
    }

    public double RefreshRateHz { get; private set; }

    public void SetRefreshRate(double? refreshRateHz)
    {
        var rate = refreshRateHz.GetValueOrDefault();
        var period = _timestampFrequency / rate;
        if (!double.IsFinite(rate) || rate <= 0 || !double.IsFinite(period) || period < 1)
        {
            rate = DefaultRefreshRateHz;
            period = _timestampFrequency / rate;
        }

        if (RefreshRateHz == rate)
        {
            return;
        }

        RefreshRateHz = rate;
        _periodTicks = period;
        Reset();
    }

    public void Reset()
    {
        _hasDeadline = false;
        _epochTimestamp = 0;
        _nextFrameIndex = 0;
        _lastTimestamp = 0;
    }

    public bool ShouldRender(long timestamp)
    {
        if (!_hasDeadline || timestamp < _lastTimestamp)
        {
            _hasDeadline = true;
            _lastTimestamp = timestamp;
            _epochTimestamp = timestamp;
            _nextFrameIndex = 1;
            return true;
        }

        if (timestamp == _lastTimestamp)
        {
            return false;
        }

        _lastTimestamp = timestamp;
        var elapsedTicks = (double)(timestamp - _epochTimestamp);
        var earliestDeadline = elapsedTicks + _periodTicks * EarlyTolerancePeriods;
        if (earliestDeadline < _nextFrameIndex * _periodTicks)
        {
            return false;
        }

        // Keep the original phase instead of rebasing the deadline to every callback.
        // Advance past every missed deadline but emit only one update after a long stall.
        _nextFrameIndex = Math.Max(_nextFrameIndex + 1,
            Math.Floor(earliestDeadline / _periodTicks) + 1);
        return true;
    }
}
