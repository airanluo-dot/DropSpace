using System.Text.Json;
using DropSpace.Core.Models;
using DropSpace.Core.Policies;
using Microsoft.Data.Sqlite;

namespace DropSpace.Infrastructure.Data;

public sealed partial class SqliteItemRepository
{
    public async Task<DropItem?> FindRecentFileByPathAsync(string normalizedPath, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await FindRecentFileWithConnectionAsync(connection, normalizedPath, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<DropItem?> FindRecentFileWithConnectionAsync(SqliteConnection connection, string path, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = SelectSql + "\n" + """
            WHERE i.source IN (@recent, @space)
              AND f.normalized_path = @path COLLATE DROPSPACE_PATH AND i.pending_delete_token IS NULL
            ORDER BY i.source ASC LIMIT 1;
            """;
        command.Parameters.AddWithValue("@recent", (int)ItemSource.Recent);
        command.Parameters.AddWithValue("@space", (int)ItemSource.Space);
        command.Parameters.AddWithValue("@path", path);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadItem(reader) : null;
    }

    public async Task<DropItem> UpsertRecentFileAsync(FileCandidate candidate, RecentItemMetadata metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(metadata);
        await database.WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var existing = await FindRecentFileWithConnectionAsync(connection, candidate.NormalizedPath, cancellationToken).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var id = existing?.Id ?? Guid.NewGuid();
            var merged = RecentItemMetadata.Merge(existing?.MetadataJson, metadata);
            if (existing?.File is { } old && existing.Status == candidate.Status &&
                existing.Title == candidate.Title && old.KnownSize == candidate.KnownSize &&
                old.KnownModifiedAtUtc == candidate.KnownModifiedAtUtc && old.EntryKind == candidate.EntryKind &&
                old.AvailabilityReason == candidate.AvailabilityReason && existing.MetadataJson == merged)
                return existing;

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                await InsertBaseItemAsync(connection, transaction, id, ItemSource.Recent,
                    candidate.EntryKind == FileEntryKind.Folder ? ItemKind.Folder : ItemKind.File,
                    candidate.Title, now, candidate.Status,
                    ContentClassifier.BuildSearchText(candidate.Title, candidate.OriginalPath),
                    null, null, cancellationToken, merged).ConfigureAwait(false);
                await InsertFileReferenceAsync(connection, transaction, id, candidate, now, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Keep the ID, source, pin and payload ownership. A revision retires every old preview.
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE items SET title=@title, kind=@kind, status=@status, search_text=@search,
                        created_at_utc=CASE WHEN source=@recent AND @refresh_recent=1 THEN @checked ELSE created_at_utc END,
                        metadata_json=@metadata, revision=revision+1 WHERE id=@id AND pending_delete_token IS NULL;
                    UPDATE file_references SET original_path=@original, normalized_path=@normalized,
                        entry_kind=@entry, extension=@extension, known_size=@size,
                        known_modified_at_utc=@modified, last_checked_at_utc=@checked,
                        availability_reason=@reason WHERE item_id=@id;
                    """;
                command.Parameters.AddWithValue("@id", ToBytes(id));
                command.Parameters.AddWithValue("@title", candidate.Title);
                command.Parameters.AddWithValue("@kind", candidate.EntryKind == FileEntryKind.Folder ? (int)ItemKind.Folder : (int)ItemKind.File);
                command.Parameters.AddWithValue("@status", (int)candidate.Status);
                command.Parameters.AddWithValue("@recent", (int)ItemSource.Recent);
                command.Parameters.AddWithValue("@refresh_recent", candidate.Status == ItemStatus.Available &&
                    (existing.Status != ItemStatus.Available || existing.File?.KnownSize != candidate.KnownSize ||
                     existing.File?.KnownModifiedAtUtc != candidate.KnownModifiedAtUtc) ? 1 : 0);
                command.Parameters.AddWithValue("@search", ContentClassifier.BuildSearchText(candidate.Title, candidate.OriginalPath));
                command.Parameters.AddWithValue("@metadata", merged);
                command.Parameters.AddWithValue("@original", candidate.OriginalPath);
                command.Parameters.AddWithValue("@normalized", candidate.NormalizedPath);
                command.Parameters.AddWithValue("@entry", (int)candidate.EntryKind);
                command.Parameters.AddWithValue("@extension", DbValue(candidate.Extension));
                command.Parameters.AddWithValue("@size", DbValue(candidate.KnownSize));
                command.Parameters.AddWithValue("@modified", DbTimestamp(candidate.KnownModifiedAtUtc));
                command.Parameters.AddWithValue("@checked", ToTimestamp(now));
                command.Parameters.AddWithValue("@reason", DbValue(candidate.AvailabilityReason));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            return (await GetWithConnectionAsync(connection, id, CancellationToken.None).ConfigureAwait(false))!;
        }
        finally { database.WriteGate.Release(); }
    }

    public async Task RetainRecentAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await database.WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE items SET source=@space, revision=revision+1 WHERE id=@id AND source=@recent AND pending_delete_token IS NULL;";
            command.Parameters.AddWithValue("@id", ToBytes(id));
            command.Parameters.AddWithValue("@space", (int)ItemSource.Space);
            command.Parameters.AddWithValue("@recent", (int)ItemSource.Recent);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { database.WriteGate.Release(); }
    }

    public async Task<int> ApplyRecentRetentionAsync(DateTimeOffset ageCutoffUtc, int countLimit, IReadOnlyCollection<Guid> protectedIds, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(countLimit);
        ArgumentNullException.ThrowIfNull(protectedIds);
        var protectedJson = JsonSerializer.Serialize(protectedIds.Distinct().Select(id => Convert.ToHexString(ToBytes(id))));
        await database.WriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            // Recent rows reference external files and never own payloads. Refuse to collect any
            // unexpected owned row; cleanup cannot turn a future schema mistake into lost content.
            command.CommandText = """
                WITH ranked AS (
                    SELECT id, created_at_utc, ROW_NUMBER() OVER (ORDER BY created_at_utc DESC, id DESC) AS n
                    FROM items WHERE source=@recent AND is_pinned=0 AND payload_id IS NULL
                      AND pending_delete_token IS NULL
                      AND hex(id) NOT IN (SELECT value FROM json_each(@protected))
                )
                DELETE FROM items WHERE id IN (SELECT id FROM ranked WHERE created_at_utc<@cutoff OR n>@count)
                  AND source=@recent AND is_pinned=0 AND payload_id IS NULL AND pending_delete_token IS NULL;
                """;
            command.Parameters.AddWithValue("@recent", (int)ItemSource.Recent);
            command.Parameters.AddWithValue("@protected", protectedJson);
            command.Parameters.AddWithValue("@cutoff", ToTimestamp(ageCutoffUtc));
            command.Parameters.AddWithValue("@count", countLimit);
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { database.WriteGate.Release(); }
    }
}
