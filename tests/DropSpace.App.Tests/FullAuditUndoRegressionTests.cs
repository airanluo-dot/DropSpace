using System.Collections.Concurrent;
using System.Reflection;
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

namespace DropSpace.App.Tests;

[TestClass]
public sealed class FullAuditUndoRegressionTests
{
    [TestMethod]
    public async Task StartupRecoveryRestoresRemovalWhoseUndoOwnerWasLost()
    {
        await WithRepositoryAsync(async (repository, paths) =>
        {
            var item = await repository.AddTextAsync(ContentClassifier.CreateTextCandidate("crash during undo window"));
            await repository.BeginPendingRemovalAsync([item.Id], "lost-process-token", DateTimeOffset.UtcNow.Add(UndoCoordinator.UndoWindow));
            await using var restarted = CreateUndo(repository, paths);

            await restarted.RecoverStaleAsync();

            Assert.IsNotNull(await repository.GetAsync(item.Id));
            Assert.IsNull(restarted.State);
            Assert.AreEqual(0, ExpirationTasks(restarted).Count);
        });
    }

    [TestMethod]
    public async Task RecoveryDuringAnActiveUndoDoesNotRestoreItsRemoval()
    {
        await WithRepositoryAsync(async (repository, paths) =>
        {
            var item = await repository.AddTextAsync(ContentClassifier.CreateTextCandidate("current undo window"));
            await using var undo = CreateUndo(repository, paths);
            var active = await undo.BeginRemovalAsync([item.Id], UndoOperationKind.RemoveItem, "test");

            await undo.RecoverStaleAsync();

            Assert.IsNull(await repository.GetAsync(item.Id));
            Assert.AreEqual(active, undo.State);
            Assert.IsTrue(await undo.UndoAsync());
            Assert.IsNotNull(await repository.GetAsync(item.Id));
        });
    }

    [TestMethod]
    public async Task SubscriberFailureDoesNotPreventRemovalExpirationOwnership()
    {
        await WithRepositoryAsync(async (repository, paths) =>
        {
            var item = await repository.AddTextAsync(ContentClassifier.CreateTextCandidate("subscriber test"));
            await using var undo = CreateUndo(repository, paths);
            var laterCalls = 0;
            undo.StateChanged += (_, _) => throw new InvalidOperationException("subscriber failure");
            undo.StateChanged += (_, _) => laterCalls++;
            Assert.IsNotNull(await undo.BeginRemovalAsync([item.Id], UndoOperationKind.RemoveItem, "test"));
            Assert.AreEqual(1, laterCalls);
            Assert.AreEqual(1, ExpirationTasks(undo).Count);
        });
    }

    [TestMethod]
    public async Task FailedUndoKeepsOriginalRemovalExpiration()
    {
        await WithRepositoryAsync(async (repository, paths) =>
        {
            var item = await repository.AddTextAsync(ContentClassifier.CreateTextCandidate("failed undo test"));
            var proxy = DispatchProxy.Create<IItemRepository, FailUndoRepository>();
            ((FailUndoRepository)(object)proxy).Target = repository;
            await using var undo = CreateUndo(proxy, paths);
            await undo.BeginRemovalAsync([item.Id], UndoOperationKind.RemoveItem, "test");
            var expiration = Task.WhenAll(ExpirationTasks(undo).Values);
            await Assert.ThrowsAsync<IOException>(() => undo.UndoAsync());
            await expiration.WaitAsync(UndoCoordinator.UndoWindow + TimeSpan.FromSeconds(3));
            Assert.IsNull(undo.State, "A failed undo must still finalize at the original deadline.");
            Assert.IsNull(await repository.GetAsync(item.Id));
        });
    }

    private static UndoCoordinator CreateUndo(IItemRepository repository, AppStoragePaths paths) =>
        new(repository, new FilePayloadStore(paths), new FilePreviewCache(paths), NullLogger<UndoCoordinator>.Instance);

    private static ConcurrentDictionary<string, Task> ExpirationTasks(UndoCoordinator undo) =>
        (ConcurrentDictionary<string, Task>)typeof(UndoCoordinator).GetField("_expirationTasks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(undo)!;

    private static async Task WithRepositoryAsync(Func<SqliteItemRepository, AppStoragePaths, Task> action)
    {
        var paths = new AppStoragePaths(Path.Combine(Path.GetTempPath(), "DropSpace-audit-undo", Guid.NewGuid().ToString("N")));
        try
        {
            var repository = new SqliteItemRepository(new SqliteDatabase(paths, NullLogger<SqliteDatabase>.Instance), NullLogger<SqliteItemRepository>.Instance);
            await action(repository, paths);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(paths.Root)) Directory.Delete(paths.Root, true);
        }
    }

    public class FailUndoRepository : DispatchProxy
    {
        public IItemRepository Target { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod!.Name == nameof(IItemRepository.UndoPendingRemovalAsync)
                ? Task.FromException<int>(new IOException("Injected undo persistence failure"))
                : targetMethod.Invoke(Target, args);
    }
}
