using System.Globalization;
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

internal static class RetentionScenario
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    internal static async Task RunAsync()
    {
        var paths = new AppStoragePaths(Path.Combine(Path.GetTempPath(),
            "DropSpace-undo-retention-probe", Guid.NewGuid().ToString("N")));
        try
        {
            var database = new SqliteDatabase(paths, NullLogger<SqliteDatabase>.Instance);
            var repository = new SqliteItemRepository(database, NullLogger<SqliteItemRepository>.Instance);
            var payloads = new FilePayloadStore(paths);
            var cleanup = new PayloadCleanupCoordinator(paths, repository, payloads,
                new OwnedPayloadReconciler(paths, repository, NullLogger<OwnedPayloadReconciler>.Instance),
                NullLogger<PayloadCleanupCoordinator>.Instance);
            var now = DateTimeOffset.UtcNow;
            var cutoff = now.AddDays(-1);
            byte[] oldBytes = [1, 2, 3];
            byte[] newBytes = [4, 5, 6];
            byte[] expiredBytes = [7, 8, 9];
            var oldProtected = await AddImageAsync(oldBytes, now.AddDays(-2), pinned: true);
            var newProtected = await AddImageAsync(newBytes, now, pinned: true);
            var compliant = await repository.AddTextAsync(ContentClassifier.CreateTextCandidate("keep within count limit"));
            await SetCreatedAtAsync(compliant.Id, now.AddHours(-1));
            var expired = await AddImageAsync(expiredBytes, now.AddDays(-3), pinned: false);
            var oldPath = payloads.ResolvePath(oldProtected.Payload!.RelativePath);
            var newPath = payloads.ResolvePath(newProtected.Payload!.RelativePath);
            var expiredPath = payloads.ResolvePath(expired.Payload!.RelativePath);
            var proxy = DispatchProxy.Create<IItemRepository, RepositoryCommitBarrier>();
            var barrier = (RepositoryCommitBarrier)proxy;
            barrier.Repository = repository;
            await using var undo = new UndoCoordinator(proxy, payloads, new FilePreviewCache(paths),
                NullLogger<UndoCoordinator>.Instance, cleanup);
            var pins = new PinItemsUseCase(undo);
            Task<BatchPinResult>? unpin = null;
            Task<RetentionResult>? retention = null;
            try
            {
                unpin = pins.SetAsync([oldProtected.Id, newProtected.Id], isPinned: false);
                await barrier.Committed.Task.WaitAsync(Deadline);
                Check((await repository.GetAsync(oldProtected.Id))?.IsPinned == false &&
                    (await repository.GetAsync(newProtected.Id))?.IsPinned == false && undo.State is null,
                    "The fixture did not stop between real unpin commit and Undo publication.");

                // Direct async invocation runs inline to the held gate's incomplete
                // WaitAsync; no background launch or scheduling delay is used here.
                retention = undo.ApplyRetentionAsync(cutoff, countLimit: 1);
                Check(!retention.IsCompleted && barrier.RetentionCalls == 0,
                    "Retention entered the repository before the committed unpin owned its Undo slot.");
                barrier.Release.TrySetResult();
                await Task.WhenAll(unpin, retention).WaitAsync(Deadline);
                Check((await unpin).AffectedCount == 2 && barrier.RetentionCalls == 1,
                    "The serialized operations did not both complete exactly once.");
                var undoState = undo.State;
                Check(undoState?.Kind == UndoOperationKind.PinChange,
                    "Retention retired the still-valid pin Undo owner.");
                var removed = await retention;
                Check(removed.RemovedCount == 1 && removed.PayloadPaths.Count == 1 &&
                    removed.PayloadPaths[0] == expired.Payload.RelativePath,
                    "Retention did not restrict cleanup to the unprotected expired image.");
                Check(await repository.GetAsync(expired.Id) is null &&
                    await repository.GetAsync(compliant.Id) is not null,
                    "A protected newest row displaced compliant history in the count ranking.");
                Check((await repository.GetAsync(oldProtected.Id))?.IsPinned == false &&
                    (await repository.GetAsync(newProtected.Id))?.IsPinned == false,
                    "Retention deleted an undo-protected unpinned row.");
                var outbox = await repository.GetPendingPayloadDeletesAsync();
                Check(outbox.Count == 1 && outbox[0].RelativePath == expired.Payload.RelativePath,
                    "Protected payloads entered the durable deletion outbox.");
                Check(await cleanup.DrainAsync() == 1 && !File.Exists(expiredPath) &&
                    (await repository.GetPendingPayloadDeletesAsync()).Count == 0,
                    "The unprotected expired payload was not physically cleaned through its outbox.");
                Check((await File.ReadAllBytesAsync(oldPath)).SequenceEqual(oldBytes) &&
                    (await File.ReadAllBytesAsync(newPath)).SequenceEqual(newBytes),
                    "Protected payload bytes changed during retention cleanup.");
                Check(undo.State == undoState,
                    "Retention cleanup changed the Undo owner or extended its deadline.");
                Check(await undo.UndoAsync().WaitAsync(Deadline) &&
                    (await repository.GetAsync(oldProtected.Id))?.IsPinned == true &&
                    (await repository.GetAsync(newProtected.Id))?.IsPinned == true,
                    "Undo could not restore both pins from the preserved real rows.");

                await pins.SetAsync([oldProtected.Id, newProtected.Id], isPinned: false).WaitAsync(Deadline);
                await undo.FinalizeActiveAsync().WaitAsync(Deadline);
                Check(undo.State is null, "Explicit finalization left stale retention protection.");
                var afterFinalization = await undo.ApplyRetentionAsync(cutoff, countLimit: 1).WaitAsync(Deadline);
                Check(afterFinalization.RemovedCount == 2 && afterFinalization.PayloadPaths.Count == 1 &&
                    afterFinalization.PayloadPaths[0] == oldProtected.Payload.RelativePath,
                    "Finalized Undo still protected aged/count-excess history.");
                Check(await repository.GetAsync(oldProtected.Id) is null &&
                    await repository.GetAsync(compliant.Id) is null &&
                    (await repository.GetAsync(newProtected.Id))?.IsPinned == false,
                    "Normal retention did not restore the latest-one count policy after finalization.");
                Check(await cleanup.DrainAsync() == 1 && !File.Exists(oldPath) &&
                    (await File.ReadAllBytesAsync(newPath)).SequenceEqual(newBytes) &&
                    (await repository.GetPendingPayloadDeletesAsync()).Count == 0 &&
                    await repository.CountAsync(ItemSource.Clipboard) == 1,
                    "Final physical cleanup or retained latest payload is inconsistent.");
                Console.WriteLine("PASS retention: real unpin commit and retention serialize; two Undo-protected images survive age/count cleanup without displacing eligible text; one expired payload is deleted through the outbox; Undo restores both pins; explicit finalization restores normal age/count deletion and leaves only the newest payload.");
            }
            finally
            {
                barrier.Release.TrySetResult();
                if (unpin is not null) await unpin.WaitAsync(Deadline);
                if (retention is not null) await retention.WaitAsync(Deadline);
            }

            async Task<DropItem> AddImageAsync(byte[] bytes, DateTimeOffset createdAt, bool pinned)
            {
                await using var input = new MemoryStream(bytes);
                var payload = await payloads.WriteFileAsync("images", ".bin", input, 1024);
                var item = await repository.AddImageAsync(new ImageCandidate(
                    payload.ContentHash, 1, 1, payload.ByteLength, "image/png", false, payload));
                if (pinned) await repository.SetPinnedAsync(item.Id, true);
                await SetCreatedAtAsync(item.Id, createdAt);
                return (await repository.GetAsync(item.Id))!;
            }

            async Task SetCreatedAtAsync(Guid id, DateTimeOffset createdAt)
            {
                // Fixture-only historical timestamps avoid sleeps or changing any
                // production clock/retention policy; all later operations are real.
                await using var connection = await database.OpenConnectionAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE items SET created_at_utc = @created WHERE id = @id;";
                command.Parameters.AddWithValue("@created", createdAt.ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.Add("@id", SqliteType.Blob).Value = id.ToByteArray();
                Check(await command.ExecuteNonQueryAsync() == 1, "Fixture timestamp update did not affect one real row.");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(paths.Root)) Directory.Delete(paths.Root, recursive: true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
