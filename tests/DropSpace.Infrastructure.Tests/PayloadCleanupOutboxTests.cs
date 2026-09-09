using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using DropSpace.Core.Policies;
using DropSpace.Infrastructure.Data;
using DropSpace.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class PayloadCleanupOutboxTests
{
    private string _root = null!;
    private AppStoragePaths _paths = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "DropSpace-outbox-tests", Guid.NewGuid().ToString("N"));
        _paths = new AppStoragePaths(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public async Task FailedPhysicalDeleteRetainsTheOutboxAndRecordsAStableCategory()
    {
        var repository = CreateRepository();
        var payloadStore = new FilePayloadStore(_paths);
        var payload = await WritePayloadAsync(payloadStore);
        var item = await repository.AddImageAsync(new ImageCandidate(
            payload.ContentHash, 1, 1, payload.ByteLength, "image/png", false, payload));
        Assert.AreEqual(item.Id, (await repository.GetAsync(item.Id))!.Id);
        await repository.RemoveAsync(item.Id);

        var failingStore = new TestPayloadStore(_paths, failDeletes: true);
        var cleanup = CreateCoordinator(repository, failingStore);
        Assert.AreEqual(0, await cleanup.DrainAsync());

        var retained = (await repository.GetPendingPayloadDeletesAsync()).Single();
        Assert.AreEqual(1, retained.AttemptCount);
        Assert.AreEqual(nameof(IOException), retained.LastErrorCategory);
        Assert.IsTrue(File.Exists(payloadStore.ResolvePath(payload.RelativePath)));

        var retry = CreateCoordinator(repository, payloadStore);
        Assert.AreEqual(1, await retry.DrainAsync());
        Assert.AreEqual(0, (await repository.GetPendingPayloadDeletesAsync()).Count);
        Assert.IsFalse(File.Exists(payloadStore.ResolvePath(payload.RelativePath)));
    }

    [TestMethod]
    public async Task MissingPhysicalPayloadIsAnIdempotentOutboxSuccess()
    {
        var repository = CreateRepository();
        var payloadStore = new FilePayloadStore(_paths);
        var payload = await WritePayloadAsync(payloadStore);
        var item = await repository.AddImageAsync(new ImageCandidate(
            payload.ContentHash, 1, 1, payload.ByteLength, "image/png", false, payload));
        await repository.RemoveAsync(item.Id);
        File.Delete(payloadStore.ResolvePath(payload.RelativePath));

        Assert.AreEqual(1, await CreateCoordinator(repository, payloadStore).DrainAsync());
        Assert.AreEqual(0, (await repository.GetPendingPayloadDeletesAsync()).Count);
    }

    [TestMethod]
    public async Task MalformedOutboxPathCannotReachThePayloadStoreOrAnExternalSentinel()
    {
        var repository = CreateRepository();
        await repository.InitializeAsync();
        var sentinel = Path.Combine(_root, "external-sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "keep");
        var database = new SqliteDatabase(_paths, NullLogger<SqliteDatabase>.Instance);
        await using (var connection = await database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO payload_delete_outbox
                    (id, relative_path, created_at_utc, attempt_count, last_attempt_at_utc, last_error_category)
                VALUES ('malformed-entry', '../external-sentinel.txt', @created, 0, NULL, NULL);
                """;
            command.Parameters.AddWithValue("@created", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        var store = new TestPayloadStore(_paths, failDeletes: false);
        Assert.AreEqual(0, await CreateCoordinator(repository, store).DrainAsync());
        var retained = (await repository.GetPendingPayloadDeletesAsync()).Single();
        Assert.AreEqual("malformed-relative-path", retained.LastErrorCategory);
        Assert.AreEqual(0, store.DeleteCalls);
        Assert.IsTrue(File.Exists(sentinel));
    }

    private SqliteItemRepository CreateRepository() =>
        new(new SqliteDatabase(_paths, NullLogger<SqliteDatabase>.Instance), NullLogger<SqliteItemRepository>.Instance);

    private PayloadCleanupCoordinator CreateCoordinator(SqliteItemRepository repository, IPayloadStore payloads) =>
        new(
            _paths,
            repository,
            payloads,
            new OwnedPayloadReconciler(_paths, repository, NullLogger<OwnedPayloadReconciler>.Instance),
            NullLogger<PayloadCleanupCoordinator>.Instance);

    private static async Task<PayloadRecord> WritePayloadAsync(FilePayloadStore store)
    {
        await using var input = new MemoryStream([1, 2, 3, 4]);
        return await store.WriteFileAsync("images", ".bin", input, 1024);
    }

    private sealed class TestPayloadStore(AppStoragePaths paths, bool failDeletes) : IPayloadStore
    {
        public int DeleteCalls { get; private set; }

        public Task<PayloadRecord> WriteAsync(string kind, Stream source, long maximumBytes, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PayloadRecord> WriteFileAsync(string kind, string? extension, Stream source, long maximumBytes, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            DeleteCalls++;
            if (failDeletes)
            {
                throw new IOException("simulated locked payload");
            }

            File.Delete(ResolvePath(relativePath));
            return Task.CompletedTask;
        }

        public Task ExportAsync(string relativePath, string destinationPath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public string ResolvePath(string relativePath) => PayloadPathPolicy.ResolveContainedPath(paths.Payloads, relativePath);
    }
}
