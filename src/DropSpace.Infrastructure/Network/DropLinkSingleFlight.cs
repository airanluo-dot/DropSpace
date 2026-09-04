namespace DropSpace.Infrastructure.Network;

/// <summary>Retains one asynchronous completion task for all concurrent idempotent callers.</summary>
internal sealed class DropLinkSingleFlight<T>
{
    private readonly object _gate = new();
    private Task<T>? _task;

    public Task<T> GetOrStart(Func<Task<T>> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (_gate)
        {
            return _task ??= factory();
        }
    }
}
