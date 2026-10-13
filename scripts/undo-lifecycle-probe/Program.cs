using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using DropSpace.App.Services;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using DropSpace.Core.Policies;
using DropSpace.Core.Undo;
using DropSpace.Infrastructure.Data;
using DropSpace.Infrastructure.Preview;
using DropSpace.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

var deadline = TimeSpan.FromSeconds(5);
var paths = new AppStoragePaths(Path.Combine(Path.GetTempPath(), "DropSpace-pin-undo-probe", Guid.NewGuid().ToString("N")));
try
{
    var database = new SqliteDatabase(paths, NullLogger<SqliteDatabase>.Instance);
    var repository = new SqliteItemRepository(database, NullLogger<SqliteItemRepository>.Instance);
    var pinItem = await repository.AddTextAsync(ContentClassifier.CreateTextCandidate("pin remains applied"));
    var removeItem = await repository.AddTextAsync(ContentClassifier.CreateTextCandidate("newer removal remains undoable"));
    var proxy = DispatchProxy.Create<IItemRepository, RepositoryCommitBarrier>();
    var barrier = (RepositoryCommitBarrier)proxy;
    barrier.Repository = repository;
    await using var undo = new UndoCoordinator(proxy, new FilePayloadStore(paths),
        new FilePreviewCache(paths), NullLogger<UndoCoordinator>.Instance);
    var pins = new PinItemsUseCase(undo);
    var publications = new ConcurrentQueue<UndoState>();
    undo.StateChanged += (_, _) =>
    {
        if (undo.State is { } state) publications.Enqueue(state);
    };

    using var caller = new CancellationTokenSource();
    Task<BatchPinResult>? pin = null;
    Task<UndoState?>? removal = null;
    try
    {
        pin = pins.ToggleAsync([pinItem], caller.Token);
        await barrier.Committed.Task.WaitAsync(deadline);
        Check((await repository.GetAsync(pinItem.Id))?.IsPinned == true,
            "Fixture barrier must be after the actual SQLite pin commit.");

        removal = undo.BeginRemovalAsync([removeItem.Id], UndoOperationKind.RemoveItem, "UndoRemoved");
        Check(!removal.IsCompleted && barrier.RemovalCalls == 0,
            "Newer removal crossed the pin operation's still-owned Undo gate.");
        await caller.CancelAsync();
        barrier.Release.TrySetResult();
        await Task.WhenAll(pin, removal).WaitAsync(deadline);
        var pinResult = await pin;
        var removalState = await removal;
        Check(pinResult.AffectedCount == 1 && pinResult.PreviousStates.TryGetValue(pinItem.Id, out var wasPinned) && !wasPinned,
            "Postcommit cancellation lost the actual pin result and previous state.");
        var states = publications.ToArray();
        Check(states.Length == 2 && states[0].Kind == UndoOperationKind.PinChange &&
            states[1].Kind == UndoOperationKind.RemoveItem,
            "Undo publication did not preserve committed Pin then newer Remove order.");
        Check(removalState is not null && undo.State?.Token == removalState.Token &&
            states[1].Token == removalState.Token && barrier.RemovalCalls == 1,
            "Late pin continuation replaced the newer removal's Undo owner.");
        Check((await repository.QueryAsync(new ItemQuery())).All(item => item.Id != removeItem.Id),
            "Removal did not hide its pending row.");
        Check(await undo.UndoAsync().WaitAsync(deadline), "Newer removal could not be undone.");
        Check((await repository.GetAsync(removeItem.Id))?.Text?.InlineText == removeItem.Text?.InlineText,
            "Undo did not restore the actual removed SQLite item.");
        Check((await repository.GetAsync(pinItem.Id))?.IsPinned == true,
            "Undo applied to the older pin instead of the newer removal.");
        Check(await repository.CountAsync(ItemSource.Clipboard) == 2 && undo.State is null,
            "Final temporary history or Undo ownership is inconsistent.");
        Console.WriteLine("PASS one pin/Undo scenario: real pin commit blocked at return; Remove cannot enter; postcommit cancellation preserves Pin then Remove publication; current removal token undoes B while A remains pinned; two real SQLite rows.");
    }
    finally
    {
        barrier.Release.TrySetResult();
        if (pin is not null) await pin.WaitAsync(deadline);
        if (removal is not null) await removal.WaitAsync(deadline);
    }
}
finally
{
    SqliteConnection.ClearAllPools();
    if (Directory.Exists(paths.Root)) Directory.Delete(paths.Root, recursive: true);
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

// The proxy delegates all storage to the real repository. Its only barrier is
// after the actual pin commit but before its Task result reaches the coordinator.
public class RepositoryCommitBarrier : DispatchProxy
{
    public IItemRepository Repository { get; set; } = null!;
    public TaskCompletionSource Committed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _removalCalls;
    public int RemovalCalls => Volatile.Read(ref _removalCalls);

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        if (targetMethod.Name == nameof(IItemRepository.BeginPendingRemovalAsync))
            Interlocked.Increment(ref _removalCalls);
        object? result;
        try { result = targetMethod.Invoke(Repository, args); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
        return targetMethod.Name == nameof(IItemRepository.SetPinnedManyAsync)
            ? HoldCommittedPinAsync((Task<BatchPinResult>)result!)
            : result;
    }

    private async Task<BatchPinResult> HoldCommittedPinAsync(Task<BatchPinResult> work)
    {
        var result = await work;
        Committed.TrySetResult();
        // Intentionally no caller cancellation: the real repository already committed.
        await Release.Task;
        return result;
    }
}
