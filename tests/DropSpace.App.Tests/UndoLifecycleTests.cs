using System.Runtime.Versioning;
using DropSpace.App.Services;
using DropSpace.Core.Models;
using DropSpace.Core.Policies;
using DropSpace.Core.Preview;
using DropSpace.Core.Undo;
using DropSpace.Infrastructure.Data;
using DropSpace.Infrastructure.Preview;
using DropSpace.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DropSpace.App.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class UndoLifecycleTests
{
    [TestMethod]
    public async Task ConcurrentShutdownFinalizesRemovalAndInvalidatesPreviewCache()
    {
        var paths = new AppStoragePaths(Path.Combine(Path.GetTempPath(), "DropSpace-undo-tests", Guid.NewGuid().ToString("N")));
        try
        {
            var repository = new SqliteItemRepository(new SqliteDatabase(paths, NullLogger<SqliteDatabase>.Instance), NullLogger<SqliteItemRepository>.Instance);
            var item = await repository.AddTextAsync(ContentClassifier.CreateTextCandidate("test"));
            var cache = new FilePreviewCache(paths);
            var request = new PreviewRequest(DropItemSnapshot.FromItem(item));
            var descriptor = new PreviewDescriptor(item.Id, PreviewKind.Text, "test", "text/plain", "test", null, null, null, null, null, new Dictionary<string, string>());
            await cache.PutAsync(request, descriptor);
            var undo = new UndoCoordinator(repository, new FilePayloadStore(paths), cache, NullLogger<UndoCoordinator>.Instance);
            await undo.BeginRemovalAsync([item.Id], UndoOperationKind.RemoveItem, "test");
            var first = undo.DisposeAsync().AsTask();
            var second = undo.DisposeAsync().AsTask();
            Assert.AreSame(first, second);
            await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(0, (await repository.QueryAsync(new ItemQuery())).Count);
            Assert.IsFalse(Directory.Exists(paths.Previews));
            await cache.PutAsync(request, descriptor);
            Assert.IsFalse(Directory.Exists(paths.Previews));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(paths.Root)) Directory.Delete(paths.Root, true);
        }
    }
}
