using DropSpace.Core.Policies;
using DropSpace.Infrastructure.Data;
using DropSpace.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class AuditHardeningTests
{
    [TestMethod]
    public async Task RetentionNeverDeletesPinnedClipboardItemsAndReportsActualRows()
    {
        var paths = Paths();
        try
        {
            var repository = Repository(paths);
            var first = await repository.AddTextAsync(ContentClassifier.CreateTextCandidate("first"));
            var second = await repository.AddTextAsync(ContentClassifier.CreateTextCandidate("second"));
            await repository.SetPinnedAsync(first.Id, true);

            var result = await repository.ApplyRetentionAsync(DateTimeOffset.UtcNow.AddDays(1), 1);
            Assert.AreEqual(1, result.RemovedCount);
            Assert.IsNotNull(await repository.GetAsync(first.Id));
            Assert.IsNull(await repository.GetAsync(second.Id));
        }
        finally { Cleanup(paths); }
    }

    [TestMethod]
    public async Task PayloadDeleteFailureIsRetriedByTheNextStoreInstance()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows sharing semantics are required.");
        var paths = Paths();
        try
        {
            var store = new FilePayloadStore(paths);
            await using var source = new MemoryStream([1, 2, 3, 4]);
            var payload = await store.WriteAsync("files", source, 1024);
            var fullPath = store.ResolvePath(payload.RelativePath);
            await using (var locked = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await Assert.ThrowsAsync<IOException>(() => store.DeleteAsync(payload.RelativePath));
            }

            Assert.IsTrue(File.Exists(fullPath));
            _ = new FilePayloadStore(paths);
            Assert.IsFalse(File.Exists(fullPath));
        }
        finally { Cleanup(paths); }
    }

    private static AppStoragePaths Paths() =>
        new(Path.Combine(Path.GetTempPath(), "DropSpace-audit-tests", Guid.NewGuid().ToString("N")));

    private static SqliteItemRepository Repository(AppStoragePaths paths)
    {
        var database = new SqliteDatabase(paths, NullLogger<SqliteDatabase>.Instance);
        return new SqliteItemRepository(database, NullLogger<SqliteItemRepository>.Instance);
    }

    private static void Cleanup(AppStoragePaths paths)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(paths.Root)) Directory.Delete(paths.Root, true);
    }
}
