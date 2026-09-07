using DropSpace.Core.Policies;
using DropSpace.Infrastructure.Data;
using DropSpace.Infrastructure.Network;
using DropSpace.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class DatabaseWriteBoundaryTests
{
    [TestMethod]
    public async Task BothRepositoriesWaitForDatabaseOwnerAndCancellationPreservesItsLease()
    {
        var paths = new AppStoragePaths(Path.Combine(Path.GetTempPath(), "DropSpace-tests", Guid.NewGuid().ToString("N")));
        try
        {
            var database = new SqliteDatabase(paths, NullLogger<SqliteDatabase>.Instance);
            await database.InitializeAsync();
            var items = new SqliteItemRepository(database, NullLogger<SqliteItemRepository>.Instance);
            var transfers = new TransferRepository(database);
            using var cancellation = new CancellationTokenSource();
            await database.WriteGate.WaitAsync();
            try
            {
                var itemWrite = items.AddTextAsync(ContentClassifier.CreateTextCandidate("test"), cancellation.Token);
                var peerWrite = transfers.DeletePeerAsync(Guid.NewGuid(), cancellation.Token);
                Assert.IsFalse(itemWrite.IsCompleted);
                Assert.IsFalse(peerWrite.IsCompleted);
                cancellation.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => itemWrite);
                await Assert.ThrowsAsync<OperationCanceledException>(() => peerWrite);
                Assert.AreEqual(0, database.WriteGate.CurrentCount);
            }
            finally
            {
                database.WriteGate.Release();
            }

            await items.AddTextAsync(ContentClassifier.CreateTextCandidate("retry"));
            await transfers.DeletePeerAsync(Guid.NewGuid());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(paths.Root)) Directory.Delete(paths.Root, true);
        }
    }

    [TestMethod]
    public async Task CancelledInitializationDoesNotCreateStorage()
    {
        var paths = new AppStoragePaths(Path.Combine(Path.GetTempPath(), "DropSpace-tests", Guid.NewGuid().ToString("N")));
        var database = new SqliteDatabase(paths, NullLogger<SqliteDatabase>.Instance);
        await Assert.ThrowsAsync<OperationCanceledException>(() => database.InitializeAsync(new CancellationToken(true)));
        Assert.IsFalse(Directory.Exists(paths.Root));
    }
}
