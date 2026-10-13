using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services;

/// <summary>Bounds remote image preparation without releasing inputs before native completion.</summary>
internal sealed class ClipboardImageReadLifetime(ILogger logger)
{
    private readonly object _gate = new();
    private bool _occupied;
    private TaskCompletionSource _idle = Completed();

    internal int Outstanding { get { lock (_gate) return _occupied ? 1 : 0; } }
    internal Task DrainAsync() { lock (_gate) return _idle.Task; }

    internal async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> read,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            // Retired work still owns this slot. Reject another image promptly so
            // a stalled codec cannot accumulate inputs or block text imports.
            if (_occupied) throw new IOException("Remote clipboard image preparation is still in progress.");
            _occupied = true;
            _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        var stop = new CancellationTokenSource();
        Task<T> owned;
        try
        {
            // Scheduling and the native owner have independent lifetimes from the
            // caller. Only the owner may dispose the stream used by its decoder.
            owned = Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                stop.Token.ThrowIfCancellationRequested();
                return read(stop.Token);
            }, CancellationToken.None);
        }
        catch
        {
            stop.Dispose();
            Release();
            throw;
        }

        try
        {
            var result = await owned.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (TimeoutException exception)
        {
            throw new IOException("Remote clipboard image preparation timed out.", exception);
        }
        finally
        {
            var completed = owned.IsCompleted;
            var retirement = RetireAsync(owned, stop);
            // Healthy sequential imports must see their slot released before
            // returning. Abandoned native work keeps ownership independently.
            if (completed) await retirement.ConfigureAwait(false);
        }
    }

    private async Task RetireAsync(Task owned, CancellationTokenSource stop)
    {
        var cancellation = owned.IsCompleted ? Task.CompletedTask : CancelAsync(stop);
        try { await owned.ConfigureAwait(false); }
        catch (Exception exception)
        {
            logger.LogDebug("Remote clipboard image preparation retired ({Category}).", exception.GetType().Name);
        }
        finally
        {
            await cancellation.ConfigureAwait(false);
            stop.Dispose();
            Release();
        }
    }

    private async Task CancelAsync(CancellationTokenSource stop)
    {
        try { await stop.CancelAsync().ConfigureAwait(false); }
        catch (Exception exception)
        {
            // A failed Cancel callback is not completion; RetireAsync continues
            // to own the slot and native inputs until the read actually ends.
            logger.LogDebug("Remote clipboard image cancellation failed ({Category}).", exception.GetType().Name);
        }
    }

    private void Release()
    {
        lock (_gate)
        {
            _occupied = false;
            _idle.TrySetResult();
        }
    }

    private static TaskCompletionSource Completed()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetResult();
        return completion;
    }
}
