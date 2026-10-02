namespace DropSpace.App.Services.Media;

/// <summary>Keeps ownership of cancelled work without blocking unrelated presentation updates.</summary>
internal sealed class RetirableMediaWork
{
    private const int MaximumOutstanding = 2;
    private readonly object _gate = new();
    private readonly List<Work> _outstanding = [];
    private Work? _current;

    public void CancelCurrent()
    {
        lock (_gate) _current?.Cancellation.Request();
    }

    public void RetireCurrent()
    {
        lock (_gate)
        {
            _current?.Cancellation.Request();
            _current = null;
        }
    }

    public Task WaitForCurrentAsync(CancellationToken token)
    {
        lock (_gate) return (_current?.Task ?? Task.CompletedTask).WaitAsync(token);
    }

    public void Start(Func<CancellationToken, Task> action, CancellationToken lifetime)
    {
        if (!TryStart(action, lifetime)) throw new InvalidOperationException("Media work has reached its recovery budget.");
    }

    public bool TryStartWhileIdle(SemaphoreSlim maintenance, Func<CancellationToken, Task> action,
        CancellationToken lifetime, Action? capacityAvailable = null)
    {
        if (!maintenance.Wait(0)) return false;
        try { return TryStart(action, lifetime, capacityAvailable); }
        finally { maintenance.Release(); }
    }

    public bool TryStart(Func<CancellationToken, Task> action, CancellationToken lifetime, Action? capacityAvailable = null)
    {
        lifetime.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _outstanding.RemoveAll(work => work.Retirement.IsCompleted);
            if (_current is not null && !_current.Retirement.IsCompleted || _outstanding.Count >= MaximumOutstanding) return false;
            var cancellation = new MediaWorkCancellation(lifetime);
            var task = Task.Run(() =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                return action(cancellation.Token);
            }, CancellationToken.None);
            var retirement = cancellation.CompleteWhenAsync(task);
            _current = new(task, retirement, cancellation);
            _outstanding.Add(_current);
            if (capacityAvailable is not null) _ = NotifyCapacityAsync(retirement, capacityAvailable);
            return true;
        }
    }

    public Task DrainAsync(CancellationToken token)
    {
        Task[] tasks;
        lock (_gate)
        {
            foreach (var work in _outstanding) work.Cancellation.Request();
            tasks = _outstanding.Select(work => work.Retirement).ToArray();
        }
        return Task.WhenAll(tasks).WaitAsync(token);
    }

    private static async Task NotifyCapacityAsync(Task retirement, Action notify)
    {
        await retirement.ConfigureAwait(false);
        notify();
    }

    private sealed record Work(Task Task, Task Retirement, MediaWorkCancellation Cancellation);
}

/// <summary>Cancellation is a request; its callbacks remain owned until actual completion.</summary>
internal sealed class MediaWorkCancellation
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _source = new();
    private readonly CancellationTokenRegistration _registration;
    private Task _callbacks = Task.CompletedTask;
    private bool _requested, _closed;

    public MediaWorkCancellation(CancellationToken lifetime)
    {
        Token = _source.Token;
        // Never propagate parent cancellation synchronously into native/user callbacks.
        _registration = lifetime.UnsafeRegister(static owner => ((MediaWorkCancellation)owner!).Request(), this);
    }

    public CancellationToken Token { get; }

    public void Request()
    {
        lock (_gate)
        {
            if (_closed || _requested) return;
            _requested = true;
            _callbacks = _source.CancelAsync();
        }
    }

    public async Task CompleteWhenAsync(Task owner)
    {
        await MediaSoftRestartOperation.ObserveAsync(owner).ConfigureAwait(false);
        Task callbacks;
        lock (_gate)
        {
            _closed = true;
            callbacks = _callbacks;
            // Unregister never waits for an in-progress parent callback. Such a callback
            // sees _closed and cannot touch this source after the lock is released.
            _registration.Unregister();
        }
        await MediaSoftRestartOperation.ObserveAsync(callbacks).ConfigureAwait(false);
        _source.Dispose();
    }
}
