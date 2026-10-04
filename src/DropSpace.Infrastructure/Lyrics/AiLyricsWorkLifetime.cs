namespace DropSpace.Infrastructure.Lyrics;

/// <summary>Stops and drains inference before model/cache maintenance; blocks new work while draining.</summary>
public sealed class AiLyricsWorkLifetime : IDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _maintenanceGate = new(1, 1);
    private sealed class ActiveWork(CancellationToken token)
    {
        public CancellationTokenSource Source { get; } = CancellationTokenSource.CreateLinkedTokenSource(token);
        public Task Callbacks { get; private set; } = Task.CompletedTask;
        public bool Closed { get; set; }
        private bool _requested;
        // Called under _sync. User/native callbacks execute outside the owner lock.
        public Task RequestStop()
        {
            if (!Closed && !_requested)
            {
                _requested = true;
                Callbacks = Source.CancelAsync();
            }
            return Callbacks;
        }
    }
    private readonly HashSet<ActiveWork> _active = [];
    private readonly Func<CancellationToken, Task>? _drainNativeCleanup;
    private TaskCompletionSource _idle = Completed();
    private bool _maintaining, _disposed;

    public AiLyricsWorkLifetime(Func<CancellationToken, Task>? drainNativeCleanup = null) =>
        _drainNativeCleanup = drainNativeCleanup;

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, T unavailable, CancellationToken token)
    {
        ActiveWork stop;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            token.ThrowIfCancellationRequested();
            if (_maintaining) return unavailable;
            stop = new ActiveWork(token);
            if (_active.Count == 0) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _active.Add(stop);
        }
        try { return await action(stop.Source.Token).ConfigureAwait(false); }
        finally
        {
            Task callbacks;
            lock (_sync)
            {
                stop.Closed = true;
                callbacks = stop.Callbacks;
            }
            try { await callbacks.ConfigureAwait(false); }
            catch (Exception error) when (error is not OutOfMemoryException) { /* Maintenance observes callback errors separately. */ }
            finally
            {
                lock (_sync)
                {
                    _active.Remove(stop);
                    stop.Source.Dispose();
                    if (_active.Count == 0) _idle.TrySetResult();
                }
            }
        }
    }

    public async Task MaintainAsync(Func<CancellationToken, Task> action, CancellationToken token)
    {
        await _maintenanceGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Task idle;
            Task[] callbacks;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _maintaining = true;
                callbacks = _active.Select(stop => stop.RequestStop()).ToArray();
                idle = _idle.Task;
            }
            await Task.WhenAll(callbacks).WaitAsync(token).ConfigureAwait(false);
            await idle.WaitAsync(token).ConfigureAwait(false);
            // A bounded inference cancellation may return before OS-confirmed exit. Keep
            // maintenance fenced until that separately owned native cleanup is confirmed.
            if (_drainNativeCleanup is not null) await _drainNativeCleanup(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await Task.Run(() => action(token), token).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync) _maintaining = false;
            _maintenanceGate.Release();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var stop in _active) stop.RequestStop();
        }
    }

    private static TaskCompletionSource Completed()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetResult();
        return completion;
    }
}
