namespace DropSpace.App.Services.Media;

/// <summary>One bounded restart at a time; cancelling a page only retires that page's waiter.</summary>
internal sealed class MediaSoftRestartOperation(TimeSpan timeout)
{
    private readonly object _gate = new();
    private Task _attempt = Task.CompletedTask, _operation = Task.CompletedTask, _retirement = Task.CompletedTask;
    private MediaWorkCancellation? _cancellation;
    private bool _closed;

    public Task RunAsync(Func<CancellationToken, Task> restart, CancellationToken lifetime, CancellationToken caller = default)
    {
        caller.ThrowIfCancellationRequested();
        lifetime.ThrowIfCancellationRequested();
        Task attempt;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_attempt.IsCompleted)
            {
                // The actual operation and its cancellation callbacks both own the slot.
                if (!_retirement.IsCompleted)
                    return Task.FromException(new InvalidOperationException("The previous music restart is still retiring."));
                var cancellation = new MediaWorkCancellation(lifetime);
                _cancellation = cancellation;
                _operation = Task.Run(() =>
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    return restart(cancellation.Token);
                }, CancellationToken.None);
                _retirement = cancellation.CompleteWhenAsync(_operation);
                _attempt = WaitForAttemptAsync(_operation, _retirement, cancellation, lifetime);
                _ = ObserveAsync(_attempt);
            }
            attempt = _attempt;
        }
        return caller.CanBeCanceled ? attempt.WaitAsync(caller) : attempt;
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            _closed = true;
            _cancellation?.Request();
            return _retirement;
        }
    }

    private async Task WaitForAttemptAsync(Task operation, Task retirement, MediaWorkCancellation cancellation, CancellationToken lifetime)
    {
        // The deadline is independent of cancellation callbacks: even a native callback
        // which never returns cannot prevent the caller receiving a timeout.
        try { await Task.WhenAll(operation, retirement).WaitAsync(timeout, lifetime).ConfigureAwait(false); }
        catch
        {
            cancellation.Request();
            throw;
        }
    }

    internal static async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { }
    }
}
