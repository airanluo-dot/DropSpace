namespace DropSpace.Infrastructure.Lyrics;

/// <summary>Stops and drains inference before model/cache maintenance; blocks new work while draining.</summary>
public sealed class AiLyricsWorkLifetime : IDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _maintenanceGate = new(1, 1);
    private readonly HashSet<CancellationTokenSource> _active = [];
    private TaskCompletionSource _idle = Completed();
    private bool _maintaining, _disposed;

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, T unavailable, CancellationToken token)
    {
        CancellationTokenSource stop;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            token.ThrowIfCancellationRequested();
            if (_maintaining) return unavailable;
            stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (_active.Count == 0) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _active.Add(stop);
        }
        try { return await action(stop.Token).ConfigureAwait(false); }
        finally
        {
            lock (_sync)
            {
                _active.Remove(stop);
                stop.Dispose();
                if (_active.Count == 0) _idle.TrySetResult();
            }
        }
    }

    public async Task MaintainAsync(Func<CancellationToken, Task> action, CancellationToken token)
    {
        await _maintenanceGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Task idle;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _maintaining = true;
                // Cancel only controlled inference callbacks; active scopes retire under the same lock.
                foreach (var stop in _active.ToArray()) stop.Cancel();
                idle = _idle.Task;
            }
            await idle.WaitAsync(token).ConfigureAwait(false);
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
            foreach (var stop in _active.ToArray()) stop.Cancel();
        }
    }

    private static TaskCompletionSource Completed()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetResult();
        return completion;
    }
}
