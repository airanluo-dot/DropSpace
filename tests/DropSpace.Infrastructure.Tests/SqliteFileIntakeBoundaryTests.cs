using DropSpace.Core.Models;
using DropSpace.Infrastructure.Data;
using DropSpace.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class SqliteFileIntakeBoundaryTests
{
    private AppStoragePaths _paths = null!;
    private SqliteDatabase _database = null!;
    private SqliteItemRepository _repository = null!;
    private FileCandidate _existingCandidate = null!;
    private Guid _existingId;

    [TestInitialize]
    public async Task InitializeAsync()
    {
        _paths = new(Path.Combine(Path.GetTempPath(), "DropSpace-file-intake-tests", Guid.NewGuid().ToString("N")));
        _database = new(_paths, NullLogger<SqliteDatabase>.Instance);
        _repository = new(_database, NullLogger<SqliteItemRepository>.Instance);
        _existingCandidate = Candidate("existing.txt");
        _existingId = (await _repository.AddSpaceFileAsync(_existingCandidate, "{\"batch\":\"original\"}")).Id;
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, recursive: true);
    }

    [TestMethod]
    public async Task FileIntakeReturnsToCallerWhileInitializedTableIsLocked()
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var transaction = connection.BeginTransaction();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE items SET title = title WHERE id = @id;";
        command.Parameters.AddWithValue("@id", _existingId.ToByteArray());
        await command.ExecuteNonQueryAsync();

        // This real shared-cache table lock blocks the duplicate lookup, after
        // initialization has completed. It must not hold the invoking thread.
        await AssertCallerCanContinueAsync(
            () => _repository.AddSpaceFileAsync(Candidate("new.txt"), null),
            transaction.Rollback);
        Assert.AreEqual(2, await _repository.CountAsync(ItemSource.Space));
        Assert.AreEqual(1, _database.WriteGate.CurrentCount);
    }

    [TestMethod]
    public async Task FileAdmissionOrderAndExistingMetadataSurviveQueuedCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var candidate = Candidate("queued.txt");
        await _database.WriteGate.WaitAsync();
        Task<DropItem> first;
        Task<DropItem> last;
        try
        {
            first = _repository.AddSpaceFileAsync(candidate, "{\"batch\":\"first\"}");
            var canceled = _repository.AddSpaceFileAsync(Candidate("canceled.txt"), null, cancellation.Token);
            last = _repository.AddSpaceFileAsync(candidate, "{\"batch\":\"last\"}");
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

        var firstItem = await first;
        var lastItem = await last;
        Assert.AreEqual(firstItem.Id, lastItem.Id);
        Assert.AreEqual("{\"batch\":\"first\"}", lastItem.MetadataJson);
        Assert.AreEqual(firstItem.Revision, lastItem.Revision);
        Assert.AreEqual(2, await _repository.CountAsync(ItemSource.Space));
        Assert.AreEqual(1, _database.WriteGate.CurrentCount);
    }

    [TestMethod]
    public async Task CancellationAfterFileAdmissionReleasesOwnerAndPreservesExistingReference()
    {
        using var blocker = new DuplicateReadBlocker();
        using var cancellation = new CancellationTokenSource();
        var repository = new SqliteItemRepository(_database, blocker);
        var pending = Task.Run(() => repository.AddSpaceFileAsync(
            _existingCandidate, "{\"batch\":\"replacement\"}", cancellation.Token));
        try
        {
            await blocker.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(0, _database.WriteGate.CurrentCount);
            cancellation.Cancel();
        }
        finally
        {
            blocker.Release();
        }

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(1, _database.WriteGate.CurrentCount);
        var item = (await _repository.GetAsync(_existingId))!;
        Assert.AreEqual("{\"batch\":\"original\"}", item.MetadataJson);
        Assert.AreEqual(1, item.Revision);
        Assert.AreEqual(1, await _repository.CountAsync(ItemSource.Space));
        Assert.AreEqual(_existingId, (await _repository.AddSpaceFileAsync(_existingCandidate, null)).Id);
    }

    private FileCandidate Candidate(string title)
    {
        var path = Path.Combine(_paths.Root, title);
        return new(path, path, FileEntryKind.File, title, ".txt", 12, null, ItemStatus.Available, null);
    }

    private static async Task AssertCallerCanContinueAsync(Func<Task> invoke, Action releaseOwner)
    {
        using var releaseCaller = new ManualResetEventSlim();
        var returned = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var caller = new Thread(() =>
        {
            try { returned.TrySetResult(invoke()); }
            catch (Exception error) { returned.TrySetException(error); }
            releaseCaller.Wait();
        }) { IsBackground = true };
        caller.Start();
        try
        {
            var pending = await returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(pending.IsCompleted);
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

    private sealed class DuplicateReadBlocker : ILogger<SqliteItemRepository>, IDisposable
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
