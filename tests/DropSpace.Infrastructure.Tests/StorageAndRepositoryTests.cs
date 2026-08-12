using System.Text;
using DropSpace.Core.Models;
using DropSpace.Core.Policies;
using DropSpace.Core.Updates;
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
            CaptureFiles = false,
            CaptureFolders = false,
            StartWithWindows = false,
            MaxImageBytes = 64L * 1024 * 1024,
            MaxImagePixels = 75_000_000,
            MaxClipboardFileBytes = 512L * 1024 * 1024,
            MaxClipboardFileTotalBytes = 4L * 1024 * 1024 * 1024,
            MaxClipboardFileItems = 42,
            RetentionDays = 14,
            RetentionItemCount = 250,
            Theme = ThemePreference.Dark,
            CloseBehavior = CloseBehavior.Exit,
            OverlayDisplayMode = OverlayDisplayMode.Notch,
            OverlayMotion = OverlayMotionPreference.Reduced,
            OverlayMonitor = OverlayMonitorPreference.Primary,
            FileDragWakeMode = FileDragWakeMode.ClassicTopEdge,
            AutoCheckForUpdates = false,
            AutoDownloadUpdates = false,
            UpdateChannel = UpdateChannel.Preview,
            LastUpdateCheckUtc = DateTimeOffset.Parse("2026-08-10T00:00:00Z"),
        };

        await service.SaveAsync(expected);
        var actual = await service.LoadAsync();

        Assert.AreEqual(expected, actual);
        Assert.IsFalse(File.Exists(string.Concat(_paths.Settings, ".tmp")));
    }

    [TestMethod]
    public async Task Settings_VersionOneMigratesWithSafeOverlayDefaults()
    {
        _paths.EnsureCreated();
        await File.WriteAllTextAsync(
            _paths.Settings,
            """
            {
              "Version": 1,
              "ClipboardPaused": true,
              "RetentionDays": 21,
              "RetentionItemCount": 300
            }
            """);

        var actual = await new JsonSettingsService(_paths).LoadAsync();

        Assert.AreEqual(AppSettings.CurrentVersion, actual.Version);
        Assert.AreEqual(OverlayDisplayMode.DynamicIsland, actual.OverlayDisplayMode);
        Assert.AreEqual(OverlayMotionPreference.System, actual.OverlayMotion);
        Assert.AreEqual(OverlayMonitorPreference.Automatic, actual.OverlayMonitor);
        Assert.AreEqual(FileDragWakeMode.SmartExperimental, actual.FileDragWakeMode);
        Assert.IsTrue(actual.ClipboardPaused);
        Assert.AreEqual(UpdateChannel.Preview, actual.UpdateChannel);
    }

    [TestMethod]
    public async Task Settings_VersionFourMigratesToSmartDragWithoutChangingExistingPreferences()
    {
        _paths.EnsureCreated();
        await File.WriteAllTextAsync(
            _paths.Settings,
            """
            {
              "Version": 4,
              "Theme": 2,
              "OverlayDisplayMode": 1,
              "UpdateChannel": 1,
              "StartWithWindows": false
            }
            """);

        var actual = await new JsonSettingsService(_paths).LoadAsync();

        Assert.AreEqual(AppSettings.CurrentVersion, actual.Version);
        Assert.AreEqual(FileDragWakeMode.SmartExperimental, actual.FileDragWakeMode);
        Assert.AreEqual(OverlayDisplayMode.Notch, actual.OverlayDisplayMode);
        Assert.AreEqual(ThemePreference.Dark, actual.Theme);
        Assert.AreEqual(UpdateChannel.Preview, actual.UpdateChannel);
        Assert.IsFalse(actual.StartWithWindows);
    }

    [TestMethod]
    public async Task Settings_InvalidDragWakeModeIsQuarantinedToSafeSmartDefault()
    {
        _paths.EnsureCreated();
        await File.WriteAllTextAsync(
            _paths.Settings,
            """
            {
              "Version": 5,
              "FileDragWakeMode": 999,
              "RetentionDays": 21,
              "RetentionItemCount": 300
            }
            """);

        var service = new JsonSettingsService(_paths);
        var actual = await service.LoadAsync();

        Assert.AreEqual(FileDragWakeMode.SmartExperimental, actual.FileDragWakeMode);
        Assert.AreEqual(21, actual.RetentionDays);
        Assert.IsTrue(service.LastLoadRecovery.Recovered);
    }

    [TestMethod]
    public async Task Settings_PersistedNotchLoadsWithoutCrash()
    {
        var service = new JsonSettingsService(_paths);
        var expected = new AppSettings
        {
            OverlayDisplayMode = OverlayDisplayMode.Notch,
            OverlayMotion = OverlayMotionPreference.Full,
        };

        await service.SaveAsync(expected);
        var actual = await service.LoadAsync();

        Assert.AreEqual(OverlayDisplayMode.Notch, actual.OverlayDisplayMode);
        Assert.AreEqual(OverlayMotionPreference.Full, actual.OverlayMotion);
        Assert.IsFalse(service.LastLoadRecovery.Recovered);
    }

    [TestMethod]
    public async Task Settings_VersionTwoMigratesClipboardFilesAndStartupToSafeDefaults()
    {
        _paths.EnsureCreated();
        await File.WriteAllTextAsync(
            _paths.Settings,
            """
            {
              "Version": 2,
              "CaptureImages": false,
              "MaxImageBytes": 33554432,
              "MaxImagePixels": 60000000
            }
            """);

        var actual = await new JsonSettingsService(_paths).LoadAsync();

        Assert.AreEqual(AppSettings.CurrentVersion, actual.Version);
        Assert.IsFalse(actual.CaptureImages);
        Assert.AreEqual(33_554_432, actual.MaxImageBytes);
        Assert.IsTrue(actual.CaptureFiles);
        Assert.IsTrue(actual.CaptureFolders);
        Assert.IsTrue(actual.StartWithWindows);
        Assert.AreEqual(UpdateChannel.Preview, actual.UpdateChannel);
    }

    [TestMethod]
    public async Task Settings_FreshStableDefaultsToStable_WhileExistingPreviewSchemaMigratesToPreview()
    {
        var fresh = await new JsonSettingsService(_paths).LoadAsync();
        Assert.AreEqual(UpdateChannel.Stable, fresh.UpdateChannel);
        Assert.IsTrue(fresh.AutoCheckForUpdates);
        Assert.IsTrue(fresh.AutoDownloadUpdates);
        Assert.IsFalse(fresh.AutoInstallUpdates);

        _paths.EnsureCreated();
        await File.WriteAllTextAsync(
            _paths.Settings,
            """
            {
              "Version": 3,
              "Theme": 2,
              "StartWithWindows": false,
              "RetentionDays": 45
            }
            """);

        var migrated = await new JsonSettingsService(_paths).LoadAsync();
        Assert.AreEqual(AppSettings.CurrentVersion, migrated.Version);
        Assert.AreEqual(UpdateChannel.Preview, migrated.UpdateChannel);
        Assert.AreEqual(ThemePreference.Dark, migrated.Theme);
        Assert.IsFalse(migrated.StartWithWindows);
        Assert.AreEqual(45, migrated.RetentionDays);
    }

    [TestMethod]
    public async Task Settings_FreshPreviewBuildDefaultsToPreviewChannel()
    {
        var service = new JsonSettingsService(
            _paths,
            NullLogger<JsonSettingsService>.Instance,
            UpdateChannel.Preview);

        var fresh = await service.LoadAsync();

        Assert.AreEqual(UpdateChannel.Preview, fresh.UpdateChannel);
        Assert.IsTrue(fresh.AutoCheckForUpdates);
        Assert.IsTrue(fresh.AutoDownloadUpdates);
        Assert.IsFalse(fresh.AutoInstallUpdates);
    }

    [TestMethod]
    public async Task Settings_InvalidOverlayValueIsQuarantinedAndNonUiPreferencesArePreserved()
    {
        _paths.EnsureCreated();
        await File.WriteAllTextAsync(
            _paths.Settings,
            """
            {
              "Version": 2,
              "ClipboardPaused": true,
              "RetentionDays": 21,
              "RetentionItemCount": 300,
              "OverlayDisplayMode": 999,
              "OverlayMotion": 0,
              "OverlayMonitor": 0
            }
            """);
        var databaseSentinel = Path.Combine(_paths.Data, "database-sentinel.bin");
        await File.WriteAllTextAsync(databaseSentinel, "do not delete");
        var service = new JsonSettingsService(_paths);

        var actual = await service.LoadAsync();

        Assert.AreEqual(OverlayDisplayMode.DynamicIsland, actual.OverlayDisplayMode);
        Assert.AreEqual(OverlayMotionPreference.System, actual.OverlayMotion);
        Assert.AreEqual(OverlayMonitorPreference.Automatic, actual.OverlayMonitor);
        Assert.IsTrue(actual.ClipboardPaused);
        Assert.AreEqual(21, actual.RetentionDays);
        Assert.IsTrue(service.LastLoadRecovery.Recovered);
        Assert.IsTrue(service.LastLoadRecovery.PreservedNonUiPreferences);
        Assert.IsTrue(File.Exists(databaseSentinel));
        Assert.AreEqual(1, Directory.GetFiles(_paths.Quarantine, "settings-*.json").Length);
    }

    [TestMethod]
    public async Task Settings_MalformedJsonFallsBackWithoutDeletingDatabaseOrPayloads()
    {
        _paths.EnsureCreated();
        await File.WriteAllTextAsync(_paths.Settings, "{ definitely not JSON");
        var databaseSentinel = Path.Combine(_paths.Data, "dropspace.db");
        var payloadSentinel = Path.Combine(_paths.Payloads, "keep.bin");
        await File.WriteAllTextAsync(databaseSentinel, "database");
        await File.WriteAllTextAsync(payloadSentinel, "payload");
        var service = new JsonSettingsService(_paths);

        var actual = await service.LoadAsync();

        Assert.AreEqual(new AppSettings(), actual);
        Assert.IsTrue(service.LastLoadRecovery.Recovered);
        Assert.IsFalse(service.LastLoadRecovery.PreservedNonUiPreferences);
        Assert.IsTrue(File.Exists(databaseSentinel));
        Assert.IsTrue(File.Exists(payloadSentinel));
    }

    [TestMethod]
    public async Task Settings_ResetUiOnlyPreservesClipboardAndRetentionPreferences()
    {
        var service = new JsonSettingsService(_paths);
        await service.SaveAsync(new AppSettings
        {
            ClipboardPaused = true,
            CaptureImages = false,
            CaptureFiles = false,
            CaptureFolders = false,
            StartWithWindows = false,
            MaxClipboardFileItems = 12,
            RetentionDays = 45,
            Theme = ThemePreference.Dark,
            OverlayDisplayMode = OverlayDisplayMode.Notch,
            OverlayMotion = OverlayMotionPreference.Full,
            OverlayMonitor = OverlayMonitorPreference.Primary,
        });

        var actual = await service.ResetUiSettingsAsync();

        Assert.IsTrue(actual.ClipboardPaused);
        Assert.IsFalse(actual.CaptureImages);
        Assert.IsFalse(actual.CaptureFiles);
        Assert.IsFalse(actual.CaptureFolders);
        Assert.IsFalse(actual.StartWithWindows);
        Assert.AreEqual(12, actual.MaxClipboardFileItems);
        Assert.AreEqual(45, actual.RetentionDays);
        Assert.AreEqual(ThemePreference.System, actual.Theme);
        Assert.AreEqual(OverlayDisplayMode.DynamicIsland, actual.OverlayDisplayMode);
        Assert.AreEqual(OverlayMotionPreference.System, actual.OverlayMotion);
        Assert.AreEqual(OverlayMonitorPreference.Automatic, actual.OverlayMonitor);
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
    public async Task Repository_PreservesNonConsecutiveClipboardTextAndSupportsSearch()
    {
        var repository = CreateRepository();
        var candidate = ContentClassifier.CreateTextCandidate("https://example.com/docs?q=drop");

        var first = await repository.AddTextAsync(candidate);
        await repository.AddTextAsync(ContentClassifier.CreateTextCandidate("intervening clipboard value"));
        var repeated = await repository.AddTextAsync(candidate);
        var results = await repository.QueryAsync(new ItemQuery(Search: "EXAMPLE"));

        Assert.AreNotEqual(first.Id, repeated.Id);
        Assert.AreEqual(1, repeated.Revision);
        Assert.AreEqual(3, await repository.CountAsync(ItemSource.Clipboard));
        Assert.AreEqual(2, results.Count);
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
    public async Task Repository_ClipboardFileIsSeparateFromTemporarySpaceAndPreservesDistinctCaptureEvents()
    {
        Directory.CreateDirectory(_root);
        var sourcePath = Path.Combine(_root, "clipboard-source.txt");
        await File.WriteAllTextAsync(sourcePath, "keep me");
        var repository = CreateRepository();
        var candidate = await new LocalFileReferenceService().InspectAsync(sourcePath);
        var fingerprint = FingerprintService.ForText($"clipboard-file\0{candidate.NormalizedPath}");

        var space = await repository.AddFileAsync(candidate);
        var clipboard = await repository.AddClipboardFileAsync(candidate, fingerprint, "{\"batchItemCount\":1}");
        var duplicate = await repository.AddClipboardFileAsync(candidate, fingerprint, "{\"batchItemCount\":1}");

        Assert.AreNotEqual(space.Id, clipboard.Id);
        Assert.AreEqual(ItemSource.Space, space.Source);
        Assert.AreEqual(ItemSource.Clipboard, clipboard.Source);
        Assert.AreNotEqual(clipboard.Id, duplicate.Id);
        Assert.AreEqual(1, duplicate.Revision);
        Assert.AreEqual(1, await repository.CountAsync(ItemSource.Space));
        Assert.AreEqual(2, await repository.CountAsync(ItemSource.Clipboard));
        await repository.RemoveAsync(clipboard.Id);
        Assert.IsTrue(File.Exists(sourcePath));
        Assert.IsNotNull(await repository.GetAsync(space.Id));
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
