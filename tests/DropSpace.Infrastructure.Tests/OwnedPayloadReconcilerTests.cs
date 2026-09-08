using DropSpace.Core.Models;
using DropSpace.Infrastructure.Data;
using DropSpace.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class OwnedPayloadReconcilerTests
{
    private string _root = null!;
    private AppStoragePaths _paths = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "DropSpace-reconciler-tests", Guid.NewGuid().ToString("N"));
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
    public async Task ReconciliationRemovesOldTemporaryFilesAndQuarantinesUnknownFinalFiles()
    {
        var repository = new SqliteItemRepository(
            new SqliteDatabase(_paths, NullLogger<SqliteDatabase>.Instance),
            NullLogger<SqliteItemRepository>.Instance);
        var payloadStore = new FilePayloadStore(_paths);
        await using var input = new MemoryStream([1, 2, 3]);
        var ownedPayload = await payloadStore.WriteFileAsync("images", ".bin", input, 1024);
        await repository.AddImageAsync(new ImageCandidate(
            ownedPayload.ContentHash, 1, 1, ownedPayload.ByteLength, "image/png", false, ownedPayload));

        var unknown = Path.Combine(_paths.Payloads, "images", "unknown.bin");
        var temporary = Path.Combine(_paths.Payloads, "images", "orphan.tmp");
        Directory.CreateDirectory(Path.GetDirectoryName(unknown)!);
        await File.WriteAllTextAsync(unknown, "unknown");
        await File.WriteAllTextAsync(temporary, "temporary");
        File.SetLastWriteTimeUtc(unknown, DateTime.UtcNow.AddMinutes(-20));
        File.SetLastWriteTimeUtc(temporary, DateTime.UtcNow.AddMinutes(-20));

        var external = Path.Combine(_root, "external.txt");
        await File.WriteAllTextAsync(external, "keep");
        var reconciler = new OwnedPayloadReconciler(
            _paths,
            repository,
            NullLogger<OwnedPayloadReconciler>.Instance);

        Assert.AreEqual(2, await reconciler.ReconcileAsync(TimeSpan.Zero));
        Assert.IsTrue(File.Exists(payloadStore.ResolvePath(ownedPayload.RelativePath)));
        Assert.IsFalse(File.Exists(unknown));
        Assert.IsFalse(File.Exists(temporary));
        Assert.IsTrue(File.Exists(external));
        Assert.IsTrue(Directory.EnumerateFiles(
            Path.Combine(_paths.Quarantine, "payload-orphans"),
            "unknown.bin",
            SearchOption.AllDirectories).Any());
    }

    [TestMethod]
    public async Task GracePeriodLeavesARecentUnknownFileForARecoverableRetry()
    {
        var repository = new SqliteItemRepository(
            new SqliteDatabase(_paths, NullLogger<SqliteDatabase>.Instance),
            NullLogger<SqliteItemRepository>.Instance);
        _paths.EnsureCreated();
        var unknown = Path.Combine(_paths.Payloads, "recent.bin");
        await File.WriteAllTextAsync(unknown, "recent");

        var reconciler = new OwnedPayloadReconciler(
            _paths,
            repository,
            NullLogger<OwnedPayloadReconciler>.Instance);

        Assert.AreEqual(0, await reconciler.ReconcileAsync(TimeSpan.FromHours(1)));
        Assert.IsTrue(File.Exists(unknown));
    }
}
