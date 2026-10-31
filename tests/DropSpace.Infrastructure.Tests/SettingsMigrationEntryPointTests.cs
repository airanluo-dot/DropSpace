using System.Text.Json;
using DropSpace.Core.Models;
using DropSpace.Core.Updates;
using DropSpace.Infrastructure.Settings;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class SettingsMigrationEntryPointTests
{
    private readonly AppStoragePaths _paths = new(Path.Combine(Path.GetTempPath(), "DropSpace-settings-entry-tests", Guid.NewGuid().ToString("N")));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LegacyUpdateAndResetPreserveChannelWithoutPriorLoad(bool resetUi)
    {
        _paths.EnsureCreated();
        foreach (var explicitStable in new[] { false, true })
        {
            var legacy = explicitStable
                ? """{"Version":13,"ClipboardPaused":true,"RetentionDays":77,"UpdateChannel":"Stable"}"""
                : """{"Version":13,"ClipboardPaused":true,"RetentionDays":77}""";
            await File.WriteAllTextAsync(_paths.Settings, legacy);
            var store = new JsonSettingsService(_paths);
            var expected = explicitStable ? UpdateChannel.Stable : UpdateChannel.Beta;
            var updated = resetUi ? await store.ResetUiSettingsAsync() : await store.UpdateAsync(current =>
            {
                Assert.AreEqual(expected, current.UpdateChannel, "Mutations must receive migrated settings.");
                return current with { StartWithWindows = false };
            });

            Assert.AreEqual(expected, updated.UpdateChannel);
            Assert.AreEqual(AppSettings.CurrentVersion, updated.Version);
            Assert.IsTrue(updated.ClipboardPaused);
            Assert.AreEqual(77, updated.RetentionDays);
            var reloaded = await new JsonSettingsService(_paths).LoadAsync();
            Assert.AreEqual(expected, reloaded.UpdateChannel);
            Assert.IsTrue(reloaded.ClipboardPaused);
            Assert.AreEqual(77, reloaded.RetentionDays);
        }
    }

    [TestMethod]
    public async Task CurrentSettingsLoadDoesNotRewriteUnchangedFile()
    {
        var store = new JsonSettingsService(_paths);
        await store.SaveAsync(new AppSettings { ClipboardPaused = true });
        var timestamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(_paths.Settings, timestamp);
        var before = await File.ReadAllBytesAsync(_paths.Settings);

        Assert.IsTrue((await store.LoadAsync()).ClipboardPaused);
        Assert.IsTrue((await store.LoadAsync()).ClipboardPaused);

        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(_paths.Settings));
        Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(_paths.Settings));
    }

    [TestMethod]
    public async Task LoadPersistsNormalizedPreferencesOnce()
    {
        _paths.EnsureCreated();
        var settings = new AppSettings { ClipboardPaused = true, QuickPanelHotkey = "shift+win+space" };
        await File.WriteAllTextAsync(_paths.Settings, JsonSerializer.Serialize(settings));
        var store = new JsonSettingsService(_paths);

        Assert.AreEqual("Win+Shift+Space", (await store.LoadAsync()).QuickPanelHotkey);
        using var persisted = JsonDocument.Parse(await File.ReadAllBytesAsync(_paths.Settings));
        Assert.AreEqual("Win+Shift+Space", persisted.RootElement.GetProperty("QuickPanelHotkey").GetString());

        var timestamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(_paths.Settings, timestamp);
        Assert.IsTrue((await store.LoadAsync()).ClipboardPaused);
        Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(_paths.Settings));
    }

    [TestMethod]
    public async Task LoadStillPersistsLegacyChannelAndPrivacyMigrations()
    {
        _paths.EnsureCreated();
        await File.WriteAllTextAsync(_paths.Settings,
            """{"Version":13,"UpdateChannel":"Preview","ClipboardPaused":true,"StartWithWindows":false}""");
        var store = new JsonSettingsService(_paths);
        var migrated = await store.LoadAsync();
        Assert.AreEqual(UpdateChannel.Beta, migrated.UpdateChannel);
        Assert.IsTrue(migrated.PrivacyChoicesCompleted);
        using var persisted = JsonDocument.Parse(await File.ReadAllBytesAsync(_paths.Settings));
        Assert.AreEqual(AppSettings.CurrentVersion, persisted.RootElement.GetProperty("Version").GetInt32());
        Assert.AreEqual("Beta", persisted.RootElement.GetProperty("UpdateChannel").GetString());
        Assert.IsTrue(persisted.RootElement.GetProperty("PrivacyChoicesCompleted").GetBoolean());
    }
}
