using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Data;

public sealed class SqliteDatabase(
    AppStoragePaths paths,
    ILogger<SqliteDatabase> logger)
{
    public const int CurrentSchemaVersion = 3;

    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private bool _initialized;

    private static readonly TableDescriptor[] RequiredTables =
    [
        new("payloads",
        [
            new("id", "BLOB", false, true),
            new("kind", "TEXT", true, false),
            new("relative_path", "TEXT", true, false),
            new("byte_length", "INTEGER", true, false),
            new("content_hash", "BLOB", true, false),
            new("created_at_utc", "TEXT", true, false),
            new("storage_version", "INTEGER", true, false),
        ]),
        new("items",
        [
            new("id", "BLOB", false, true),
            new("source", "INTEGER", true, false),
            new("kind", "INTEGER", true, false),
            new("title", "TEXT", true, false),
            new("created_at_utc", "TEXT", true, false),
            new("last_used_at_utc", "TEXT", false, false),
            new("is_pinned", "INTEGER", true, false),
            new("status", "INTEGER", true, false),
            new("fingerprint", "TEXT", false, false),
            new("search_text", "TEXT", true, false),
            new("payload_id", "BLOB", false, false),
            new("metadata_json", "TEXT", false, false),
            new("revision", "INTEGER", true, false),
            new("pending_delete_token", "TEXT", false, false),
            new("pending_delete_expires_at_utc", "TEXT", false, false),
        ]),
        new("file_references",
        [
            new("item_id", "BLOB", false, true),
            new("original_path", "TEXT", true, false),
            new("normalized_path", "TEXT", true, false),
            new("entry_kind", "INTEGER", true, false),
            new("extension", "TEXT", false, false),
            new("known_size", "INTEGER", false, false),
            new("known_modified_at_utc", "TEXT", false, false),
            new("volume_hint", "TEXT", false, false),
            new("last_checked_at_utc", "TEXT", false, false),
            new("availability_reason", "TEXT", false, false),
        ]),
        new("text_payloads",
        [
            new("item_id", "BLOB", false, true),
            new("inline_text", "TEXT", false, false),
            new("character_count", "INTEGER", true, false),
            new("detected_subtype", "INTEGER", true, false),
            new("detection_confidence", "INTEGER", true, false),
            new("language_hint", "TEXT", false, false),
        ]),
        new("image_payloads",
        [
            new("item_id", "BLOB", false, true),
            new("pixel_width", "INTEGER", true, false),
            new("pixel_height", "INTEGER", true, false),
            new("encoded_bytes", "INTEGER", true, false),
            new("mime_type", "TEXT", true, false),
            new("has_alpha", "INTEGER", false, false),
            new("thumbnail_revision", "INTEGER", true, false),
        ]),
        new("url_metadata",
        [
            new("item_id", "BLOB", false, true),
            new("normalized_url", "TEXT", true, false),
            new("display_url", "TEXT", true, false),
            new("host", "TEXT", true, false),
            new("scheme", "TEXT", true, false),
        ]),
        new("paired_devices",
        [
            new("id", "TEXT", false, true),
            new("display_name", "TEXT", true, false),
            new("platform", "INTEGER", true, false),
            new("identity_fingerprint", "TEXT", true, false),
            new("secret_key_id", "TEXT", true, false),
            new("capabilities", "INTEGER", true, false),
            new("created_at_utc", "TEXT", true, false),
            new("last_seen_at_utc", "TEXT", false, false),
            new("is_blocked", "INTEGER", true, false),
        ]),
        new("transfer_sessions",
        [
            new("id", "TEXT", false, true),
            new("direction", "INTEGER", true, false),
            new("mode", "INTEGER", true, false),
            new("peer_id", "TEXT", false, false),
            new("state", "INTEGER", true, false),
            new("created_at_utc", "TEXT", true, false),
            new("completed_at_utc", "TEXT", false, false),
            new("item_count", "INTEGER", true, false),
            new("total_bytes", "INTEGER", true, false),
            new("transferred_bytes", "INTEGER", true, false),
            new("error_category", "TEXT", false, false),
        ]),
    ];

    private static readonly IndexDescriptor[] RequiredIndexes =
    [
        new("items", "ix_items_source_created", false, ["source", "created_at_utc"]),
        new("items", "ix_items_pinned_created", false, ["is_pinned", "created_at_utc"]),
        new("items", "ix_items_kind_created", false, ["kind", "created_at_utc"]),
        new("items", "ix_items_fingerprint_source_created", false, ["fingerprint", "source", "created_at_utc"]),
        new("items", "ix_items_pending_delete", false, ["pending_delete_token", "pending_delete_expires_at_utc"]),
        new("file_references", "ix_file_references_normalized_path", false, ["normalized_path"]),
        new("url_metadata", "ix_url_metadata_host", false, ["host"]),
        new("transfer_sessions", "ix_transfer_sessions_peer_created", false, ["peer_id", "created_at_utc"]),
        new("paired_devices", "ix_paired_devices_last_seen", false, ["last_seen_at_utc"]),
        new("payloads", null, true, ["relative_path"]),
    ];

    private static readonly ForeignKeyDescriptor[] RequiredForeignKeys =
    [
        new("items", "payload_id", "payloads", "id", "NO ACTION"),
        new("file_references", "item_id", "items", "id", "CASCADE"),
        new("text_payloads", "item_id", "items", "id", "CASCADE"),
        new("image_payloads", "item_id", "items", "id", "CASCADE"),
        new("url_metadata", "item_id", "items", "id", "CASCADE"),
    ];

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            paths.EnsureCreated();
            await using var connection = await OpenConnectionCoreAsync(cancellationToken).ConfigureAwait(false);
            var version = await GetUserVersionAsync(connection, cancellationToken).ConfigureAwait(false);

            if (version > CurrentSchemaVersion)
            {
                throw new UnsupportedSchemaVersionException(version, CurrentSchemaVersion);
            }

            if (version < CurrentSchemaVersion)
            {
                await ApplyMigrationsAsync(connection, version, cancellationToken).ConfigureAwait(false);
            }

            await ValidateSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            _initialized = true;
            logger.LogInformation("Database initialized at schema {SchemaVersion}.", CurrentSchemaVersion);
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await OpenConnectionCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenConnectionCoreAsync(CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = paths.Database,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        };

        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 3000; PRAGMA journal_mode = WAL;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private async Task ApplyMigrationsAsync(
        SqliteConnection connection,
        int fromVersion,
        CancellationToken cancellationToken)
    {
        if (fromVersion > 0)
        {
            CreateBackup(connection, fromVersion);
        }

        try
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            if (fromVersion < 1)
            {
                await ApplyV1Async(connection, transaction, cancellationToken).ConfigureAwait(false);
            }

            if (fromVersion < 2)
            {
                await ApplyV2Async(connection, transaction, cancellationToken).ConfigureAwait(false);
            }

            if (fromVersion < 3)
            {
                await ApplyV3Async(connection, transaction, cancellationToken).ConfigureAwait(false);
            }

            await using var versionCommand = connection.CreateCommand();
            versionCommand.Transaction = (SqliteTransaction)transaction;
            versionCommand.CommandText = $"PRAGMA user_version = {CurrentSchemaVersion};";
            await versionCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            throw new DatabaseMigrationException(
                $"Migration from schema {fromVersion} to {CurrentSchemaVersion} failed. The original database was preserved.",
                exception);
        }
    }

    private void CreateBackup(SqliteConnection source, int version)
    {
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var backupPath = Path.Combine(paths.Backups, $"pre-migration-{version}-{stamp}.db");
        var backupBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        };

        using var destination = new SqliteConnection(backupBuilder.ToString());
        destination.Open();
        source.BackupDatabase(destination);
    }

    private static async Task<int> GetUserVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ApplyV1Async(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE payloads (
              id BLOB PRIMARY KEY,
              kind TEXT NOT NULL,
              relative_path TEXT NOT NULL UNIQUE,
              byte_length INTEGER NOT NULL CHECK(byte_length >= 0),
              content_hash BLOB NOT NULL,
              created_at_utc TEXT NOT NULL,
              storage_version INTEGER NOT NULL
            );

            CREATE TABLE items (
              id BLOB PRIMARY KEY,
              source INTEGER NOT NULL,
              kind INTEGER NOT NULL,
              title TEXT NOT NULL CHECK(length(title) <= 512),
              created_at_utc TEXT NOT NULL,
              last_used_at_utc TEXT NULL,
              is_pinned INTEGER NOT NULL DEFAULT 0 CHECK(is_pinned IN (0, 1)),
              status INTEGER NOT NULL,
              fingerprint TEXT NULL,
              search_text TEXT NOT NULL,
              payload_id BLOB NULL REFERENCES payloads(id),
              metadata_json TEXT NULL,
              revision INTEGER NOT NULL DEFAULT 1
            );

            CREATE TABLE file_references (
              item_id BLOB PRIMARY KEY REFERENCES items(id) ON DELETE CASCADE,
              original_path TEXT NOT NULL,
              normalized_path TEXT NOT NULL,
              entry_kind INTEGER NOT NULL,
              extension TEXT NULL,
              known_size INTEGER NULL,
              known_modified_at_utc TEXT NULL,
              volume_hint TEXT NULL,
              last_checked_at_utc TEXT NULL,
              availability_reason TEXT NULL
            );

            CREATE TABLE text_payloads (
              item_id BLOB PRIMARY KEY REFERENCES items(id) ON DELETE CASCADE,
              inline_text TEXT NULL,
              character_count INTEGER NOT NULL,
              detected_subtype INTEGER NOT NULL,
              detection_confidence INTEGER NOT NULL,
              language_hint TEXT NULL
            );

            CREATE TABLE image_payloads (
              item_id BLOB PRIMARY KEY REFERENCES items(id) ON DELETE CASCADE,
              pixel_width INTEGER NOT NULL,
              pixel_height INTEGER NOT NULL,
              encoded_bytes INTEGER NOT NULL,
              mime_type TEXT NOT NULL,
              has_alpha INTEGER NULL,
              thumbnail_revision INTEGER NOT NULL DEFAULT 1
            );

            CREATE TABLE url_metadata (
              item_id BLOB PRIMARY KEY REFERENCES items(id) ON DELETE CASCADE,
              normalized_url TEXT NOT NULL,
              display_url TEXT NOT NULL,
              host TEXT NOT NULL,
              scheme TEXT NOT NULL
            );

            CREATE INDEX ix_items_source_created ON items(source, created_at_utc DESC);
            CREATE INDEX ix_items_pinned_created ON items(is_pinned, created_at_utc DESC);
            CREATE INDEX ix_items_kind_created ON items(kind, created_at_utc DESC);
            CREATE INDEX ix_items_fingerprint_source_created ON items(fingerprint, source, created_at_utc DESC);
            CREATE INDEX ix_file_references_normalized_path ON file_references(normalized_path COLLATE NOCASE);
            CREATE INDEX ix_url_metadata_host ON url_metadata(host COLLATE NOCASE);
            """;

        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyV2Async(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            CREATE TABLE paired_devices (
              id TEXT PRIMARY KEY,
              display_name TEXT NOT NULL CHECK(length(display_name) BETWEEN 1 AND 64),
              platform INTEGER NOT NULL,
              identity_fingerprint TEXT NOT NULL,
              secret_key_id TEXT NOT NULL,
              capabilities INTEGER NOT NULL,
              created_at_utc TEXT NOT NULL,
              last_seen_at_utc TEXT NULL,
              is_blocked INTEGER NOT NULL DEFAULT 0 CHECK(is_blocked IN (0, 1))
            );

            CREATE TABLE transfer_sessions (
              id TEXT PRIMARY KEY,
              direction INTEGER NOT NULL,
              mode INTEGER NOT NULL,
              peer_id TEXT NULL,
              state INTEGER NOT NULL,
              created_at_utc TEXT NOT NULL,
              completed_at_utc TEXT NULL,
              item_count INTEGER NOT NULL CHECK(item_count >= 0),
              total_bytes INTEGER NOT NULL CHECK(total_bytes >= 0),
              transferred_bytes INTEGER NOT NULL DEFAULT 0 CHECK(transferred_bytes >= 0),
              error_category TEXT NULL
            );

            CREATE INDEX ix_transfer_sessions_peer_created ON transfer_sessions(peer_id, created_at_utc DESC);
            CREATE INDEX ix_paired_devices_last_seen ON paired_devices(last_seen_at_utc DESC);
            """;

        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplyV3Async(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        const string sql = """
            ALTER TABLE items ADD COLUMN pending_delete_token TEXT NULL;
            ALTER TABLE items ADD COLUMN pending_delete_expires_at_utc TEXT NULL;
            CREATE INDEX ix_items_pending_delete ON items(pending_delete_token, pending_delete_expires_at_utc);
            """;

        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ValidateSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (await GetUserVersionAsync(connection, cancellationToken).ConfigureAwait(false) != CurrentSchemaVersion)
        {
            throw new InvalidDataException("Database schema version validation failed.");
        }

        await using (var foreignKeyCommand = connection.CreateCommand())
        {
            foreignKeyCommand.CommandText = "PRAGMA foreign_keys;";
            var foreignKeysEnabled = Convert.ToInt32(
                await foreignKeyCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture);
            if (foreignKeysEnabled != 1)
            {
                throw new InvalidDataException("SQLite foreign-key enforcement is disabled.");
            }
        }

        var tables = await ReadTableNamesAsync(connection, cancellationToken).ConfigureAwait(false);
        foreach (var table in RequiredTables)
        {
            if (!tables.Contains(table.Name))
            {
                throw new InvalidDataException($"Required database table '{table.Name}' is missing.");
            }

            var columns = await ReadColumnsAsync(connection, table.Name, cancellationToken).ConfigureAwait(false);
            foreach (var expected in table.Columns)
            {
                if (!columns.TryGetValue(expected.Name, out var actual) ||
                    actual.Affinity != expected.Affinity ||
                    actual.NotNull != expected.NotNull ||
                    actual.PrimaryKey != expected.PrimaryKey)
                {
                    throw new InvalidDataException($"Database column '{table.Name}.{expected.Name}' failed schema validation.");
                }
            }
        }

        foreach (var requiredIndex in RequiredIndexes)
        {
            var indexes = await ReadIndexesAsync(connection, requiredIndex.Table, cancellationToken)
                .ConfigureAwait(false);
            if (!indexes.Any(index =>
                    (requiredIndex.Name is null || string.Equals(index.Name, requiredIndex.Name, StringComparison.Ordinal)) &&
                    index.IsUnique == requiredIndex.IsUnique &&
                    index.Columns.SequenceEqual(requiredIndex.Columns, StringComparer.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException($"Required database index on '{requiredIndex.Table}' is missing.");
            }
        }

        var foreignKeys = await ReadForeignKeysAsync(connection, cancellationToken).ConfigureAwait(false);
        var expectedForeignKeys = RequiredForeignKeys
            .Select(foreignKey => foreignKey.ToKey())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!foreignKeys.SetEquals(expectedForeignKeys))
        {
            throw new InvalidDataException("Database foreign-key schema validation failed.");
        }
    }

    private static async Task<HashSet<string>> ReadTableNamesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private static async Task<Dictionary<string, ColumnDescriptor>> ReadColumnsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info('{EscapePragmaIdentifier(table)}');";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<string, ColumnDescriptor>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var name = reader.GetString(1);
            var declaredType = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            result[name] = new ColumnDescriptor(
                name,
                GetAffinity(declaredType),
                reader.GetInt32(3) != 0,
                reader.GetInt32(5) != 0);
        }

        return result;
    }

    private static async Task<IReadOnlyList<IndexDescriptor>> ReadIndexesAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_list('{EscapePragmaIdentifier(table)}');";
        var indexHeaders = new List<(string Name, bool IsUnique)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                indexHeaders.Add((reader.GetString(1), reader.GetInt32(2) != 0));
            }
        }

        var result = new List<IndexDescriptor>(indexHeaders.Count);
        foreach (var (name, isUnique) in indexHeaders)
        {
            var columns = await ReadIndexColumnsAsync(connection, name, cancellationToken).ConfigureAwait(false);
            result.Add(new IndexDescriptor(table, name, isUnique, columns));
        }

        return result;
    }

    private static async Task<IReadOnlyList<string>> ReadIndexColumnsAsync(
        SqliteConnection connection,
        string index,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA index_info('{EscapePragmaIdentifier(index)}');";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(reader.IsDBNull(2) ? string.Empty : reader.GetString(2));
        }

        return result;
    }

    private static async Task<HashSet<string>> ReadForeignKeysAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in RequiredTables)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA foreign_key_list('{EscapePragmaIdentifier(table.Name)}');";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result.Add(string.Join(
                    "|",
                    table.Name,
                    reader.GetString(3),
                    reader.GetString(2),
                    reader.GetString(4),
                    reader.GetString(6).ToUpperInvariant()));
            }
        }

        return result;
    }

    private static string EscapePragmaIdentifier(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string GetAffinity(string declaredType)
    {
        var type = declaredType.ToUpperInvariant();
        if (type.Contains("INT", StringComparison.Ordinal))
        {
            return "INTEGER";
        }

        if (type.Contains("CHAR", StringComparison.Ordinal) ||
            type.Contains("CLOB", StringComparison.Ordinal) ||
            type.Contains("TEXT", StringComparison.Ordinal))
        {
            return "TEXT";
        }

        if (type.Contains("BLOB", StringComparison.Ordinal) || type.Length == 0)
        {
            return "BLOB";
        }

        if (type.Contains("REAL", StringComparison.Ordinal) ||
            type.Contains("FLOA", StringComparison.Ordinal) ||
            type.Contains("DOUB", StringComparison.Ordinal))
        {
            return "REAL";
        }

        return "NUMERIC";
    }

    private readonly record struct TableDescriptor(
        string Name,
        IReadOnlyList<ColumnDescriptor> Columns);

    private readonly record struct ColumnDescriptor(
        string Name,
        string Affinity,
        bool NotNull,
        bool PrimaryKey);

    private readonly record struct IndexDescriptor(
        string Table,
        string? Name,
        bool IsUnique,
        IReadOnlyList<string> Columns);

    private readonly record struct ForeignKeyDescriptor(
        string Table,
        string From,
        string PrincipalTable,
        string PrincipalColumn,
        string OnDelete)
    {
        public string ToKey() =>
            string.Join("|", Table, From, PrincipalTable, PrincipalColumn, OnDelete);
    }

}
