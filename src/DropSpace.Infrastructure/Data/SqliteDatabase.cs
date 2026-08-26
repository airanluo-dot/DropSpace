using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Data;

public sealed class SqliteDatabase(
    AppStoragePaths paths,
    ILogger<SqliteDatabase> logger)
{
    public const int CurrentSchemaVersion = 2;

    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private bool _initialized;

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

    private static async Task ValidateSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table'
              AND name IN ('items', 'file_references', 'text_payloads', 'image_payloads', 'url_metadata', 'payloads', 'paired_devices', 'transfer_sessions');
            """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var count = Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
        if (count != 8)
        {
            throw new InvalidDataException("Database schema validation failed.");
        }
    }
}
