// Ported from NovaClip Beta8, b798c571dad1cae03c16129e4b4c43b85b428d7a.
using System.Diagnostics;

namespace DropSpace.Infrastructure.Downloads;

/// <summary>One aggregate token bucket shared by every HTTP transfer; zero disables throttling.</summary>
public sealed class DownloadBandwidthLimiter
{
    private readonly object _gate = new();
    private long _rate;
    private double _tokens;
    private long _updated = Stopwatch.GetTimestamp();
    private TaskCompletionSource _changed = NewSignal();

    public long BytesPerSecond { get { lock (_gate) return _rate; } }

    public void SetLimit(long bytesPerSecond)
    {
        if (bytesPerSecond is < 0 or > 1_073_741_824) throw new ArgumentOutOfRangeException(nameof(bytesPerSecond));
        lock (_gate)
        {
            _rate = bytesPerSecond;
            _tokens = Math.Max(1, _rate / 10d);
            _updated = Stopwatch.GetTimestamp();
            var old = _changed;
            _changed = NewSignal();
            old.TrySetResult();
        }
    }

    public async ValueTask ConsumeAsync(int bytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        var remaining = bytes;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task changed;
            TimeSpan delay;
            lock (_gate)
            {
                if (_rate == 0) return;
                var now = Stopwatch.GetTimestamp();
                var capacity = Math.Max(1, _rate / 10d);
                _tokens = Math.Min(capacity, _tokens + Stopwatch.GetElapsedTime(_updated, now).TotalSeconds * _rate);
                _updated = now;
                var granted = (int)Math.Min(remaining, Math.Floor(_tokens));
                _tokens -= granted;
                remaining -= granted;
                if (remaining == 0) return;
                delay = TimeSpan.FromSeconds(Math.Clamp((Math.Min(remaining, capacity) - _tokens) / _rate, .001, .1));
                changed = _changed.Task;
            }
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try { await Task.WhenAny(Task.Delay(delay, wait.Token), changed).WaitAsync(cancellationToken).ConfigureAwait(false); }
            finally { await wait.CancelAsync().ConfigureAwait(false); }
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
