using System.IO.Compression;
using DropSpace.Core.Actions;
using DropSpace.Core.Models;
using DropSpace.Core.Preview;
using DropSpace.Infrastructure.Actions;
using DropSpace.Infrastructure.Data;
using DropSpace.Infrastructure.Settings;
using DropSpace.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class RecentItemsTests
{
    private string _root = null!;
    private AppStoragePaths _storage = null!;
    private SqliteItemRepository _repository = null!;
    private JsonSettingsService _settings = null!;
    private readonly LocalFileReferenceService _files = new();

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "DropSpace-recent-tests", Guid.NewGuid().ToString("N"));
        _storage = new AppStoragePaths(Path.Combine(_root, "app-data"));
        _repository = new SqliteItemRepository(new SqliteDatabase(_storage, NullLogger<SqliteDatabase>.Instance), NullLogger<SqliteItemRepository>.Instance);
        _settings = new JsonSettingsService(_storage);
        Directory.CreateDirectory(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [TestMethod]
    public async Task RecentRepository_MergesOutputAndWatcherWithoutLosingIdentityOrMetadata()
    {
        var path = await WriteAsync("first", "same.txt", "one");
        var candidate = await _files.InspectAsync(path);
        var first = await _repository.UpsertRecentFileAsync(candidate, new(Path.GetDirectoryName(path), null, false, false));
        var output = await _repository.UpsertRecentFileAsync(candidate with { NormalizedPath = candidate.NormalizedPath.ToUpperInvariant() }, new(null, "CompressZip", true, true));
        Assert.AreEqual(first.Id, output.Id);
        Assert.IsTrue(RecentItemMetadata.Read(output.MetadataJson)!.IsPartial);
        var repeat = await _repository.UpsertRecentFileAsync(candidate, new(Path.GetDirectoryName(path), null, false, false));
        Assert.AreEqual(output.Revision, repeat.Revision, "Duplicate notifications cannot invalidate previews.");
        Assert.IsTrue(RecentItemMetadata.Read(repeat.MetadataJson)!.IsOutput);

        await _repository.RetainRecentAsync(first.Id);
        var retained = await _repository.UpsertRecentFileAsync(candidate with { KnownSize = 8 }, new(null, "ConvertImage", true, false));
        Assert.AreEqual(first.Id, retained.Id);
        Assert.AreEqual(ItemSource.Space, retained.Source);
        Assert.IsTrue(retained.Revision > repeat.Revision);
        Assert.IsFalse(RecentItemMetadata.Read(retained.MetadataJson)!.IsPartial);

        var second = await WriteAsync("second", "same.txt", "two");
        var distinct = await _repository.UpsertRecentFileAsync(await _files.InspectAsync(second), new(null, "ResizeImage", true, false));
        Assert.AreNotEqual(first.Id, distinct.Id, "Same names from different directories are separate objects.");

        var third = await WriteAsync("third", "batch.txt", "batch");
        var staged = await _repository.AddSpaceFileAsync(await _files.InspectAsync(third), "{\"dropBatchId\":\"preserve-me\"}");
        var merged = await _repository.UpsertRecentFileAsync(await _files.InspectAsync(third), new(null, "CompressZip", true, false));
        Assert.AreEqual(staged.Id, merged.Id);
        StringAssert.Contains(merged.MetadataJson!, "preserve-me");
    }

    [TestMethod]
    public async Task FileRelay_IsOptInTopLevelAndWaitsForReadableStableFinalFiles()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "inbox")).FullName;
        var existing = await WriteAsync("inbox", "existing.txt", "before");
        await using var service = CreateService();
        await service.StartAsync();
        Assert.AreEqual(0, service.Directories.Count);
        await WriteAsync("inbox", "not-selected.txt", "ignored");
        await service.AddDirectoryAsync(directory);
        await File.AppendAllTextAsync(existing, " changed");
        await WriteAsync("inbox/nested", "nested.txt", "ignored");
        var temporary = await WriteAsync("inbox", "transfer.crdownload", "final bytes");
        await WriteAsync("inbox", "~$lock.docx", "ignored");
        var reservation = await WriteAsync("inbox", "archive.zip.dropspace-reservation", "internal download reservation");
        await WriteAsync("inbox", "model.gguf", "ignored internal resource");
        var final = Path.Combine(directory, "final.png");
        File.Move(temporary, final);
        await WaitForAsync(async () => (await _repository.FindRecentFileByPathAsync(final))?.Status == ItemStatus.Available);
        Assert.AreEqual(1, await _repository.CountAsync(ItemSource.Recent));
        Assert.IsTrue(File.Exists(reservation));
        Assert.IsNull(await _repository.FindRecentFileByPathAsync(reservation));

        var locked = Path.Combine(directory, "writing.txt");
        await using (var writer = new FileStream(locked, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await writer.WriteAsync("still writing"u8.ToArray());
            await WaitForAsync(async () => (await _repository.FindRecentFileByPathAsync(locked))?.Status == ItemStatus.Processing);
            await Task.Delay(1_200);
            Assert.AreEqual(ItemStatus.Processing, (await _repository.FindRecentFileByPathAsync(locked))!.Status);
        }
        await WaitForAsync(async () => (await _repository.FindRecentFileByPathAsync(locked))?.Status == ItemStatus.Available);
        var captured = (await _repository.FindRecentFileByPathAsync(final))!;
        var output = await service.RegisterOutputAsync(final, "ConvertImage");
        Assert.AreEqual(captured.Id, output!.Id, "Successful output registration merges the watcher object.");
        Assert.AreEqual(2, await _repository.CountAsync(ItemSource.Recent));

        await service.SetDirectoryPausedAsync(directory, true);
        var paused = await WriteAsync("inbox", "while-paused.txt", "ignored");
        await service.SetDirectoryPausedAsync(directory, false);
        await File.AppendAllTextAsync(paused, " changed");
        await Task.Delay(1_300);
        Assert.IsNull(await _repository.FindRecentFileByPathAsync(paused));
        await service.RemoveDirectoryAsync(directory);
        Assert.AreEqual(0, service.Directories.Count);
        Assert.IsTrue(File.Exists(final));
    }

    [TestMethod]
    public async Task FileRelay_OverwriteAndDeletionInvalidateSharedObjectAndRemovalOnlyRemovesRecord()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "inbox")).FullName;
        await using var service = CreateService();
        await service.StartAsync();
        await service.AddDirectoryAsync(directory);
        var path = await WriteAsync("inbox", "note.txt", "original");
        await WaitForAsync(async () => (await _repository.FindRecentFileByPathAsync(path))?.Status == ItemStatus.Available);
        var first = (await _repository.FindRecentFileByPathAsync(path))!;
        await File.WriteAllTextAsync(path, "replacement with new length");
        await WaitForAsync(async () => (await _repository.GetAsync(first.Id)) is { Status: ItemStatus.Available, File.KnownSize: 27 });
        Assert.IsTrue((await _repository.GetAsync(first.Id))!.Revision > first.Revision);
        var moved = Path.Combine(directory, "moved.txt");
        File.Move(path, moved);
        await WaitForAsync(async () => (await _repository.GetAsync(first.Id))?.Status == ItemStatus.Missing);
        await WaitForAsync(async () => (await _repository.FindRecentFileByPathAsync(moved))?.Status == ItemStatus.Available);
        var newItem = (await _repository.FindRecentFileByPathAsync(moved))!;
        await service.RemoveAsync(newItem.Id);
        await File.AppendAllTextAsync(moved, " after removal");
        await Task.Delay(1_200);
        Assert.IsNull(await _repository.GetAsync(newItem.Id));
        Assert.IsTrue(File.Exists(moved), "Removing a candidate never deletes the external file.");
    }

    [TestMethod]
    public async Task RecentRetention_ProtectsKeptPinnedAndReferencedContentAcrossRestart()
    {
        await using var service = CreateService();
        await service.StartAsync();
        var kept = (await service.RegisterOutputAsync(await WriteAsync("files", "kept.txt", "keep"), "Download"))!;
        var pinned = (await service.RegisterOutputAsync(await WriteAsync("files", "pinned.txt", "pin"), "Download"))!;
        var reference = (await service.RegisterOutputAsync(await WriteAsync("files", "reference.txt", "view"), "Download"))!;
        var disposablePath = await WriteAsync("files", "expired.txt", "original remains");
        var disposable = (await service.RegisterOutputAsync(disposablePath, "Download"))!;
        await service.RetainAsync(kept.Id);
        await _repository.SetPinnedAsync(pinned.Id, true);
        using (service.Protect(reference.Id))
        {
            await AgeAllAsync();
            await service.ApplyRetentionAsync();
            Assert.IsNull(await _repository.GetAsync(disposable.Id));
            Assert.IsNotNull(await _repository.GetAsync(reference.Id));
            Assert.IsNotNull(await _repository.GetAsync(pinned.Id));
            Assert.AreEqual(ItemSource.Space, (await _repository.GetAsync(kept.Id))!.Source);
        }
        await service.ApplyRetentionAsync();
        Assert.IsNull(await _repository.GetAsync(reference.Id));
        Assert.IsTrue(File.Exists(disposablePath));
        await service.DisposeAsync();
        await using var restarted = CreateService();
        await restarted.StartAsync();
        Assert.AreEqual(kept.Id, (await _repository.QueryAsync(new ItemQuery(Source: ItemSource.Space))).Single().Id);
        File.Delete(kept.File!.OriginalPath);
        await restarted.RefreshAsync(kept.Id);
        Assert.AreEqual(ItemStatus.Missing, (await _repository.GetAsync(kept.Id))!.Status);
    }

    [TestMethod]
    public async Task RelaySettings_RestorePauseUnavailableReasonsAndRejectInternalDirectories()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "inbox")).FullName;
        await using (var service = CreateService())
        {
            await service.StartAsync();
            await service.AddDirectoryAsync(directory);
            await service.SetDirectoryPausedAsync(directory, true);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.AddDirectoryAsync(_storage.Data));
            var internalFile = Path.Combine(_storage.Data, "secret.txt");
            await File.WriteAllTextAsync(internalFile, "internal");
            Assert.IsNull(await service.RegisterOutputAsync(internalFile, "Download"));
            Directory.CreateDirectory(_storage.Exports);
            var export = Path.Combine(_storage.Exports, "explicit.txt");
            await File.WriteAllTextAsync(export, "explicit successful output");
            Assert.IsNotNull(await service.RegisterOutputAsync(export, "CompressZip"));
            foreach (var name in new[] { "DropSpace.exe", "DropSpace-x64.msix", "DropSpace-CUDA-llama-cpp-v0.5.0-cuda13-win-x64-v1.zip", "llama-server.exe", "dlc-dropspace.sample-1.0.0.zip", "pinned-ai-runtime.zip" })
            {
                var package = await WriteAsync("downloads", name, "internal package");
                Assert.IsNull(await service.RegisterOutputAsync(package, "Download"), name);
            }
            Assert.IsNotNull(await service.RegisterOutputAsync(await WriteAsync("downloads", "holiday.zip", "ordinary user archive"), "Download"));
            Assert.IsNotNull(await service.RegisterOutputAsync(await WriteAsync("downloads", "my-tool.exe", "ordinary user executable"), "Download"));
        }
        Directory.Delete(directory);
        await using var restored = CreateService();
        await restored.StartAsync();
        Assert.IsTrue(restored.Directories.Single().IsPaused);
        await restored.SetDirectoryPausedAsync(directory, false);
        Assert.IsFalse(restored.Directories.Single().IsAvailable);
        Assert.AreEqual("DirectoryUnavailable", restored.Directories.Single().Reason);
    }

    [TestMethod]
    public async Task Refresh_CannotBypassStabilityAndDoesNotRepeatMissingNotifications()
    {
        await using var service = CreateService();
        await service.StartAsync();
        var path = await WriteAsync("files", "reference.txt", "before");
        var item = (await service.RegisterOutputAsync(path, "Download"))!;
        await File.WriteAllTextAsync(path, "a replacement while reference is open");
        await service.RefreshAsync(item.Id);
        Assert.AreEqual(ItemStatus.Processing, (await _repository.GetAsync(item.Id))!.Status);
        await service.RefreshAsync(item.Id);
        Assert.AreEqual(ItemStatus.Processing, (await _repository.GetAsync(item.Id))!.Status,
            "A second UI refresh must not promote a file before the stability interval.");
        await WaitForAsync(async () => (await _repository.GetAsync(item.Id))?.Status == ItemStatus.Available);
        var available = (await _repository.GetAsync(item.Id))!;
        Assert.IsTrue(available.Revision > item.Revision);
        File.Delete(path);
        await service.RefreshAsync(item.Id);
        var missing = (await _repository.GetAsync(item.Id))!;
        Assert.AreEqual(ItemStatus.Missing, missing.Status);
        await service.RefreshAsync(item.Id);
        Assert.AreEqual(missing.Revision, (await _repository.GetAsync(item.Id))!.Revision);
    }

    [TestMethod]
    public async Task RecentRetention_CountCapAppliesWithoutDeletingOriginalFiles()
    {
        await _settings.SaveAsync(new AppSettings { RecentFiles = new RecentFileSettings { CountLimit = 10 } });
        await using var service = CreateService();
        await service.StartAsync();
        var originals = new List<string>();
        for (var index = 0; index < 13; index++)
        {
            var path = await WriteAsync("files", $"output-{index}.txt", "content");
            originals.Add(path);
            Assert.IsNotNull(await service.RegisterOutputAsync(path, "Download"));
        }
        Assert.AreEqual(10, await _repository.CountAsync(ItemSource.Recent));
        Assert.IsTrue(originals.All(File.Exists));
    }

    [TestMethod]
    public async Task WaitingRecords_ResumeAfterRestartAndPausingStopsOnlyNewDiscovery()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "inbox")).FullName;
        var path = Path.Combine(directory, "writing.txt");
        Guid id;
        await using (var service = CreateService())
        {
            await service.StartAsync();
            await service.AddDirectoryAsync(directory);
            await using (var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await writer.WriteAsync("first version"u8.ToArray());
                await WaitForAsync(async () => (await _repository.FindRecentFileByPathAsync(path))?.Status == ItemStatus.Processing);
                id = (await _repository.FindRecentFileByPathAsync(path))!.Id;
                await service.DisposeAsync();
            }
        }
        await using var restarted = CreateService();
        await restarted.StartAsync();
        await WaitForAsync(async () => (await _repository.GetAsync(id))?.Status == ItemStatus.Available);
        await using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            await writer.WriteAsync("other version"u8.ToArray());
            await writer.FlushAsync();
            // Same-length writes need not produce a Windows LastWrite notification until
            // the writer closes. An explicit retrieval check must still observe the lock.
            await restarted.RefreshAsync(id);
            await WaitForAsync(async () => (await _repository.GetAsync(id))?.Status == ItemStatus.Processing);
            await restarted.SetDirectoryPausedAsync(directory, true);
        }
        await WaitForAsync(async () => (await _repository.GetAsync(id))?.Status == ItemStatus.Available);
        var ignored = await WriteAsync("inbox", "paused-new.txt", "not admitted");
        await Task.Delay(1_100);
        Assert.IsNull(await _repository.FindRecentFileByPathAsync(ignored));
    }

    [TestMethod]
    public async Task RecentOverwrite_RefreshesRetentionAgeAndSameMetadataWatcherInvalidatesPreview()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "inbox")).FullName;
        await using var service = CreateService();
        await service.StartAsync();
        await service.AddDirectoryAsync(directory);
        var path = await WriteAsync("inbox", "result.txt", "first");
        var item = (await service.RegisterOutputAsync(path, "Download"))!;
        await AgeAllAsync();
        await File.WriteAllTextAsync(path, "new output replaces old path");
        var updated = (await service.RegisterOutputAsync(path, "Download"))!;
        Assert.AreEqual(item.Id, updated.Id);
        Assert.IsNotNull(await _repository.GetAsync(item.Id), "A newly completed output cannot immediately expire because the prior revision was old.");
        Assert.IsTrue(updated.CreatedAtUtc > DateTimeOffset.UtcNow.AddMinutes(-1));
        // A real Changed notification remains evidence of replacement when a tool preserves
        // both the original length and timestamp. A new revision retires cached previews.
        await Task.Delay(600);
        var previous = (await _repository.GetAsync(item.Id))!;
        var timestamp = File.GetLastWriteTimeUtc(path);
        await File.WriteAllTextAsync(path, "NEW OUTPUT REPLACES OLD PATH");
        File.SetLastWriteTimeUtc(path, timestamp);
        await WaitForAsync(async () => (await _repository.GetAsync(item.Id)) is { Status: ItemStatus.Available } current && current.Revision > previous.Revision);
        Assert.AreEqual(previous.File!.KnownSize, (await _repository.GetAsync(item.Id))!.File!.KnownSize);
    }

    [TestMethod]
    public async Task FileReplacement_WithDirectoryIsUnavailableWithoutChangingTheOriginalReferenceKind()
    {
        await using var service = CreateService();
        await service.StartAsync();
        var path = await WriteAsync("files", "original.txt", "source");
        var item = (await service.RegisterOutputAsync(path, "Download"))!;
        File.Delete(path);
        Directory.CreateDirectory(path);
        await service.RefreshAsync(item.Id);
        var invalid = (await _repository.GetAsync(item.Id))!;
        Assert.AreEqual(ItemStatus.Unavailable, invalid.Status);
        Assert.AreEqual(FileEntryKind.File, invalid.File!.EntryKind);
        await service.RefreshAsync(item.Id);
        Assert.AreEqual(invalid.Revision, (await _repository.GetAsync(item.Id))!.Revision);
    }

    [TestMethod]
    public async Task ZipAction_RegistersOnlyCommittedOutputAndMergesWatchedDestination()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "exports")).FullName;
        var source = await WriteAsync("source", "hello.txt", "hello archive");
        var item = await _repository.AddFileAsync(await _files.InspectAsync(source));
        await using var service = CreateService();
        await service.StartAsync();
        await service.AddDirectoryAsync(directory);
        var registry = new ItemActionRegistry([new ZipActionService(_storage)], recent: service);
        var context = new ItemActionContext(new ItemSelectionSnapshot([DropItemSnapshot.FromItem(item)]), directory);
        var result = await registry.ExecuteAsync(ItemActionId.CompressZip, context);
        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1, result.OutputItemIds.Count);
        using (var archive = ZipFile.OpenRead(result.OutputPaths.Single()))
        using (var reader = new StreamReader(archive.Entries.Single().Open()))
            Assert.AreEqual("hello archive", await reader.ReadToEndAsync());
        await Task.Delay(2_000);
        Assert.AreEqual(1, await _repository.CountAsync(ItemSource.Recent));
        Assert.AreEqual(result.OutputItemIds.Single(), (await _repository.FindRecentFileByPathAsync(result.OutputPaths.Single()))!.Id);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var cancelled = await registry.ExecuteAsync(ItemActionId.CompressZip, context, cancellation.Token);
        Assert.IsFalse(cancelled.Succeeded);
        Assert.AreEqual("cancelled", cancelled.ErrorCategory);
        File.Delete(source);
        var failed = await registry.ExecuteAsync(ItemActionId.CompressZip, context);
        Assert.IsFalse(failed.Succeeded);
        await Task.Delay(1_200);
        Assert.AreEqual(1, await _repository.CountAsync(ItemSource.Recent), "Failed and cancelled outputs are never successful records.");
        Assert.AreEqual(1, Directory.GetFiles(directory).Length);
    }

    [TestMethod]
    public async Task PartialAction_RegistersEachFinalOutputOnceAndExcludesFailedOrIntermediatePaths()
    {
        var first = await WriteAsync("partial-output", "first.txt", "first committed result");
        var second = await WriteAsync("partial-output", "second.txt", "second committed result");
        var failed = Path.Combine(_root, "partial-output", "third-failed.txt");
        var intermediate = await WriteAsync("partial-output", "third-failed.tmp", "uncommitted bytes");
        await using var service = CreateService();
        await service.StartAsync();
        await using var registry = new ItemActionRegistry(
            [new PartialOutputAction([first, second, first.ToUpperInvariant()])], recent: service);
        var result = await registry.ExecuteAsync(ItemActionId.ConvertImage,
            new ItemActionContext(new ItemSelectionSnapshot([])));

        Assert.IsFalse(result.Succeeded, "Partial success must not be reported as complete success.");
        Assert.IsTrue(result.IsPartial);
        Assert.AreEqual("ActionPartiallyCompleted", result.MessageResourceKey);
        Assert.AreEqual(2, result.OutputItemIds.Count, "The same path repeated with different casing is one object.");
        Assert.AreEqual(2, result.OutputItemIds.Distinct().Count());
        Assert.IsFalse(result.OutputPaths.Contains(failed));
        Assert.IsFalse(result.OutputPaths.Contains(intermediate));
        var items = await _repository.QueryAsync(new ItemQuery(Source: ItemSource.Recent));
        Assert.AreEqual(2, items.Count);
        foreach (var path in new[] { first, second })
        {
            var item = (await _repository.FindRecentFileByPathAsync(path))!;
            Assert.IsTrue(result.OutputItemIds.Contains(item.Id));
            Assert.AreEqual(ItemStatus.Available, item.Status);
            var metadata = RecentItemMetadata.Read(item.MetadataJson)!;
            Assert.IsTrue(metadata.IsOutput);
            Assert.IsTrue(metadata.IsPartial);
            Assert.AreEqual(nameof(ItemActionId.ConvertImage), metadata.Operation);
        }

        // A failed third output is absent from the action's final-path list. The intake
        // boundary also rejects missing/intermediate paths if a caller passes one anyway.
        Assert.IsNull(await service.RegisterOutputAsync(failed, "ConvertImage", partial: true));
        Assert.IsNull(await service.RegisterOutputAsync(intermediate, "ConvertImage", partial: true));
        Assert.IsNull(await _repository.FindRecentFileByPathAsync(failed));
        Assert.IsNull(await _repository.FindRecentFileByPathAsync(intermediate));
        Assert.AreEqual(2, await _repository.CountAsync(ItemSource.Recent));
        Assert.AreEqual("first committed result", await File.ReadAllTextAsync(first));
        Assert.AreEqual("second committed result", await File.ReadAllTextAsync(second));
        Assert.IsTrue(File.Exists(intermediate), "Rejecting an intermediate reference never deletes its external file.");
    }

    private sealed class PartialOutputAction(IReadOnlyList<string> outputs) : IItemAction
    {
        public ItemActionDescriptor Descriptor { get; } = new(ItemActionId.ConvertImage, "ActionConvertImage.Text",
            "Image", ItemActionGroup.Transform, 1, false, false);
        public ItemActionCapability Evaluate(ItemSelectionSnapshot selection) => new(true, null, Descriptor);
        public Task<ItemActionResult> ExecuteAsync(ItemActionContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ItemActionResult(false, null, outputs, [], "one-output-failed") { IsPartial = true });
    }

    private RecentItemsService CreateService() => new(_repository, _files, _settings, _storage, NullLogger<RecentItemsService>.Instance);

    private async Task<string> WriteAsync(string directory, string name, string content)
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, directory)).FullName;
        var path = Path.Combine(folder, name);
        await File.WriteAllTextAsync(path, content);
        return path;
    }

    private async Task AgeAllAsync()
    {
        await using var connection = new SqliteConnection($"Data Source={_storage.Database}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE items SET created_at_utc = '2020-01-01T00:00:00.0000000Z';";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
        Assert.Fail("The expected passive file state was not observed within ten seconds.");
    }
}
