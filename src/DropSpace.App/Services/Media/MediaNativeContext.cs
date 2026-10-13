using Microsoft.UI.Dispatching;

namespace DropSpace.App.Services.Media;

/// <summary>
/// Owns one lazily created STA dispatcher for a media service's native objects. Timeouts
/// never replace its thread or release objects whose native operations are still running.
/// </summary>
internal sealed class MediaNativeContext : IMediaOperationExecutor, IAsyncDisposable
{
    private readonly object _gate = new();
    private Task<Owner>? _startup;
    private TaskCompletionSource _idle = Completed();
    private Task? _shutdown;
    private int _outstanding;
    private bool _retired;

    public Task<T> RunAsync<T>(Func<Task<T>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Task<Owner> startup;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_retired, this);
            // Count before starting the dispatcher: retirement must also wait for callers
            // still creating the apartment or waiting to enqueue their operation.
            if (_outstanding++ == 0) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
            startup = _startup ??= Task.Run(() => new Owner(DispatcherQueueController.CreateOnDedicatedThread()));
        }

        return RunCoreAsync(startup, operation);
    }

    public Task RunAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return RunAsync(() =>
        {
            action();
            return Task.FromResult(true);
        });
    }

    private async Task<T> RunCoreAsync<T>(Task<Owner> startup, Func<Task<T>> operation)
    {
        try
        {
            var owner = await startup.ConfigureAwait(false);
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!owner.Controller.DispatcherQueue.TryEnqueue(() =>
                {
                    // This queue belongs exclusively to this service. Keep its context
                    // installed for every await continuation, including native detach.
                    SynchronizationContext.SetSynchronizationContext(owner.Context);
                    _ = InvokeAsync();
                }))
            {
                throw new InvalidOperationException("The media native dispatcher rejected an operation.");
            }

            return await completion.Task.ConfigureAwait(false);

            async Task InvokeAsync()
            {
                try { completion.TrySetResult(await operation()); }
                catch (OperationCanceledException exception) { completion.TrySetCanceled(exception.CancellationToken); }
                catch (Exception exception) { completion.TrySetException(exception); }
            }
        }
        finally
        {
            lock (_gate)
            {
                if (--_outstanding == 0)
                {
                    // A failed factory created no usable apartment. Permit a later
                    // explicit call to retry after every waiter has observed that failure.
                    // Never replace a successfully created dispatcher after an operation
                    // fault or timeout: it may still own uncompleted native work.
                    if (!_retired && (startup.IsFaulted || startup.IsCanceled)) _startup = null;
                    _idle.TrySetResult();
                }
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            _retired = true;
            // Starting and stopping the native dispatcher must never synchronously block
            // the UI. Do not shut it down until every admitted operation fully completes.
            return new ValueTask(_shutdown ??= Task.Run(() => ShutdownAsync(_startup, _idle.Task)));
        }
    }

    private static async Task ShutdownAsync(Task<Owner>? startup, Task idle)
    {
        await idle.ConfigureAwait(false);
        if (startup is null) return;
        var owner = await startup.ConfigureAwait(false);
        await owner.Controller.ShutdownQueueAsync();
    }

    private static TaskCompletionSource Completed()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetResult();
        return completion;
    }

    private sealed class Owner(DispatcherQueueController controller)
    {
        public DispatcherQueueController Controller { get; } = controller;
        public SynchronizationContext Context { get; } = new DispatcherQueueSynchronizationContext(controller.DispatcherQueue);
    }
}
