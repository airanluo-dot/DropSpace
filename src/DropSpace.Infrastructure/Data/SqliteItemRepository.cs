using System.Globalization;
using System.Text;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using DropSpace.Core.Policies;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace DropSpace.Infrastructure.Data;

public sealed class SqliteItemRepository(
    SqliteDatabase database,
    ILogger<SqliteItemRepository> logger) : IItemRepository
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public Task InitializeAsync(CancellationToken cancellationToken = default) => database.InitializeAsync(cancellationToken);

    public Task<DropItem> AddFileAsync(FileCandidate candidate, CancellationToken cancellationToken = default) =>
        AddFileCoreAsync(candidate, ItemSource.Space, null, null, cancellationToken);

    public Task<DropItem> AddSpaceFileAsync(
        FileCandidate candidate,
        string? metadataJson,
        CancellationToken cancellationToken = default) =>
        AddFileCoreAsync(candidate, ItemSource.Space, null, metadataJson, cancellationToken);

    public Task<DropItem> AddClipboardFileAsync(
        FileCandidate candidate,
        string fingerprint,
        string? metadataJson,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fingerprint);
        return AddFileCoreAsync(candidate, ItemSource.Clipboard, fingerprint, metadataJson, cancellationToken);
    }

    private async Task<DropItem> AddFileCoreAsync(
        FileCandidate candidate,
        ItemSource source,
        string? fingerprint,
        string? metadataJson,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var duplicateId = source == ItemSource.Space
                ? await FindFileDuplicateAsync(connection, source, candidate.NormalizedPath, cancellationToken)
                    .ConfigureAwait(false)
                : null;
            if (duplicateId is Guid existingId)
            {
                logger.LogInformation("An existing Space reference was reused instead of creating a duplicate.");
                return (await GetWithConnectionAsync(connection, existingId, cancellationToken).ConfigureAwait(false))!;
            }

            var now = DateTimeOffset.UtcNow;
            var itemId = Guid.NewGuid();
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await InsertBaseItemAsync(
                    connection,
                    transaction,
                    itemId,
                    source,
                    candidate.EntryKind == FileEntryKind.Folder ? ItemKind.Folder : ItemKind.File,
                    candidate.Title,
                    now,
                    candidate.Status,
                    ContentClassifier.BuildSearchText(candidate.Title, candidate.OriginalPath),
                    fingerprint,
                    null,
                    cancellationToken,
                    metadataJson)
                .ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                INSERT INTO file_references (
                    item_id, original_path, normalized_path, entry_kind, extension, known_size,
                    known_modified_at_utc, volume_hint, last_checked_at_utc, availability_reason)
                VALUES (
                    @item_id, @original_path, @normalized_path, @entry_kind, @extension, @known_size,
                    @known_modified_at_utc, NULL, @last_checked_at_utc, @availability_reason);
                """;
            command.Parameters.AddWithValue("@item_id", ToBytes(itemId));
            command.Parameters.AddWithValue("@original_path", candidate.OriginalPath);
            command.Parameters.AddWithValue("@normalized_path", candidate.NormalizedPath);
            command.Parameters.AddWithValue("@entry_kind", (int)candidate.EntryKind);
            command.Parameters.AddWithValue("@extension", DbValue(candidate.Extension));
            command.Parameters.AddWithValue("@known_size", DbValue(candidate.KnownSize));
            command.Parameters.AddWithValue("@known_modified_at_utc", DbTimestamp(candidate.KnownModifiedAtUtc));
            command.Parameters.AddWithValue("@last_checked_at_utc", ToTimestamp(now));
            command.Parameters.AddWithValue("@availability_reason", DbValue(candidate.AvailabilityReason));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return (await GetWithConnectionAsync(connection, itemId, cancellationToken).ConfigureAwait(false))!;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public Task<DropItem> AddTextAsync(TextCandidate candidate, CancellationToken cancellationToken = default) =>
        AddTextCoreAsync(candidate, ItemSource.Clipboard, null, cancellationToken);

    public Task<DropItem> AddSpaceTextAsync(
        TextCandidate candidate,
        string? metadataJson = null,
        CancellationToken cancellationToken = default) =>
        AddTextCoreAsync(candidate, ItemSource.Space, metadataJson, cancellationToken);

    private async Task<DropItem> AddTextCoreAsync(
        TextCandidate candidate,
        ItemSource source,
        string? metadataJson,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var itemId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await InsertBaseItemAsync(
                    connection,
                    transaction,
                    itemId,
                    source,
                    candidate.Kind,
                    candidate.Title,
                    now,
                    ItemStatus.Available,
                    ContentClassifier.BuildSearchText(candidate.Title, candidate.Text),
                    candidate.Fingerprint,
                    null,
                    cancellationToken,
                    metadataJson)
                .ConfigureAwait(false);

            await using (var textCommand = connection.CreateCommand())
            {
                textCommand.Transaction = (SqliteTransaction)transaction;
                textCommand.CommandText = """
                    INSERT INTO text_payloads (
                        item_id, inline_text, character_count, detected_subtype, detection_confidence, language_hint)
                    VALUES (@item_id, @inline_text, @character_count, @detected_subtype, @detection_confidence, NULL);
                    """;
                textCommand.Parameters.AddWithValue("@item_id", ToBytes(itemId));
                textCommand.Parameters.AddWithValue("@inline_text", candidate.Text);
                textCommand.Parameters.AddWithValue("@character_count", candidate.Text.Length);
                textCommand.Parameters.AddWithValue("@detected_subtype", (int)candidate.Subtype);
                textCommand.Parameters.AddWithValue("@detection_confidence", (int)candidate.Confidence);
                await textCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            if (candidate.Url is not null)
            {
                await using var urlCommand = connection.CreateCommand();
                urlCommand.Transaction = (SqliteTransaction)transaction;
                urlCommand.CommandText = """
                    INSERT INTO url_metadata (item_id, normalized_url, display_url, host, scheme)
                    VALUES (@item_id, @normalized_url, @display_url, @host, @scheme);
                    """;
                urlCommand.Parameters.AddWithValue("@item_id", ToBytes(itemId));
                urlCommand.Parameters.AddWithValue("@normalized_url", candidate.Url.NormalizedUrl);
                urlCommand.Parameters.AddWithValue("@display_url", candidate.Url.DisplayUrl);
                urlCommand.Parameters.AddWithValue("@host", candidate.Url.Host);
                urlCommand.Parameters.AddWithValue("@scheme", candidate.Url.Scheme);
                await urlCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return (await GetWithConnectionAsync(connection, itemId, cancellationToken).ConfigureAwait(false))!;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<DropItem> AddImageAsync(ImageCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var itemId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await InsertPayloadAsync(connection, transaction, candidate.Payload, cancellationToken).ConfigureAwait(false);
            await InsertBaseItemAsync(
                    connection,
                    transaction,
                    itemId,
                    ItemSource.Clipboard,
                    ItemKind.Image,
                    $"Image {candidate.PixelWidth} × {candidate.PixelHeight}",
                    now,
                    ItemStatus.Available,
                    $"image {candidate.PixelWidth} {candidate.PixelHeight}",
                    candidate.Fingerprint,
                    candidate.Payload.Id,
                    cancellationToken)
                .ConfigureAwait(false);

            await using var imageCommand = connection.CreateCommand();
            imageCommand.Transaction = (SqliteTransaction)transaction;
            imageCommand.CommandText = """
                INSERT INTO image_payloads (
                    item_id, pixel_width, pixel_height, encoded_bytes, mime_type, has_alpha, thumbnail_revision)
                VALUES (@item_id, @pixel_width, @pixel_height, @encoded_bytes, @mime_type, @has_alpha, 1);
                """;
            imageCommand.Parameters.AddWithValue("@item_id", ToBytes(itemId));
            imageCommand.Parameters.AddWithValue("@pixel_width", candidate.PixelWidth);
            imageCommand.Parameters.AddWithValue("@pixel_height", candidate.PixelHeight);
            imageCommand.Parameters.AddWithValue("@encoded_bytes", candidate.EncodedBytes);
            imageCommand.Parameters.AddWithValue("@mime_type", candidate.MimeType);
            imageCommand.Parameters.AddWithValue("@has_alpha", DbValue(candidate.HasAlpha is null ? null : candidate.HasAlpha.Value ? 1 : 0));
            await imageCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return (await GetWithConnectionAsync(connection, itemId, cancellationToken).ConfigureAwait(false))!;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<IReadOnlyList<DropItem>> QueryAsync(ItemQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var sql = new StringBuilder(SelectSql);
        var clauses = new List<string>();
        await using var command = connection.CreateCommand();

        if (query.Source is not null)
        {
            clauses.Add("i.source = @source");
            command.Parameters.AddWithValue("@source", (int)query.Source.Value);
        }

        if (query.PinnedOnly)
        {
            clauses.Add("i.is_pinned = 1");
        }

        if (query.Kind is not null)
        {
            clauses.Add("i.kind = @kind");
            command.Parameters.AddWithValue("@kind", (int)query.Kind.Value);
        }

        if (query.Status is not null)
        {
            clauses.Add("i.status = @status");
            command.Parameters.AddWithValue("@status", (int)query.Status.Value);
        }

        var normalizedSearch = string.IsNullOrWhiteSpace(query.Search)
            ? null
            : SearchNormalizer.Normalize(query.Search);
        if (normalizedSearch is not null)
        {
            clauses.Add("i.search_text LIKE @search ESCAPE '\\'");
            command.Parameters.AddWithValue("@search", $"%{EscapeLike(normalizedSearch)}%");
            command.Parameters.AddWithValue("@exact", normalizedSearch);
            command.Parameters.AddWithValue("@prefix", $"{EscapeLike(normalizedSearch)}%");
        }

        if (clauses.Count > 0)
        {
            sql.Append(" WHERE ").AppendJoin(" AND ", clauses);
        }

        if (normalizedSearch is not null)
        {
            sql.Append(" ORDER BY CASE WHEN lower(i.title) = @exact THEN 0 WHEN i.search_text LIKE @prefix ESCAPE '\\' THEN 1 ELSE 2 END, i.created_at_utc DESC");
        }
        else
        {
            sql.Append(" ORDER BY i.created_at_utc DESC");
        }

        sql.Append(" LIMIT @limit OFFSET @offset;");
        command.Parameters.AddWithValue("@limit", Math.Clamp(query.Limit, 1, 100_000));
        command.Parameters.AddWithValue("@offset", Math.Max(query.Offset, 0));
        command.CommandText = sql.ToString();

        var items = new List<DropItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(ReadItem(reader));
        }

        return items;
    }

    public async Task<DropItem?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await GetWithConnectionAsync(connection, id, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetPinnedAsync(Guid id, bool isPinned, CancellationToken cancellationToken = default)
    {
        await ExecuteItemUpdateAsync(
                "UPDATE items SET is_pinned = @value, revision = revision + 1 WHERE id = @id;",
                id,
                isPinned ? 1 : 0,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task MarkUsedAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE items SET last_used_at_utc = @now, revision = revision + 1 WHERE id = @id;";
            command.Parameters.AddWithValue("@now", ToTimestamp(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("@id", ToBytes(id));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task UpdateFileStatusAsync(
        Guid id,
        ItemStatus status,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (var itemCommand = connection.CreateCommand())
            {
                itemCommand.Transaction = (SqliteTransaction)transaction;
                itemCommand.CommandText = "UPDATE items SET status = @status, revision = revision + 1 WHERE id = @id;";
                itemCommand.Parameters.AddWithValue("@status", (int)status);
                itemCommand.Parameters.AddWithValue("@id", ToBytes(id));
                await itemCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var referenceCommand = connection.CreateCommand())
            {
                referenceCommand.Transaction = (SqliteTransaction)transaction;
                referenceCommand.CommandText = """
                    UPDATE file_references
                    SET availability_reason = @reason, last_checked_at_utc = @now
                    WHERE item_id = @id;
                    """;
                referenceCommand.Parameters.AddWithValue("@reason", DbValue(reason));
                referenceCommand.Parameters.AddWithValue("@now", ToTimestamp(DateTimeOffset.UtcNow));
                referenceCommand.Parameters.AddWithValue("@id", ToBytes(id));
                await referenceCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task ReplaceFileReferenceAsync(
        Guid id,
        FileCandidate replacement,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using (var itemCommand = connection.CreateCommand())
            {
                itemCommand.Transaction = (SqliteTransaction)transaction;
                itemCommand.CommandText = """
                    UPDATE items
                    SET title = @title, kind = @kind, status = @status, search_text = @search_text, revision = revision + 1
                    WHERE id = @id AND source = @source;
                    """;
                itemCommand.Parameters.AddWithValue("@title", replacement.Title);
                itemCommand.Parameters.AddWithValue("@kind", replacement.EntryKind == FileEntryKind.Folder ? (int)ItemKind.Folder : (int)ItemKind.File);
                itemCommand.Parameters.AddWithValue("@status", (int)replacement.Status);
                itemCommand.Parameters.AddWithValue("@search_text", ContentClassifier.BuildSearchText(replacement.Title, replacement.OriginalPath));
                itemCommand.Parameters.AddWithValue("@id", ToBytes(id));
                itemCommand.Parameters.AddWithValue("@source", (int)ItemSource.Space);
                await itemCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var referenceCommand = connection.CreateCommand())
            {
                referenceCommand.Transaction = (SqliteTransaction)transaction;
                referenceCommand.CommandText = """
                    UPDATE file_references
                    SET original_path = @original_path,
                        normalized_path = @normalized_path,
                        entry_kind = @entry_kind,
                        extension = @extension,
                        known_size = @known_size,
                        known_modified_at_utc = @known_modified_at_utc,
                        last_checked_at_utc = @last_checked_at_utc,
                        availability_reason = @availability_reason
                    WHERE item_id = @id;
                    """;
                referenceCommand.Parameters.AddWithValue("@original_path", replacement.OriginalPath);
                referenceCommand.Parameters.AddWithValue("@normalized_path", replacement.NormalizedPath);
                referenceCommand.Parameters.AddWithValue("@entry_kind", (int)replacement.EntryKind);
                referenceCommand.Parameters.AddWithValue("@extension", DbValue(replacement.Extension));
                referenceCommand.Parameters.AddWithValue("@known_size", DbValue(replacement.KnownSize));
                referenceCommand.Parameters.AddWithValue("@known_modified_at_utc", DbTimestamp(replacement.KnownModifiedAtUtc));
                referenceCommand.Parameters.AddWithValue("@last_checked_at_utc", ToTimestamp(DateTimeOffset.UtcNow));
                referenceCommand.Parameters.AddWithValue("@availability_reason", DbValue(replacement.AvailabilityReason));
                referenceCommand.Parameters.AddWithValue("@id", ToBytes(id));
                await referenceCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<string?> RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var removed = await RemoveManyCoreAsync(connection, [id], cancellationToken).ConfigureAwait(false);
            return removed.PayloadPaths.FirstOrDefault();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<ClearResult> ClearClipboardAsync(
        DateTimeOffset? fromUtc,
        bool includePinned,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var sql = new StringBuilder("SELECT id FROM items WHERE source = @source");
            await using var command = connection.CreateCommand();
            command.Parameters.AddWithValue("@source", (int)ItemSource.Clipboard);
            if (!includePinned)
            {
                sql.Append(" AND is_pinned = 0");
            }

            if (fromUtc is not null)
            {
                sql.Append(" AND created_at_utc >= @from");
                command.Parameters.AddWithValue("@from", ToTimestamp(fromUtc.Value));
            }

            command.CommandText = sql.Append(';').ToString();
            var ids = new List<Guid>();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    ids.Add(ReadGuid(reader, 0));
                }
            }

            var removed = await RemoveManyCoreAsync(connection, ids, cancellationToken).ConfigureAwait(false);
            return new ClearResult(removed.RemovedCount, removed.PayloadPaths);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<RetentionResult> ApplyRetentionAsync(
        DateTimeOffset ageCutoffUtc,
        int countLimit,
        CancellationToken cancellationToken = default)
    {
        var items = await QueryAsync(
                new ItemQuery(Source: ItemSource.Clipboard, Limit: 100_000),
                cancellationToken)
            .ConfigureAwait(false);
        var ids = RetentionPolicy.SelectExpired(items, ageCutoffUtc, countLimit);
        if (ids.Count == 0)
        {
            return new RetentionResult(0, Array.Empty<string>());
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var removed = await RemoveManyCoreAsync(connection, ids, cancellationToken).ConfigureAwait(false);
            return new RetentionResult(removed.RemovedCount, removed.PayloadPaths);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<int> CountAsync(
        ItemSource? source = null,
        bool pinnedOnly = false,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var sql = new StringBuilder("SELECT COUNT(*) FROM items");
        var clauses = new List<string>();
        await using var command = connection.CreateCommand();
        if (source is not null)
        {
            clauses.Add("source = @source");
            command.Parameters.AddWithValue("@source", (int)source.Value);
        }

        if (pinnedOnly)
        {
            clauses.Add("is_pinned = 1");
        }

        if (clauses.Count > 0)
        {
            sql.Append(" WHERE ").AppendJoin(" AND ", clauses);
        }

        command.CommandText = sql.Append(';').ToString();
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    private async Task ExecuteItemUpdateAsync(
        string sql,
        Guid id,
        object value,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("@id", ToBytes(id));
            command.Parameters.AddWithValue("@value", value);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static async Task<Guid?> FindFileDuplicateAsync(
        SqliteConnection connection,
        ItemSource source,
        string normalizedPath,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.item_id
            FROM file_references f
            JOIN items i ON i.id = f.item_id
            WHERE i.source = @source
              AND f.normalized_path = @path COLLATE NOCASE
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@source", (int)source);
        command.Parameters.AddWithValue("@path", normalizedPath);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is byte[] bytes ? new Guid(bytes) : null;
    }

    private static async Task InsertBaseItemAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        Guid itemId,
        ItemSource source,
        ItemKind kind,
        string title,
        DateTimeOffset createdAtUtc,
        ItemStatus status,
        string searchText,
        string? fingerprint,
        Guid? payloadId,
        CancellationToken cancellationToken,
        string? metadataJson = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO items (
                id, source, kind, title, created_at_utc, last_used_at_utc, is_pinned, status,
                fingerprint, search_text, payload_id, metadata_json, revision)
            VALUES (
                @id, @source, @kind, @title, @created_at_utc, NULL, 0, @status,
                @fingerprint, @search_text, @payload_id, @metadata_json, 1);
            """;
        command.Parameters.AddWithValue("@id", ToBytes(itemId));
        command.Parameters.AddWithValue("@source", (int)source);
        command.Parameters.AddWithValue("@kind", (int)kind);
        command.Parameters.AddWithValue("@title", title.Length <= 512 ? title : title[..512]);
        command.Parameters.AddWithValue("@created_at_utc", ToTimestamp(createdAtUtc));
        command.Parameters.AddWithValue("@status", (int)status);
        command.Parameters.AddWithValue("@fingerprint", DbValue(fingerprint));
        command.Parameters.AddWithValue("@search_text", searchText);
        command.Parameters.AddWithValue("@payload_id", payloadId is null ? DBNull.Value : ToBytes(payloadId.Value));
        command.Parameters.AddWithValue("@metadata_json", DbValue(metadataJson));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertPayloadAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        PayloadRecord payload,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO payloads (
                id, kind, relative_path, byte_length, content_hash, created_at_utc, storage_version)
            VALUES (
                @id, @kind, @relative_path, @byte_length, @content_hash, @created_at_utc, @storage_version);
            """;
        command.Parameters.AddWithValue("@id", ToBytes(payload.Id));
        command.Parameters.AddWithValue("@kind", payload.Kind);
        command.Parameters.AddWithValue("@relative_path", payload.RelativePath);
        command.Parameters.AddWithValue("@byte_length", payload.ByteLength);
        command.Parameters.AddWithValue("@content_hash", Convert.FromHexString(payload.ContentHash));
        command.Parameters.AddWithValue("@created_at_utc", ToTimestamp(payload.CreatedAtUtc));
        command.Parameters.AddWithValue("@storage_version", payload.StorageVersion);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<DropItem?> GetWithConnectionAsync(
        SqliteConnection connection,
        Guid id,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = string.Concat(SelectSql, " WHERE i.id = @id LIMIT 1;");
        command.Parameters.AddWithValue("@id", ToBytes(id));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadItem(reader) : null;
    }

    private static async Task<RetentionResult> RemoveManyCoreAsync(
        SqliteConnection connection,
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return new RetentionResult(0, Array.Empty<string>());
        }

        var payloads = new List<(Guid Id, string Path)>();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var id in ids)
        {
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = (SqliteTransaction)transaction;
                select.CommandText = """
                    SELECT p.id, p.relative_path
                    FROM items i
                    JOIN payloads p ON p.id = i.payload_id
                    WHERE i.id = @id;
                    """;
                select.Parameters.AddWithValue("@id", ToBytes(id));
                await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    payloads.Add((ReadGuid(reader, 0), reader.GetString(1)));
                }
            }

            await using var delete = connection.CreateCommand();
            delete.Transaction = (SqliteTransaction)transaction;
            delete.CommandText = "DELETE FROM items WHERE id = @id;";
            delete.Parameters.AddWithValue("@id", ToBytes(id));
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var payload in payloads)
        {
            await using var deletePayload = connection.CreateCommand();
            deletePayload.Transaction = (SqliteTransaction)transaction;
            deletePayload.CommandText = "DELETE FROM payloads WHERE id = @id;";
            deletePayload.Parameters.AddWithValue("@id", ToBytes(payload.Id));
            await deletePayload.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new RetentionResult(ids.Count, payloads.Select(payload => payload.Path).ToArray());
    }

    private static DropItem ReadItem(SqliteDataReader reader)
    {
        var file = reader.IsDBNull(13)
            ? null
            : new FileReference(
                reader.GetString(13),
                reader.GetString(14),
                (FileEntryKind)reader.GetInt32(15),
                GetNullableString(reader, 16),
                GetNullableInt64(reader, 17),
                GetNullableTimestamp(reader, 18),
                GetNullableTimestamp(reader, 19),
                GetNullableString(reader, 20));

        var text = reader.IsDBNull(21)
            ? null
            : new TextPayload(
                GetNullableString(reader, 21),
                reader.GetInt32(22),
                (DetectedSubtype)reader.GetInt32(23),
                (DetectionConfidence)reader.GetInt32(24),
                GetNullableString(reader, 25));

        var image = reader.IsDBNull(26)
            ? null
            : new ImagePayload(
                reader.GetInt32(26),
                reader.GetInt32(27),
                reader.GetInt64(28),
                reader.GetString(29),
                reader.IsDBNull(30) ? null : reader.GetInt32(30) != 0,
                reader.GetInt32(31));

        var url = reader.IsDBNull(32)
            ? null
            : new UrlMetadata(
                reader.GetString(32),
                reader.GetString(33),
                reader.GetString(34),
                reader.GetString(35));

        var payload = reader.IsDBNull(36)
            ? null
            : new PayloadRecord(
                ReadGuid(reader, 36),
                reader.GetString(37),
                reader.GetString(38),
                reader.GetInt64(39),
                Convert.ToHexString((byte[])reader.GetValue(40)).ToLowerInvariant(),
                ParseTimestamp(reader.GetString(41)),
                reader.GetInt32(42));

        return new DropItem(
            ReadGuid(reader, 0),
            (ItemSource)reader.GetInt32(1),
            (ItemKind)reader.GetInt32(2),
            reader.GetString(3),
            ParseTimestamp(reader.GetString(4)),
            GetNullableTimestamp(reader, 5),
            reader.GetInt32(6) != 0,
            (ItemStatus)reader.GetInt32(7),
            reader.GetString(8),
            reader.GetInt32(9),
            GetNullableString(reader, 10),
            GetNullableString(reader, 11),
            file,
            text,
            image,
            url,
            payload);
    }

    private const string SelectSql = """
        SELECT
            i.id, i.source, i.kind, i.title, i.created_at_utc, i.last_used_at_utc,
            i.is_pinned, i.status, i.search_text, i.revision, i.fingerprint, i.metadata_json, i.payload_id,
            f.original_path, f.normalized_path, f.entry_kind, f.extension, f.known_size,
            f.known_modified_at_utc, f.last_checked_at_utc, f.availability_reason,
            t.inline_text, t.character_count, t.detected_subtype, t.detection_confidence, t.language_hint,
            im.pixel_width, im.pixel_height, im.encoded_bytes, im.mime_type, im.has_alpha, im.thumbnail_revision,
            u.normalized_url, u.display_url, u.host, u.scheme,
            p.id, p.kind, p.relative_path, p.byte_length, p.content_hash, p.created_at_utc, p.storage_version
        FROM items i
        LEFT JOIN file_references f ON f.item_id = i.id
        LEFT JOIN text_payloads t ON t.item_id = i.id
        LEFT JOIN image_payloads im ON im.item_id = i.id
        LEFT JOIN url_metadata u ON u.item_id = i.id
        LEFT JOIN payloads p ON p.id = i.payload_id
        """;

    private static byte[] ToBytes(Guid id) => id.ToByteArray();

    private static Guid ReadGuid(SqliteDataReader reader, int ordinal) => new((byte[])reader.GetValue(ordinal));

    private static object DbValue(object? value) => value ?? DBNull.Value;

    private static object DbTimestamp(DateTimeOffset? value) => value is null ? DBNull.Value : ToTimestamp(value.Value);

    private static string ToTimestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static DateTimeOffset? GetNullableTimestamp(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : ParseTimestamp(reader.GetString(ordinal));

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? GetNullableInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static string EscapeLike(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
}
