using System.Security.Cryptography;
using DropSpace.Core.Actions;
using DropSpace.Core.Downloads;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Actions;
using DropSpace.Infrastructure.Data;
using DropSpace.Infrastructure.Downloads;
using DropSpace.Infrastructure.Settings;
using DropSpace.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class OutputRegistrationLifetimeTests
{
    [TestMethod]
    public async Task ConfirmedDownloadIsRegisteredEvenWhenItsCompletionCheckpointFails()
    {
        await using var fixture = new Fixture();
        await fixture.Recent.StartAsync();
        var bytes = "a final download, already atomically committed"u8.ToArray();
        var path = Path.Combine(fixture.Root, "completed.txt");
        await File.WriteAllBytesAsync(path, bytes);
        var request = DirectFileRequestFactory.Create("http://127.0.0.1:1/not-requested", fixture.Root, "completed.txt");
        var journal = new FailingCompletionJournal(new DownloadTaskSnapshot
        {
            Id = request.TaskId, Request = request, OutputPath = path,
            State = DownloadTaskState.Finalizing, TotalBytes = bytes.Length,
            FinalSha256 = Convert.ToHexString(SHA256.HashData(bytes)),
        });
        using var engine = new HttpRangeDownloader();
        await using var manager = new DownloadManager(engine, journal, recent: fixture.Recent);

        await manager.RestoreAsync();

        Assert.IsTrue(journal.CompletionFailed, "The fixture must reach the postcommit persistence failure.");
        Assert.AreEqual(DownloadTaskState.Completed, manager.Tasks.Single().State);
        var result = await fixture.Repository.FindRecentFileByPathAsync(path);
        Assert.IsNotNull(result, "A real completed file remains retrievable despite its download checkpoint failing.");
        Assert.AreEqual("Download", RecentItemMetadata.Read(result.MetadataJson)?.Operation);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(path));
        Assert.AreEqual(0, engine.Transfers.Active, "Recovery verifies local committed bytes; it does not start a download.");
    }

    [TestMethod]
    public async Task ActionShutdownDrainsCommittedOutputRegistrationAndRejectsNewActions()
    {
        await using var fixture = new Fixture();
        await fixture.Recent.StartAsync();
        var action = new HeldSuccessfulAction(Path.Combine(fixture.Root, "result.zip"));
        await using var registry = new ItemActionRegistry([action], recent: fixture.Recent);
        var context = new ItemActionContext(new ItemSelectionSnapshot([]));
        using var owner = new CancellationTokenSource();
        var execution = registry.ExecuteAsync(ItemActionId.CompressZip, context, owner.Token);
        await action.Committed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var shutdown = registry.ShutdownAsync();
        try
        {
            Assert.IsFalse(shutdown.IsCompleted, "Shutdown must retain the action through result registration.");
            owner.Cancel(); // A late owner cancellation cannot turn an already committed file into a failed result.
            var rejected = await registry.ExecuteAsync(ItemActionId.CompressZip, context);
            Assert.IsFalse(rejected.Succeeded);
            Assert.AreEqual("cancelled", rejected.ErrorCategory);
        }
        finally { action.ReturnResult.TrySetResult(); }
        var result = await execution.WaitAsync(TimeSpan.FromSeconds(5));
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(result.Succeeded);
        Assert.HasCount(1, result.OutputItemIds);
        Assert.AreEqual(result.OutputItemIds[0], (await fixture.Repository.FindRecentFileByPathAsync(action.Path))?.Id);
        Assert.IsTrue(File.Exists(action.Path));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "DropSpace-output-lifetime-" + Guid.NewGuid().ToString("N"));
        public SqliteItemRepository Repository { get; }
        public RecentItemsService Recent { get; }

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            var storage = new AppStoragePaths(Path.Combine(Root, "app-data"));
            Repository = new SqliteItemRepository(new SqliteDatabase(storage, NullLogger<SqliteDatabase>.Instance), NullLogger<SqliteItemRepository>.Instance);
            Recent = new RecentItemsService(Repository, new LocalFileReferenceService(), new JsonSettingsService(storage), storage, NullLogger<RecentItemsService>.Instance);
        }

        public async ValueTask DisposeAsync()
        {
            await Recent.DisposeAsync();
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class FailingCompletionJournal(DownloadTaskSnapshot original) : IDownloadTaskRepository
    {
        public bool CompletionFailed { get; private set; }
        public Task<IReadOnlyList<DownloadTaskSnapshot>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DownloadTaskSnapshot>>([original]);
        public Task UpsertAsync(DownloadTaskSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            if (snapshot.State == DownloadTaskState.Completed && !CompletionFailed)
            {
                CompletionFailed = true;
                throw new IOException("Fixture completion checkpoint unavailable.");
            }
            return Task.CompletedTask;
        }
    }

    private sealed class HeldSuccessfulAction(string path) : IItemAction
    {
        public string Path { get; } = path;
        public TaskCompletionSource Committed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReturnResult { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ItemActionDescriptor Descriptor { get; } = new(ItemActionId.CompressZip, "ActionCompressZip.Text", "ZipFolder", ItemActionGroup.Transform, 30, false, false);
        public ItemActionCapability Evaluate(ItemSelectionSnapshot selection) => new(true, null, Descriptor);
        public async Task<ItemActionResult> ExecuteAsync(ItemActionContext context, CancellationToken cancellationToken = default)
        {
            await File.WriteAllTextAsync(Path, "closed final output", cancellationToken);
            Committed.TrySetResult();
            await ReturnResult.Task;
            return ItemActionResult.Success([Path]);
        }
    }
}
