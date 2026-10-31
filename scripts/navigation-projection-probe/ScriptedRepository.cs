using System.Reflection;
using System.Threading.Channels;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;

namespace NavigationProjectionProbe;

// Only QueryPageAsync is admitted. The actual ItemProjectionService constructs each query
// and schedules it on Task.Run; the fixture controls response order and failure/cancellation.
public class ScriptedRepository : DispatchProxy
{
    private readonly Channel<QueryCall> _calls = Channel.CreateUnbounded<QueryCall>();
    private int _callCount;

    internal int CallCount => Volatile.Read(ref _callCount);
    internal bool IgnoreCancellation { get; set; }

    internal static (IItemRepository Repository, ScriptedRepository Control) Create()
    {
        var repository = Create<IItemRepository, ScriptedRepository>();
        return (repository, (ScriptedRepository)repository);
    }

    internal Task<QueryCall> NextAsync() =>
        _calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        ArgumentNullException.ThrowIfNull(args);
        if (targetMethod.Name != nameof(IItemRepository.QueryPageAsync))
            throw new NotSupportedException("Unexpected repository call: " + targetMethod.Name);

        var call = new QueryCall((ItemQuery)args[0]!, (ItemQueryCursor?)args[1],
            IgnoreCancellation ? CancellationToken.None : (CancellationToken)args[2]!);
        Interlocked.Increment(ref _callCount);
        if (!_calls.Writer.TryWrite(call)) throw new InvalidOperationException("Query fixture is unavailable.");
        return call.Completion;
    }
}

internal sealed class QueryCall : IDisposable
{
    private readonly TaskCompletionSource<ItemQueryPage> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenRegistration _registration;
    internal ItemQuery Query { get; }
    internal ItemQueryCursor? Cursor { get; }
    internal Task<ItemQueryPage> Completion => _completion.Task;

    internal QueryCall(ItemQuery query, ItemQueryCursor? cursor, CancellationToken token)
    {
        Query = query;
        Cursor = cursor;
        _registration = token.Register(() => _completion.TrySetCanceled(token));
    }

    internal void Reply(ItemQueryPage page) => _completion.SetResult(page);
    internal void Fail(Exception error) => _completion.SetException(error);
    public void Dispose() => _registration.Dispose();
}
