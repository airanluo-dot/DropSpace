using DropSpace.Core.Models;
using DropSpace.Core.Policies;
using DropSpace.Core.Preview;
using DropSpace.Infrastructure.Content;
using DropSpace.Infrastructure.Data;
using DropSpace.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class RepositoryRelinkAuditTests
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DropSpace-relink-audit", Guid.NewGuid().ToString("N"));
    private AppStoragePaths Paths => new(_root);

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [TestMethod]
    public async Task MissingClipboardFileRelinkUpdatesAvailabilityMetadataAndReadableContent()
    {
        var repository = CreateRepository();
        var original = await WriteCandidateAsync("original.txt", "old contents");
        var item = await repository.AddClipboardFileAsync(original, FingerprintService.ForText(original.OriginalPath), null);
        File.Delete(original.OriginalPath);
        await repository.UpdateFileStatusAsync(item.Id, ItemStatus.Missing, "File no longer exists");
        var before = (await repository.GetAsync(item.Id))!;
        var replacement = await WriteCandidateAsync("replacement.md", "replacement contents");

        await repository.ReplaceFileReferenceAsync(item.Id, replacement);
        var refreshed = (await repository.GetAsync(item.Id))!;
        var content = new ItemContentResolver(Paths).Resolve(DropItemSnapshot.FromItem(refreshed));

        Assert.AreEqual(ItemSource.Clipboard, refreshed.Source);
        Assert.AreEqual(ItemStatus.Available, refreshed.Status);
        Assert.AreEqual(replacement.Title, refreshed.Title);
        Assert.IsTrue(refreshed.Revision > before.Revision);
        Assert.IsTrue(refreshed.SearchText.Contains("replacement.md", StringComparison.Ordinal));
        Assert.IsTrue(content.HasReadablePath);
        Assert.AreEqual("replacement contents", await File.ReadAllTextAsync(content.ReadablePath!));
    }

    [TestMethod]
    public async Task LateAvailabilityForReplacedFileCannotChangeNewReference()
    {
        var repository = CreateRepository();
        var original = await WriteCandidateAsync("observed-original.txt", "original contents");
        var observed = await repository.AddFileAsync(original);
        File.Delete(original.OriginalPath);
        var lateResult = await new LocalFileReferenceService().CheckAvailabilityAsync(observed.File!);
        Assert.AreEqual(ItemStatus.Missing, lateResult.Status);

        var replacement = await WriteCandidateAsync("current-replacement.txt", "replacement contents");
        await repository.ReplaceFileReferenceAsync(observed.Id, replacement);
        var current = (await repository.GetAsync(observed.Id))!;

        // Deliver A's completed check only after B has become the authoritative file.
        Assert.IsFalse(await repository.TryUpdateFileStatusAsync(observed.Id, observed.Revision,
            observed.File!.NormalizedPath, observed.File.EntryKind, lateResult.Status, lateResult.Reason));
        Assert.AreEqual(current, await repository.GetAsync(observed.Id));

        // Path and kind are part of the observed reference, even with a current revision.
        Assert.IsFalse(await repository.TryUpdateFileStatusAsync(current.Id, current.Revision,
            original.NormalizedPath, current.File!.EntryKind, lateResult.Status, lateResult.Reason));
        Assert.IsFalse(await repository.TryUpdateFileStatusAsync(current.Id, current.Revision,
            current.File.NormalizedPath, FileEntryKind.Folder, lateResult.Status, lateResult.Reason));
        Assert.AreEqual(current, await repository.GetAsync(observed.Id));

        File.Delete(replacement.OriginalPath);
        var currentResult = await new LocalFileReferenceService().CheckAvailabilityAsync(current.File);
        Assert.IsTrue(await repository.TryUpdateFileStatusAsync(current.Id, current.Revision,
            current.File.NormalizedPath, current.File.EntryKind, currentResult.Status, currentResult.Reason));
        var changed = (await repository.GetAsync(current.Id))!;
        Assert.AreEqual(current.Revision + 1, changed.Revision);
        Assert.AreEqual(currentResult.Status, changed.Status);
        Assert.AreEqual(currentResult.Reason, changed.File!.AvailabilityReason);
        Assert.AreEqual(replacement.NormalizedPath, changed.File.NormalizedPath);

        // The accepted write retires its observation too; a repeated old result is inert.
        Assert.IsFalse(await repository.TryUpdateFileStatusAsync(current.Id, current.Revision,
            current.File.NormalizedPath, current.File.EntryKind, ItemStatus.Available, null));
        Assert.AreEqual(changed, await repository.GetAsync(current.Id));
    }

    [TestMethod]
    public async Task LateRelinkCannotReplaceNewReferenceOrRevivedItem()
    {
        var repository = CreateRepository();
        var original = await WriteCandidateAsync("relink-observed.txt", "original contents");
        var observed = await repository.AddFileAsync(original);
        var replacement = await WriteCandidateAsync("relink-current.txt", "current contents");
        var lateReplacement = await WriteCandidateAsync("relink-late.txt", "late contents");

        Assert.IsTrue(await repository.TryReplaceFileReferenceAsync(observed.Id, observed.Revision,
            observed.File!.NormalizedPath, observed.File.EntryKind, replacement));
        var current = (await repository.GetAsync(observed.Id))!;
        Assert.IsFalse(await repository.TryReplaceFileReferenceAsync(observed.Id, observed.Revision,
            observed.File.NormalizedPath, observed.File.EntryKind, lateReplacement));
        Assert.AreEqual(current, await repository.GetAsync(observed.Id));

        // The picker/inspection may remain pending across both Remove and Undo.
        Assert.AreEqual(1, await repository.BeginPendingRemovalAsync(
            [current.Id], "pending-late-relink", DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.IsFalse(await repository.TryReplaceFileReferenceAsync(current.Id, current.Revision,
            current.File!.NormalizedPath, current.File.EntryKind, lateReplacement));
        Assert.AreEqual(1, await repository.UndoPendingRemovalAsync("pending-late-relink"));
        var restored = (await repository.GetAsync(current.Id))!;
        Assert.AreEqual(current.Revision + 1, restored.Revision);
        Assert.AreEqual(current with { Revision = restored.Revision }, restored);
        Assert.IsFalse(await repository.TryReplaceFileReferenceAsync(current.Id, current.Revision,
            current.File.NormalizedPath, current.File.EntryKind, lateReplacement));
        Assert.IsFalse(await repository.TryUpdateFileStatusAsync(current.Id, current.Revision,
            current.File.NormalizedPath, current.File.EntryKind, ItemStatus.Missing, "obsolete check"));
        Assert.AreEqual(restored, await repository.GetAsync(current.Id));
        Assert.IsTrue(await repository.TryReplaceFileReferenceAsync(restored.Id, restored.Revision,
            restored.File!.NormalizedPath, restored.File.EntryKind, lateReplacement));
        Assert.AreEqual(lateReplacement.NormalizedPath, (await repository.GetAsync(restored.Id))!.File!.NormalizedPath);

        // Clipboard clear is a separate removal entry point with the same retirement rule.
        var clipboard = await repository.AddClipboardFileAsync(original, "late-clipboard-relink", null);
        Assert.AreEqual(1, await repository.BeginPendingClipboardClearAsync(
            null, true, "pending-late-clear", DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.AreEqual(1, await repository.UndoPendingRemovalAsync("pending-late-clear"));
        var clearedThenRestored = (await repository.GetAsync(clipboard.Id))!;
        Assert.AreEqual(clipboard.Revision + 1, clearedThenRestored.Revision);
        Assert.IsFalse(await repository.TryReplaceFileReferenceAsync(clipboard.Id, clipboard.Revision,
            clipboard.File!.NormalizedPath, clipboard.File.EntryKind, replacement));
        Assert.IsFalse(await repository.TryUpdateFileStatusAsync(clipboard.Id, clipboard.Revision,
            clipboard.File.NormalizedPath, clipboard.File.EntryKind, ItemStatus.Missing, "obsolete clear check"));
        Assert.AreEqual(clearedThenRestored, await repository.GetAsync(clipboard.Id));
        Assert.AreEqual("original contents", await File.ReadAllTextAsync(original.OriginalPath));
        Assert.AreEqual("current contents", await File.ReadAllTextAsync(replacement.OriginalPath));
        Assert.AreEqual("late contents", await File.ReadAllTextAsync(lateReplacement.OriginalPath));
    }

    [TestMethod]
    public async Task MissingOwnedSpaceFileRelinkDetachesTheMissingPayloadAndQueuesCleanup()
    {
        var repository = CreateRepository();
        var store = new FilePayloadStore(Paths);
        var item = await AddOwnedFileAsync(repository, store);
        File.Delete(store.ResolvePath(item.Payload!.RelativePath));
        await repository.UpdateFileStatusAsync(item.Id, ItemStatus.Missing, "File no longer exists");
        var replacement = await WriteCandidateAsync("located.txt", "located contents");

        await repository.ReplaceFileReferenceAsync(item.Id, replacement);
        var refreshed = (await repository.GetAsync(item.Id))!;
        var content = new ItemContentResolver(Paths).Resolve(DropItemSnapshot.FromItem(refreshed));

        Assert.IsNull(refreshed.Payload);
        Assert.AreEqual(ItemStatus.Available, refreshed.Status);
        Assert.IsTrue(content.HasReadablePath);
        Assert.AreEqual(replacement.OriginalPath, content.ReadablePath);
        Assert.AreEqual(item.Payload.RelativePath, (await repository.GetPendingPayloadDeletesAsync()).Single().RelativePath);
        Assert.AreEqual(1, await CreateCleanup(repository, store).DrainAsync());
        Assert.AreEqual("located contents", await File.ReadAllTextAsync(replacement.OriginalPath));
    }

    [TestMethod]
    public async Task RelinkCleanupDeletesOnlyTheObsoleteOwnedPayloadAndKeepsTheReplacement()
    {
        var repository = CreateRepository();
        var store = new FilePayloadStore(Paths);
        var item = await AddOwnedFileAsync(repository, store);
        var obsoletePath = store.ResolvePath(item.Payload!.RelativePath);
        var replacement = await WriteCandidateAsync("external.txt", "keep replacement");

        await repository.ReplaceFileReferenceAsync(item.Id, replacement);

        Assert.AreEqual(1, await CreateCleanup(repository, store).DrainAsync());
        Assert.IsFalse(File.Exists(obsoletePath));
        Assert.AreEqual("keep replacement", await File.ReadAllTextAsync(replacement.OriginalPath));
        Assert.IsNull((await repository.GetAsync(item.Id))!.Payload);
    }

    [TestMethod]
    public async Task RelinkKeepsAFormerPayloadOwnedByAnotherItem()
    {
        var database = new SqliteDatabase(Paths, NullLogger<SqliteDatabase>.Instance);
        var repository = new SqliteItemRepository(database, NullLogger<SqliteItemRepository>.Instance);
        var store = new FilePayloadStore(Paths);
        var item = await AddOwnedFileAsync(repository, store);
        var second = await repository.AddFileAsync(await WriteCandidateAsync("second-reference.txt", "shared owner"));
        // The schema permits several items to own one payload; make that existing
        // ownership explicit without duplicating its unique payload record.
        await using (var connection = await database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE items SET payload_id = @payload WHERE id = @item;";
            command.Parameters.AddWithValue("@payload", item.Payload!.Id.ToByteArray());
            command.Parameters.AddWithValue("@item", second.Id.ToByteArray());
            await command.ExecuteNonQueryAsync();
        }
        var replacement = await WriteCandidateAsync("replacement.txt", "new contents");

        await repository.ReplaceFileReferenceAsync(item.Id, replacement);

        Assert.IsNull((await repository.GetAsync(item.Id))!.Payload);
        Assert.AreEqual(item.Payload!.Id, (await repository.GetAsync(second.Id))!.Payload!.Id);
        Assert.AreEqual(0, (await repository.GetPendingPayloadDeletesAsync()).Count);
        Assert.IsTrue(File.Exists(store.ResolvePath(item.Payload.RelativePath)));
        await repository.RemoveAsync(second.Id);
        Assert.AreEqual(item.Payload.RelativePath, (await repository.GetPendingPayloadDeletesAsync()).Single().RelativePath);
    }

    [TestMethod]
    public async Task RelinkDoesNotMutateAPendingRemovalOrItsRetainedPayload()
    {
        var repository = CreateRepository();
        var store = new FilePayloadStore(Paths);
        var item = await AddOwnedFileAsync(repository, store);
        await repository.BeginPendingRemovalAsync([item.Id], "pending-relink", DateTimeOffset.UtcNow.AddMinutes(1));
        var replacement = await WriteCandidateAsync("replacement.txt", "new contents");

        await repository.ReplaceFileReferenceAsync(item.Id, replacement);
        await repository.UndoPendingRemovalAsync("pending-relink");
        var restored = (await repository.GetAsync(item.Id))!;

        Assert.AreEqual(item.File!.OriginalPath, restored.File!.OriginalPath);
        Assert.AreEqual(item.Title, restored.Title);
        Assert.AreEqual(item.Payload!.Id, restored.Payload!.Id);
        Assert.AreEqual(0, (await repository.GetPendingPayloadDeletesAsync()).Count);
    }

    [TestMethod]
    public async Task RelinkToTheSameOwnedFileKeepsItsPayloadAndNeverQueuesTheSelectedFileForDeletion()
    {
        var repository = CreateRepository();
        var store = new FilePayloadStore(Paths);
        var item = await AddOwnedFileAsync(repository, store);
        await repository.UpdateFileStatusAsync(item.Id, ItemStatus.Missing, "Temporarily unavailable");
        var restoredFile = await new LocalFileReferenceService().InspectAsync(item.File!.OriginalPath);

        await repository.ReplaceFileReferenceAsync(item.Id, restoredFile);
        var restored = (await repository.GetAsync(item.Id))!;

        Assert.AreEqual(ItemStatus.Available, restored.Status);
        Assert.AreEqual(item.Payload!.Id, restored.Payload!.Id);
        Assert.AreEqual(0, (await repository.GetPendingPayloadDeletesAsync()).Count);
        Assert.AreEqual(0, await CreateCleanup(repository, store).DrainAsync());
        Assert.AreEqual("owned contents", await File.ReadAllTextAsync(restoredFile.OriginalPath));
    }

    [TestMethod]
    public async Task FailedReferenceWriteRollsBackTheItemAndPayloadOwnership()
    {
        var database = new SqliteDatabase(Paths, NullLogger<SqliteDatabase>.Instance);
        var repository = new SqliteItemRepository(database, NullLogger<SqliteItemRepository>.Instance);
        var store = new FilePayloadStore(Paths);
        var item = await AddOwnedFileAsync(repository, store);
        var replacement = await WriteCandidateAsync("replacement.txt", "new contents");
        await using (var connection = await database.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER reject_relink BEFORE UPDATE OF original_path ON file_references
                BEGIN SELECT RAISE(ABORT, 'simulated reference persistence failure'); END;
                """;
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsExactlyAsync<SqliteException>(() => repository.ReplaceFileReferenceAsync(item.Id, replacement));
        var retained = (await repository.GetAsync(item.Id))!;

        Assert.AreEqual(item.Title, retained.Title);
        Assert.AreEqual(item.Revision, retained.Revision);
        Assert.AreEqual(item.File!.OriginalPath, retained.File!.OriginalPath);
        Assert.AreEqual(item.Payload!.Id, retained.Payload!.Id);
        Assert.AreEqual(0, (await repository.GetPendingPayloadDeletesAsync()).Count);
        Assert.IsTrue(File.Exists(store.ResolvePath(item.Payload.RelativePath)));
        Assert.AreEqual("new contents", await File.ReadAllTextAsync(replacement.OriginalPath));
    }

    [TestMethod]
    public async Task RelinkRepairsALegacyReferenceThatAlreadyPointsOutsideItsRetainedPayload()
    {
        var database = new SqliteDatabase(Paths, NullLogger<SqliteDatabase>.Instance);
        var repository = new SqliteItemRepository(database, NullLogger<SqliteItemRepository>.Instance);
        var store = new FilePayloadStore(Paths);
        var item = await AddOwnedFileAsync(repository, store);
        var replacement = await WriteCandidateAsync("external.txt", "external contents");
        await SetLegacyReferenceAsync(database, item.Id, replacement);

        await repository.ReplaceFileReferenceAsync(item.Id, replacement);
        var repaired = (await repository.GetAsync(item.Id))!;

        Assert.IsNull(repaired.Payload);
        Assert.AreEqual(replacement.OriginalPath,
            new ItemContentResolver(Paths).Resolve(DropItemSnapshot.FromItem(repaired)).ReadablePath);
        Assert.AreEqual(1, await CreateCleanup(repository, store).DrainAsync());
        Assert.AreEqual("external contents", await File.ReadAllTextAsync(replacement.OriginalPath));
    }

    [TestMethod]
    public async Task LocatingTheActualOwnedFileFromALegacyReferenceRetainsTheSelectedPayload()
    {
        var database = new SqliteDatabase(Paths, NullLogger<SqliteDatabase>.Instance);
        var repository = new SqliteItemRepository(database, NullLogger<SqliteItemRepository>.Instance);
        var store = new FilePayloadStore(Paths);
        var item = await AddOwnedFileAsync(repository, store);
        var external = await WriteCandidateAsync("legacy-external.txt", "external contents");
        await SetLegacyReferenceAsync(database, item.Id, external);
        var actualOwnedFile = await new LocalFileReferenceService().InspectAsync(store.ResolvePath(item.Payload!.RelativePath));

        await repository.ReplaceFileReferenceAsync(item.Id, actualOwnedFile);
        var repaired = (await repository.GetAsync(item.Id))!;

        Assert.AreEqual(item.Payload.Id, repaired.Payload!.Id);
        Assert.AreEqual(actualOwnedFile.OriginalPath, repaired.File!.OriginalPath);
        Assert.AreEqual(0, (await repository.GetPendingPayloadDeletesAsync()).Count);
        Assert.AreEqual(0, await CreateCleanup(repository, store).DrainAsync());
        Assert.AreEqual("owned contents", await File.ReadAllTextAsync(actualOwnedFile.OriginalPath));
    }

    private SqliteItemRepository CreateRepository() =>
        new(new SqliteDatabase(Paths, NullLogger<SqliteDatabase>.Instance), NullLogger<SqliteItemRepository>.Instance);

    private static async Task SetLegacyReferenceAsync(SqliteDatabase database, Guid id, FileCandidate candidate)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE file_references SET original_path = @original, normalized_path = @normalized WHERE item_id = @id;";
        command.Parameters.AddWithValue("@original", candidate.OriginalPath);
        command.Parameters.AddWithValue("@normalized", candidate.NormalizedPath);
        command.Parameters.AddWithValue("@id", id.ToByteArray());
        await command.ExecuteNonQueryAsync();
    }

    private async Task<FileCandidate> WriteCandidateAsync(string name, string contents)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name);
        await File.WriteAllTextAsync(path, contents);
        return await new LocalFileReferenceService().InspectAsync(path);
    }

    private static async Task<DropItem> AddOwnedFileAsync(SqliteItemRepository repository, FilePayloadStore store)
    {
        await using var input = new MemoryStream("owned contents"u8.ToArray());
        var payload = await store.WriteFileAsync("files", ".txt", input, 1024);
        var candidate = await new LocalFileReferenceService().InspectAsync(store.ResolvePath(payload.RelativePath));
        return await repository.AddOwnedSpaceFileAsync(candidate, payload, null);
    }

    private PayloadCleanupCoordinator CreateCleanup(SqliteItemRepository repository, FilePayloadStore store) =>
        new(Paths, repository, store,
            new OwnedPayloadReconciler(Paths, repository, NullLogger<OwnedPayloadReconciler>.Instance),
            NullLogger<PayloadCleanupCoordinator>.Instance);
}
