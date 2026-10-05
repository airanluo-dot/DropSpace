// Ported from NovaClip Beta8, b798c571dad1cae03c16129e4b4c43b85b428d7a.
namespace DropSpace.Infrastructure.Downloads;

/// <summary>Shared, live-configurable ceiling for active HTTP transfers across queued tasks.</summary>
public sealed class DownloadConnectionBudget
{
    public const int Maximum = 256;
    private readonly object _gate = new();
    private int _limit;
    private int _active;
    private TaskCompletionSource _changed = NewSignal();

    public DownloadConnectionBudget(int limit = 64) => SetLimit(limit);
    public int Limit { get { lock (_gate) return _limit; } }
    public int Active { get { lock (_gate) return _active; } }

    public void SetLimit(int limit)
    {
        if (limit is < 1 or > Maximum) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (_gate) { _limit = limit; Pulse(); }
    }

    public async ValueTask<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task wait;
            lock (_gate)
            {
                if (_active < _limit) { _active++; return new Lease(this); }
                wait = _changed.Task;
            }
            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void Release()
    {
        lock (_gate) { _active--; Pulse(); }
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Pulse()
    {
        var previous = _changed;
        _changed = NewSignal();
        previous.TrySetResult();
    }
    private sealed class Lease(DownloadConnectionBudget owner) : IDisposable
    {
        private DownloadConnectionBudget? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
