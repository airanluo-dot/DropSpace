using System.Collections.Concurrent;

namespace NavigationProjectionProbe;

// A single managed thread pumps continuations, matching the production UI's serialized
// collection mutations. This is not a WinUI dispatcher or a rendering/input simulation.
internal sealed class ProbeDispatcher : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _callbacks = [];

    public override void Post(SendOrPostCallback callback, object? state) => _callbacks.Add((callback, state));

    internal static void Run(Func<Task> operation)
    {
        var previous = Current;
        using var dispatcher = new ProbeDispatcher();
        SetSynchronizationContext(dispatcher);
        try
        {
            var work = operation();
            _ = work.ContinueWith(_ => dispatcher._callbacks.CompleteAdding(),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            foreach (var (callback, state) in dispatcher._callbacks.GetConsumingEnumerable()) callback(state);
            work.GetAwaiter().GetResult();
        }
        finally { SetSynchronizationContext(previous); }
    }

    public void Dispose() => _callbacks.Dispose();
}
