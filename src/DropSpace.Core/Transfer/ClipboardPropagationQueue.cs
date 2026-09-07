using System.Threading.Channels;
using DropSpace.Core.Models;

namespace DropSpace.Core.Transfer;

/// <summary>
/// Owns automatic clipboard propagation. A slow peer cannot create unbounded concurrent
/// sends; overload retains the newest snapshots while local history remains independent.
/// </summary>
public sealed class ClipboardPropagationQueue : IAsyncDisposable
{
    public const int Capacity = 16;
    private readonly Channel<DropItem> _queue = Channel.CreateBounded<DropItem>(new BoundedChannelOptions(Capacity)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropOldest,
    });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Func<DropItem, CancellationToken, Task> _propagate;
    private readonly Action<Exception> _reportFailure;
    private readonly Task _worker;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;

    public ClipboardPropagationQueue(Func<DropItem, CancellationToken, Task> propagate, Action<Exception> reportFailure)
    {
        _propagate = propagate ?? throw new ArgumentNullException(nameof(propagate));
        _reportFailure = reportFailure ?? throw new ArgumentNullException(nameof(reportFailure));
        _worker = Task.Run(ProcessAsync);
    }

    public bool TryEnqueue(DropItem item) => _queue.Writer.TryWrite(item);

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                _shutdown.Token.ThrowIfCancellationRequested();
                try { await _propagate(item, _shutdown.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { return; }
                catch (Exception exception) { _reportFailure(exception); }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        finally
        {
            while (_queue.Reader.TryRead(out _)) { }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            _disposeTask ??= StopAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task StopAsync()
    {
        _queue.Writer.TryComplete();
        await Task.Yield();
        try
        {
            try { await _shutdown.CancelAsync().ConfigureAwait(false); }
            catch (AggregateException exception) { _reportFailure(exception); }
            await _worker.ConfigureAwait(false);
        }
        finally { _shutdown.Dispose(); }
    }
}
