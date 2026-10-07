using DropSpace.Core.Models;
using DropSpace.Core.Policies;
using DropSpace.Infrastructure.Data;
using DropSpace.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class SqliteUiExecutionBoundaryTests
{
    private AppStoragePaths _paths = null!;
    private SqliteDatabase _database = null!;
    private SqliteItemRepository _repository = null!;
    private Guid _fileId;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _paths = new(Path.Combine(Path.GetTempPath(), "DropSpace-sqlite-ui-tests", Guid.NewGuid().ToString("N")));
        _database = new(_paths, NullLogger<SqliteDatabase>.Instance);
        _repository = new(_database, NullLogger<SqliteItemRepository>.Instance);
        var filePath = Path.Combine(_paths.Root, "fixture.txt");
        var item = await _repository.AddFileAsync(new(
            filePath, filePath, FileEntryKind.File, "fixture.txt", ".txt", 12, null,
            ItemStatus.Available, null));
        _fileId = item.Id;
        await _repository.AddTextAsync(ContentClassifier.CreateTextCandidate("clipboard fixture"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, recursive: true);
    }

    [TestMethod]
    public async Task InitializationReturnsToCallerWhileDatabaseOwnerFinishes()
    {
        using var blocker = new InitializationBlocker();
        var database = new SqliteDatabase(_paths, blocker);

        await AssertCallerCanContinueAsync(() => database.InitializeAsync(), blocker);
        await Assert.ThrowsAsync<OperationCanceledException>(() => database.InitializeAsync(new CancellationToken(true)));
        Assert.IsTrue(database.InitializeAsync().IsCompletedSuccessfully);
    }

    [TestMethod]
    [DataRow("count")]
    [DataRow("countClipboard")]
    [DataRow("get")]
    public async Task ReadsReturnToCallerWhileInitializedTableIsLocked(string operation)
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE items SET title = title WHERE id = @id;";
        command.Parameters.AddWithValue("@id", _fileId.ToByteArray());
        await command.ExecuteNonQueryAsync();
        Task ReadAsync() => operation switch
        {
            "count" => _repository.CountAsync(ItemSource.Space),
            "countClipboard" => _repository.CountClipboardAsync(null, includePinned: false),
            "get" => _repository.GetAsync(_fileId),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

        // The shared-cache table lock holds the real SQLite call, even though
        // initialization is already complete. The caller must remain available.
        await AssertCallerCanContinueAsync(ReadAsync, transaction.Rollback);
        Assert.AreEqual(1, await _repository.CountAsync(ItemSource.Space));
        Assert.AreEqual(1, await _repository.CountClipboardAsync(null, includePinned: false));
        Assert.AreEqual(_fileId, (await _repository.GetAsync(_fileId))!.Id);
    }

    [TestMethod]
    public async Task PinAdmissionOrderAndInputSnapshotSurviveQueuedCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await _database.WriteGate.WaitAsync();
        Task<BatchPinResult> first;
        Task<BatchPinResult> last;
        try
        {
            var ids = new List<Guid> { _fileId };
            first = _repository.SetPinnedManyAsync(ids, true);
            ids.Clear();
            var canceled = _repository.UpdateFileStatusAsync(_fileId, ItemStatus.Missing, "canceled", cancellation.Token);
            last = _repository.SetPinnedManyAsync([_fileId], false);
            Assert.IsFalse(first.IsCompleted);
            Assert.IsFalse(last.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => canceled);
            Assert.AreEqual(0, _database.WriteGate.CurrentCount);
        }
        finally
        {
            _database.WriteGate.Release();
        }

        Assert.IsFalse((await first).PreviousStates[_fileId]);
        Assert.IsTrue((await last).PreviousStates[_fileId]);
        var item = (await _repository.GetAsync(_fileId))!;
        Assert.IsFalse(item.IsPinned);
        Assert.AreEqual(ItemStatus.Available, item.Status);
        Assert.AreEqual(1, _database.WriteGate.CurrentCount);
    }

    [TestMethod]
    public async Task CancellationAfterWriteAdmissionReleasesOwnerAndPreservesData()
    {
        using var blocker = new InitializationBlocker();
        using var cancellation = new CancellationTokenSource();
        var database = new SqliteDatabase(_paths, blocker);
        var repository = new SqliteItemRepository(database, NullLogger<SqliteItemRepository>.Instance);
        var pending = repository.SetPinnedManyAsync([_fileId], true, cancellation.Token);
        try
        {
            await blocker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(0, database.WriteGate.CurrentCount);
            cancellation.Cancel();
        }
        finally
        {
            blocker.Release();
        }

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(1, database.WriteGate.CurrentCount);
        Assert.IsFalse((await repository.GetAsync(_fileId))!.IsPinned);
        Assert.AreEqual(1, (await repository.SetPinnedManyAsync([_fileId], true)).AffectedCount);
    }

    [TestMethod]
    public async Task BackgroundWritesPreservePinUndoUsageAndAvailabilityRecords()
    {
        var before = (await _repository.GetAsync(_fileId))!;
        var pinned = await _repository.SetPinnedManyAsync([_fileId], true);
        await _repository.MarkUsedAsync(_fileId);
        await _repository.UpdateFileStatusAsync(_fileId, ItemStatus.Missing, "fixture missing");
        var item = (await _repository.GetAsync(_fileId))!;
        Assert.IsTrue(item.IsPinned);
        Assert.IsNotNull(item.LastUsedAtUtc);
        Assert.AreEqual(ItemStatus.Missing, item.Status);
        Assert.AreEqual("fixture missing", item.File!.AvailabilityReason);
        Assert.IsNotNull(item.File.LastCheckedAtUtc);
        Assert.AreEqual(before.Revision + 3, item.Revision);

        await _database.WriteGate.WaitAsync();
        Task<int> restored;
        try
        {
            var previousStates = new Dictionary<Guid, bool>(pinned.PreviousStates);
            restored = _repository.RestorePinnedStatesAsync(previousStates);
            previousStates.Clear();
            Assert.IsFalse(restored.IsCompleted);
        }
        finally
        {
            _database.WriteGate.Release();
        }

        Assert.AreEqual(1, await restored);
        Assert.IsFalse((await _repository.GetAsync(_fileId))!.IsPinned);
        Assert.AreEqual(1, _database.WriteGate.CurrentCount);
    }

    private static Task AssertCallerCanContinueAsync(Func<Task> invoke, InitializationBlocker blocker) =>
        AssertCallerCanContinueAsync(invoke, blocker.Release, blocker.Entered.Task);

    private static async Task AssertCallerCanContinueAsync(Func<Task> invoke, Action releaseOwner, Task<int>? ownerThread = null)
    {
        using var releaseCaller = new ManualResetEventSlim();
        var returned = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callerThread = 0;
        var caller = new Thread(() =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            try { returned.TrySetResult(invoke()); }
            catch (Exception error) { returned.TrySetException(error); }
            releaseCaller.Wait();
        }) { IsBackground = true };
        caller.Start();
        try
        {
            var pending = await returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(pending.IsCompleted);
            if (ownerThread is not null)
                Assert.AreNotEqual(callerThread, await ownerThread.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            releaseOwner();
            releaseCaller.Set();
            Assert.IsTrue(caller.Join(TimeSpan.FromSeconds(5)));
            var pending = await returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class InitializationBlocker : ILogger<SqliteDatabase>, IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        public TaskCompletionSource<int> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Information) return;
            Entered.TrySetResult(Environment.CurrentManagedThreadId);
            _release.Wait();
        }
        public void Release() => _release.Set();
        public void Dispose() => _release.Dispose();
    }
}
