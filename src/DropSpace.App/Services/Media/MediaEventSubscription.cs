namespace DropSpace.App.Services.Media;

/// <summary>Owns subscriptions even if native add/remove calls finish after retirement.</summary>
internal sealed class MediaEventSubscription
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _retired = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _active = 1;

    public bool IsActive => Volatile.Read(ref _active) != 0;

    public async Task StartAsync(BoundedMediaOperation operations, object owner, Action attach, Action detach,
        TimeSpan timeout, CancellationToken token)
    {
        _ = OwnAsync();
        try { await _ready.Task.WaitAsync(timeout, token).ConfigureAwait(false); }
        catch { Retire(); throw; }

        async Task OwnAsync()
        {
            try
            {
                await operations.RunAsync(owner, async _ =>
                {
                    if (!IsActive) return true;
                    try
                    {
                        attach();
                        _ready.TrySetResult();
                        await _retired.Task.ConfigureAwait(false);
                        return true;
                    }
                    finally { detach(); }
                }, Timeout.InfiniteTimeSpan, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _ready.TrySetException(exception);
                _ = _ready.Task.Exception; // Also observe setup faults that arrive after timeout.
            }
        }
    }

    public void Retire()
    {
        Interlocked.Exchange(ref _active, 0);
        _retired.TrySetResult();
    }
}
