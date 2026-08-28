using DropSpace.Core.Models;
using DropSpace.Infrastructure.Data;
using DropSpace.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class UndoRepositoryTests
{
    private string _root = null!;
    private AppStoragePaths _paths = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "DropSpace-tests", Guid.NewGuid().ToString("N"));
        _paths = new AppStoragePaths(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public async Task PendingRemovalHidesItemAndUndoPreservesTheRecord()
    {
        Directory.CreateDirectory(_root);
        var sourcePath = Path.Combine(_root, "source.txt");
        await File.WriteAllTextAsync(sourcePath, "keep");
        var repository = CreateRepository();
        var item = await repository.AddSpaceFileAsync(await new LocalFileReferenceService().InspectAsync(sourcePath), "metadata");
        await repository.SetPinnedAsync(item.Id, true);

        var token = "pending-test";
        var marked = await repository.BeginPendingRemovalAsync(
            [item.Id],
            token,
            DateTimeOffset.UtcNow.AddSeconds(8));

        Assert.AreEqual(1, marked);
        Assert.AreEqual(0, await repository.CountAsync(ItemSource.Space));
        Assert.IsNull(await repository.GetAsync(item.Id));
        Assert.AreEqual(1, await repository.UndoPendingRemovalAsync(token));

        var restored = await repository.GetAsync(item.Id);
        Assert.IsNotNull(restored);
        Assert.AreEqual(item.Id, restored!.Id);
        Assert.AreEqual("metadata", restored.MetadataJson);
        Assert.IsTrue(restored.IsPinned);
        Assert.IsTrue(File.Exists(sourcePath));
    }

    [TestMethod]
    public async Task FinalizePendingRemovalReturnsOwnedPayloadButNeverTouchesSourceFiles()
    {
        Directory.CreateDirectory(_root);
        var sourcePath = Path.Combine(_root, "source.txt");
        await File.WriteAllTextAsync(sourcePath, "keep");
        var repository = CreateRepository();
        var fileItem = await repository.AddFileAsync(await new LocalFileReferenceService().InspectAsync(sourcePath));
        var payloadStore = new FilePayloadStore(_paths);
        await using var input = new MemoryStream([1, 2, 3, 4]);
        var payload = await payloadStore.WriteFileAsync("images", ".bin", input, 1024);
        var imageItem = await repository.AddImageAsync(new ImageCandidate(
            payload.ContentHash,
            1,
            1,
            payload.ByteLength,
            "image/png",
            false,
            payload));

        var token = "finalize-test";
        Assert.AreEqual(2, await repository.BeginPendingRemovalAsync(
            [fileItem.Id, imageItem.Id],
            token,
            DateTimeOffset.UtcNow.AddSeconds(8)));
        var finalized = await repository.FinalizePendingRemovalAsync(token);

        Assert.AreEqual(2, finalized.RemovedCount);
        CollectionAssert.Contains(finalized.PayloadPaths.ToArray(), payload.RelativePath);
        Assert.IsNull(await repository.GetAsync(fileItem.Id));
        Assert.IsNull(await repository.GetAsync(imageItem.Id));
        Assert.IsTrue(File.Exists(sourcePath));
        await payloadStore.DeleteAsync(payload.RelativePath);
        Assert.IsFalse(File.Exists(payloadStore.ResolvePath(payload.RelativePath)));
    }

    [TestMethod]
    public async Task ExpiredPendingRemovalIsFinalizedDuringRecovery()
    {
        var repository = CreateRepository();
        var item = await repository.AddSpaceTextAsync(
            DropSpace.Core.Policies.ContentClassifier.CreateTextCandidate("pending"));
        Assert.AreEqual(1, await repository.BeginPendingRemovalAsync(
            [item.Id],
            "expired-test",
            DateTimeOffset.UtcNow.AddSeconds(-1)));

        var finalized = await repository.FinalizeExpiredPendingRemovalsAsync(DateTimeOffset.UtcNow);

        Assert.AreEqual(1, finalized.RemovedCount);
        Assert.AreEqual(0, await repository.CountAsync(ItemSource.Space));
    }

    [TestMethod]
    public async Task SchemaVersionTwoMigratesToVersionThreeWithPendingColumns()
    {
        await CreateSchemaV2Async();
        var database = new SqliteDatabase(_paths, NullLogger<SqliteDatabase>.Instance);

        await using var connection = await database.OpenConnectionAsync();
        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        Assert.AreEqual(3L, (long)(await versionCommand.ExecuteScalarAsync())!);

        await using var columnCommand = connection.CreateCommand();
        columnCommand.CommandText = "SELECT COUNT(*) FROM pragma_table_info('items') WHERE name IN ('pending_delete_token', 'pending_delete_expires_at_utc');";
        Assert.AreEqual(2L, (long)(await columnCommand.ExecuteScalarAsync())!);
        Assert.IsTrue(Directory.EnumerateFiles(_paths.Backups, "pre-migration-2-*.db").Any());
    }

    private SqliteItemRepository CreateRepository()
    {
        var database = new SqliteDatabase(_paths, NullLogger<SqliteDatabase>.Instance);
        return new SqliteItemRepository(database, NullLogger<SqliteItemRepository>.Instance);
    }

    private async Task CreateSchemaV2Async()
    {
        _paths.EnsureCreated();
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _paths.Database,
            Mode = SqliteOpenMode.ReadWriteCreate,
        };
        await using var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = ON;
            CREATE TABLE payloads (id BLOB PRIMARY KEY, kind TEXT NOT NULL, relative_path TEXT NOT NULL UNIQUE, byte_length INTEGER NOT NULL, content_hash BLOB NOT NULL, created_at_utc TEXT NOT NULL, storage_version INTEGER NOT NULL);
            CREATE TABLE items (id BLOB PRIMARY KEY, source INTEGER NOT NULL, kind INTEGER NOT NULL, title TEXT NOT NULL, created_at_utc TEXT NOT NULL, last_used_at_utc TEXT NULL, is_pinned INTEGER NOT NULL DEFAULT 0, status INTEGER NOT NULL, fingerprint TEXT NULL, search_text TEXT NOT NULL, payload_id BLOB NULL REFERENCES payloads(id), metadata_json TEXT NULL, revision INTEGER NOT NULL DEFAULT 1);
            CREATE TABLE file_references (item_id BLOB PRIMARY KEY REFERENCES items(id) ON DELETE CASCADE, original_path TEXT NOT NULL, normalized_path TEXT NOT NULL, entry_kind INTEGER NOT NULL, extension TEXT NULL, known_size INTEGER NULL, known_modified_at_utc TEXT NULL, volume_hint TEXT NULL, last_checked_at_utc TEXT NULL, availability_reason TEXT NULL);
            CREATE TABLE text_payloads (item_id BLOB PRIMARY KEY REFERENCES items(id) ON DELETE CASCADE, inline_text TEXT NULL, character_count INTEGER NOT NULL, detected_subtype INTEGER NOT NULL, detection_confidence INTEGER NOT NULL, language_hint TEXT NULL);
            CREATE TABLE image_payloads (item_id BLOB PRIMARY KEY REFERENCES items(id) ON DELETE CASCADE, pixel_width INTEGER NOT NULL, pixel_height INTEGER NOT NULL, encoded_bytes INTEGER NOT NULL, mime_type TEXT NOT NULL, has_alpha INTEGER NULL, thumbnail_revision INTEGER NOT NULL DEFAULT 1);
            CREATE TABLE url_metadata (item_id BLOB PRIMARY KEY REFERENCES items(id) ON DELETE CASCADE, normalized_url TEXT NOT NULL, display_url TEXT NOT NULL, host TEXT NOT NULL, scheme TEXT NOT NULL);
            CREATE TABLE paired_devices (id TEXT PRIMARY KEY, display_name TEXT NOT NULL, platform INTEGER NOT NULL, identity_fingerprint TEXT NOT NULL, secret_key_id TEXT NOT NULL, capabilities INTEGER NOT NULL, created_at_utc TEXT NOT NULL, last_seen_at_utc TEXT NULL, is_blocked INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE transfer_sessions (id TEXT PRIMARY KEY, direction INTEGER NOT NULL, mode INTEGER NOT NULL, peer_id TEXT NULL, state INTEGER NOT NULL, created_at_utc TEXT NOT NULL, completed_at_utc TEXT NULL, item_count INTEGER NOT NULL, total_bytes INTEGER NOT NULL, transferred_bytes INTEGER NOT NULL DEFAULT 0, error_category TEXT NULL);
            PRAGMA user_version = 2;
            """;
        await command.ExecuteNonQueryAsync();
    }
}
