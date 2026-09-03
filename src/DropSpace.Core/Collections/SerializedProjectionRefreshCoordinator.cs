namespace DropSpace.Core.Collections;

/// <summary>
/// Coalesces projection refresh requests and guarantees that load/apply pairs never overlap.
/// A result is applied only when no newer revision was requested while it was loading.
/// </summary>
public sealed class SerializedProjectionRefreshCoordinator<T> : IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Func<CancellationToken, Task<IReadOnlyList<T>>> _loadAsync;
    private readonly Func<IReadOnlyList<T>, long, CancellationToken, Task> _applyAsync;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SortedDictionary<long, List<TaskCompletionSource>> _waiters = [];
    private Task? _workerTask;
    private long _requestedRevision = -1;
    private long _appliedRevision = -1;
    private bool _running;
    private bool _disposed;

    public SerializedProjectionRefreshCoordinator(
        Func<CancellationToken, Task<IReadOnlyList<T>>> loadAsync,
        Func<IReadOnlyList<T>, long, CancellationToken, Task> applyAsync)
    {
        _loadAsync = loadAsync ?? throw new ArgumentNullException(nameof(loadAsync));
        _applyAsync = applyAsync ?? throw new ArgumentNullException(nameof(applyAsync));
    }

    public long AppliedRevision
    {
        get
        {
            lock (_gate)
            {
                return _appliedRevision;
            }
        }
    }

    public Task RequestAsync(long revision, CancellationToken cancellationToken = default)
    {
        Task waitTask;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (revision <= _appliedRevision)
            {
                return Task.CompletedTask;
            }

            _requestedRevision = Math.Max(_requestedRevision, revision);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_waiters.TryGetValue(revision, out var completions))
            {
                completions = [];
                _waiters.Add(revision, completions);
            }

            completions.Add(completion);
            waitTask = completion.Task;
            if (!_running)
            {
                _running = true;
                // Keep the worker task owned by the coordinator so asynchronous disposal can
                // observe its final cancellation/unwind.
                _workerTask = RunAsync();
            }
        }

        return cancellationToken.CanBeCanceled
            ? waitTask.WaitAsync(cancellationToken)
            : waitTask;
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        Task? worker;
        lock (_gate)
        {
            worker = _workerTask;
        }

        if (worker is not null)
        {
            try
            {
                await worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                // Shutdown cancellation is the expected worker completion path.
            }
        }
    }

    public void Dispose()
    {
        List<TaskCompletionSource> waiters;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _shutdown.Cancel();
            waiters = _waiters.Values.SelectMany(static values => values).ToList();
            _waiters.Clear();
        }

        foreach (var waiter in waiters)
        {
            waiter.TrySetCanceled(_shutdown.Token);
        }

        // RunAsync may still be unwinding with this token. Disposing the CTS here creates a race
        // where its cancellation handler cannot complete waiter tasks. It is intentionally left
        // for GC after the short-lived coordinator is released.
    }

    private async Task RunAsync()
    {
        while (true)
        {
            long revision;
            lock (_gate)
            {
                if (_disposed || _requestedRevision <= _appliedRevision)
                {
                    _running = false;
                    return;
                }

                revision = _requestedRevision;
            }

            try
            {
                var items = await _loadAsync(_shutdown.Token);
                lock (_gate)
                {
                    if (_requestedRevision > revision)
                    {
                        continue;
                    }
                }

                await _applyAsync(items, revision, _shutdown.Token);
                CompleteThrough(revision);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                CompleteAllWithCancellation();
                return;
            }
            catch (Exception exception)
            {
                CompleteThrough(revision, exception);
            }
        }
    }

    private void CompleteThrough(long revision, Exception? exception = null)
    {
        List<TaskCompletionSource> completed = [];
        lock (_gate)
        {
            // A failed revision is terminal for its waiters. Advancing here prevents the worker
            // from spinning forever on the same failed request; a later revision can retry.
            _appliedRevision = Math.Max(_appliedRevision, revision);

            foreach (var key in _waiters.Keys.Where(key => key <= revision).ToArray())
            {
                completed.AddRange(_waiters[key]);
                _waiters.Remove(key);
            }
        }

        foreach (var completion in completed)
        {
            if (exception is null)
            {
                completion.TrySetResult();
            }
            else
            {
                completion.TrySetException(exception);
            }
        }
    }

    private void CompleteAllWithCancellation()
    {
        List<TaskCompletionSource> waiters;
        lock (_gate)
        {
            _running = false;
            waiters = _waiters.Values.SelectMany(static values => values).ToList();
            _waiters.Clear();
        }

        foreach (var waiter in waiters)
        {
            waiter.TrySetCanceled(_shutdown.Token);
        }
    }
}
