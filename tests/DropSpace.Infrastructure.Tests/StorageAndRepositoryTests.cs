using System.Text;
using DropSpace.Core.Models;
using DropSpace.Core.Policies;
using DropSpace.Infrastructure.Data;
using DropSpace.Infrastructure.Settings;
using DropSpace.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class StorageAndRepositoryTests
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
    public async Task Settings_RoundTripTypedValuesAtomically()
    {
        var service = new JsonSettingsService(_paths);
        var expected = new AppSettings
        {
            ClipboardPaused = true,
            CaptureImages = false,
            RetentionDays = 14,
            RetentionItemCount = 250,
            Theme = ThemePreference.Dark,
            CloseBehavior = CloseBehavior.Exit,
        };

        await service.SaveAsync(expected);
        var actual = await service.LoadAsync();

        Assert.AreEqual(expected, actual);
        Assert.IsFalse(File.Exists(string.Concat(_paths.Settings, ".tmp")));
    }

    [TestMethod]
    public async Task PayloadStore_WritesHashesReadsAndDeletes()
    {
        var store = new FilePayloadStore(_paths);
        var bytes = Encoding.UTF8.GetBytes("local payload");
        await using var source = new MemoryStream(bytes);

        var record = await store.WriteAsync("images", source, 1_024);
        using var copy = new MemoryStream();
        await using (var read = await store.OpenReadAsync(record.RelativePath))
        {
            await read.CopyToAsync(copy);
        }

        CollectionAssert.AreEqual(bytes, copy.ToArray());
        Assert.AreEqual(FingerprintService.ForBytes(bytes), record.ContentHash);

        var exportPath = Path.Combine(_root, "export.png");
        await store.ExportAsync(record.RelativePath, exportPath);
        CollectionAssert.AreEqual(bytes, await File.ReadAllBytesAsync(exportPath));

        await store.DeleteAsync(record.RelativePath);
        Assert.IsFalse(File.Exists(store.ResolvePath(record.RelativePath)));
    }

    [TestMethod]
    public async Task PayloadStore_DeletesPartialFileWhenLimitIsExceeded()
    {
        var store = new FilePayloadStore(_paths);
        await using var source = new MemoryStream(new byte[256]);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.WriteAsync("images", source, 32));

        var files = Directory.Exists(_paths.Payloads)
            ? Directory.EnumerateFiles(_paths.Payloads, "*", SearchOption.AllDirectories).ToArray()
            : [];
        Assert.AreEqual(0, files.Length);
    }

    [TestMethod]
    public async Task Repository_DeduplicatesRecentClipboardTextAndSupportsSearch()
    {
        var repository = CreateRepository();
        var candidate = ContentClassifier.CreateTextCandidate("https://example.com/docs?q=drop");

        var first = await repository.AddTextAsync(candidate);
        var duplicate = await repository.AddTextAsync(candidate);
        var results = await repository.QueryAsync(new ItemQuery(Search: "EXAMPLE"));

        Assert.AreEqual(first.Id, duplicate.Id);
        Assert.AreEqual(2, duplicate.Revision);
        Assert.AreEqual(1, results.Count);
        Assert.AreEqual(ItemKind.Url, results[0].Kind);
        Assert.AreEqual("example.com", results[0].Url?.Host);
    }

    [TestMethod]
    public async Task Database_LoadsAPatchedSQLiteRuntime()
    {
        var database = new SqliteDatabase(_paths, NullLogger<SqliteDatabase>.Instance);
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sqlite_version();";

        var value = (string)(await command.ExecuteScalarAsync())!;

        Assert.IsTrue(Version.Parse(value) >= new Version(3, 50, 2), $"SQLite {value} is below the patched baseline.");
    }

    [TestMethod]
    public async Task FileReferenceService_ReportsAvailableThenMissing()
    {
        Directory.CreateDirectory(_root);
        var sourcePath = Path.Combine(_root, "availability.txt");
        await File.WriteAllTextAsync(sourcePath, "source");
        var service = new LocalFileReferenceService();

        var candidate = await service.InspectAsync(sourcePath);
        var reference = new FileReference(
            candidate.OriginalPath,
            candidate.NormalizedPath,
            candidate.EntryKind,
            candidate.Extension,
            candidate.KnownSize,
            candidate.KnownModifiedAtUtc,
            DateTimeOffset.UtcNow,
            candidate.AvailabilityReason);
        var available = await service.CheckAvailabilityAsync(reference);
        File.Delete(sourcePath);
        var missing = await service.CheckAvailabilityAsync(reference);

        Assert.AreEqual(ItemStatus.Available, candidate.Status);
        Assert.AreEqual(ItemStatus.Available, available.Status);
        Assert.AreEqual(ItemStatus.Missing, missing.Status);
    }

    [TestMethod]
    public async Task Repository_FileRemovalNeverDeletesSourceFile()
    {
        Directory.CreateDirectory(_root);
        var sourcePath = Path.Combine(_root, "source.txt");
        await File.WriteAllTextAsync(sourcePath, "keep me");
        var repository = CreateRepository();
        var fileReferences = new LocalFileReferenceService();

        var item = await repository.AddFileAsync(await fileReferences.InspectAsync(sourcePath));
        var duplicate = await repository.AddFileAsync(await fileReferences.InspectAsync(sourcePath));
        await repository.RemoveAsync(item.Id);

        Assert.AreEqual(item.Id, duplicate.Id);
        Assert.IsTrue(File.Exists(sourcePath));
        Assert.IsNull(await repository.GetAsync(item.Id));
    }

    [TestMethod]
    public async Task Repository_ClearClipboardPreservesPinnedItemsAndReturnsPayloads()
    {
        var repository = CreateRepository();
        var text = await repository.AddTextAsync(ContentClassifier.CreateTextCandidate("ordinary text"));
        await repository.SetPinnedAsync(text.Id, true);
        var payload = new PayloadRecord(
            Guid.NewGuid(),
            "images",
            Path.Combine("images", "aa", "image.bin"),
            4,
            FingerprintService.ForBytes([1, 2, 3, 4]),
            DateTimeOffset.UtcNow,
            1);
        await repository.AddImageAsync(new ImageCandidate(
            payload.ContentHash,
            1,
            1,
            4,
            "image/png",
            true,
            payload));

        var result = await repository.ClearClipboardAsync(fromUtc: null, includePinned: false);

        Assert.AreEqual(1, result.RemovedCount);
        CollectionAssert.Contains(result.PayloadPaths.ToArray(), payload.RelativePath);
        Assert.IsNotNull(await repository.GetAsync(text.Id));
        Assert.AreEqual(1, await repository.CountAsync(ItemSource.Clipboard, pinnedOnly: true));
    }

    private SqliteItemRepository CreateRepository()
    {
        var database = new SqliteDatabase(_paths, NullLogger<SqliteDatabase>.Instance);
        return new SqliteItemRepository(database, NullLogger<SqliteItemRepository>.Instance);
    }
}
