namespace DropSpace.App.Services.Media;

/// <summary>
/// Bounds both a caller's wait and the native work left behind when a publisher ignores
/// cancellation. A timed-out operation retains its slot, token and resource-owning async
/// state until it actually completes. One broken session cannot consume every slot.
/// </summary>
internal sealed class BoundedMediaOperation(int maximumOutstanding = 16, int maximumPerOwner = 2)
{
    private readonly object _gate = new();
    private readonly Dictionary<object, int> _owners = new(ReferenceEqualityComparer.Instance);
    private int _outstanding;

    internal int Outstanding { get { lock (_gate) return _outstanding; } }

    public async Task<T> RunAsync<T>(object owner, Func<CancellationToken, Task<T>> operation,
        TimeSpan timeout, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _owners.TryGetValue(owner, out var count);
            if (_outstanding >= maximumOutstanding || count >= maximumPerOwner)
                throw new InvalidOperationException("The media publisher still has uncompleted operations.");
            _owners[owner] = count + 1;
            _outstanding++;
        }

        // The caller token ends only the wait. Forward cancellation asynchronously below,
        // instead of running provider callbacks inside the caller's Cancel()/StopAsync().
        var stop = new CancellationTokenSource();
        Task<T> pending;
        // Invoke on a worker too: a COM property getter or even the call returning a
        // WinRT IAsyncOperation can block synchronously before a Task exists.
        try
        {
            pending = Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                stop.Token.ThrowIfCancellationRequested();
                return operation(stop.Token);
            }, stop.Token);
        }
        catch
        {
            stop.Dispose();
            Release(owner);
            throw;
        }

        try
        {
            var result = await pending.WaitAsync(timeout, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            // This continuation owns cleanup after a timeout; never dispose an in-use
            // stream or release a slot just because the caller left.
            _ = CompleteAsync(pending, owner, stop);
        }
    }

    private async Task CompleteAsync(Task pending, object owner, CancellationTokenSource stop)
    {
        var cancellation = pending.IsCompleted ? Task.CompletedTask : CancelAsync(stop);
        try { await pending.ConfigureAwait(false); }
        catch (Exception) { /* The active caller sees errors; retired errors are observed. */ }
        finally
        {
            await cancellation.ConfigureAwait(false);
            stop.Dispose();
            Release(owner);
        }
    }

    private static async Task CancelAsync(CancellationTokenSource stop)
    {
        try { await stop.CancelAsync().ConfigureAwait(false); }
        catch (Exception) { /* Completion may already have disposed this source. */ }
    }

    private void Release(object owner)
    {
        lock (_gate)
        {
            if (_owners[owner] == 1) _owners.Remove(owner);
            else _owners[owner]--;
            _outstanding--;
        }
    }
}
