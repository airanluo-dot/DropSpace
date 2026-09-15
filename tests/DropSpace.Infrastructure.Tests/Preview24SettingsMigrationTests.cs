using System.Text.Json;
using DropSpace.Core.Models;
using DropSpace.Core.Widgets;
using DropSpace.Infrastructure.Settings;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class Preview24SettingsMigrationTests
{
    private readonly AppStoragePaths _paths = new(Path.Combine(Path.GetTempPath(), "DropSpace-preview24-tests", Guid.NewGuid().ToString("N")));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true);
    }

    [TestMethod]
    [DataRow(11)]
    [DataRow(12)]
    [DataRow(13)]
    public async Task UpgradesWithoutQuarantiningUserPreferences(int version)
    {
        _paths.EnsureCreated();
        var settings = new AppSettings
        {
            Version = version, ClipboardPaused = true, Theme = ThemePreference.Light,
            Language = AppLanguagePreference.SimplifiedChinese, RetentionDays = 77,
            Lyrics = new() { Provider = LyricsProviderKind.QqMusic, DelayMilliseconds = 750 },
            IslandActivity = new() { AllowedMediaSourceAppIds = ["player.exe"] },
            Widgets = new() { Layout = new([new(NativeWidgetId.Calendar, 4, 1, 2, 2)], new(null, NativeWidgetId.Clock, null)) },
        };
        await File.WriteAllTextAsync(_paths.Settings, JsonSerializer.Serialize(settings));
        var store = new JsonSettingsService(_paths);
        var result = await store.LoadAsync();
        Assert.AreEqual(14, result.Version);
        Assert.IsTrue(result.ClipboardPaused);
        Assert.AreEqual(ThemePreference.Light, result.Theme);
        Assert.AreEqual(77, result.RetentionDays);
        Assert.AreEqual(LyricsProviderKind.QqMusic, result.Lyrics.Provider);
        Assert.AreEqual(750, result.Lyrics.DelayMilliseconds);
        CollectionAssert.AreEqual(new[] { "player.exe" }, result.IslandActivity.AllowedMediaSourceAppIds);
        Assert.AreEqual(settings.Widgets.Layout, result.Widgets.Layout);
        var reloaded = await new JsonSettingsService(_paths).LoadAsync();
        Assert.AreEqual(14, reloaded.Version);
        Assert.AreEqual(result.Widgets.Layout, reloaded.Widgets.Layout);
        Assert.IsTrue(File.Exists(_paths.Settings));
        Assert.AreEqual(0, Directory.Exists(_paths.Quarantine) ? Directory.GetFiles(_paths.Quarantine).Length : 0);
    }

    [TestMethod]
    public async Task LegacyElevenGetsFreshNativeDefaults()
    {
        _paths.EnsureCreated();
        await File.WriteAllTextAsync(_paths.Settings, """{"Version":11,"ClipboardPaused":true}""");
        var result = await new JsonSettingsService(_paths).LoadAsync();
        Assert.IsTrue(result.Lyrics.Enabled);
        Assert.AreEqual(LyricsProviderKind.NetEase, result.Lyrics.Provider);
        Assert.AreEqual(3_000, result.IslandAppearance.HideDelayMilliseconds);
        Assert.IsFalse(result.SystemActivities.ShowVolumeChanges);
        Assert.IsTrue(result.ClipboardPaused);
    }

    [TestMethod]
    public async Task CustomEightByFourLayoutAndEmptyExplicitPlayerSelectionSurviveRestart()
    {
        var store = new JsonSettingsService(_paths);
        var settings = new AppSettings
        {
            IslandActivity = new() { UseMediaSourceAllowList = true, AllowedMediaSourceAppIds = [] },
            Widgets = new() { Layout = new([new(NativeWidgetId.Battery, 7, 3, 1, 1)], new(null, NativeWidgetId.Clock, null)) },
        };
        await store.SaveAsync(settings);
        var reloaded = await new JsonSettingsService(_paths).LoadAsync();
        Assert.IsTrue(reloaded.IslandActivity.UseMediaSourceAllowList);
        Assert.AreEqual(0, reloaded.IslandActivity.AllowedMediaSourceAppIds.Length);
        Assert.AreEqual(settings.Widgets.Layout, reloaded.Widgets.Layout);
    }

    [TestMethod]
    public async Task BadLegacyNativeUiFieldsDoNotQuarantineWholeSettings()
    {
        _paths.EnsureCreated();
        await File.WriteAllTextAsync(_paths.Settings, """{"Version":13,"ClipboardPaused":true,"Lyrics":{"Provider":999,"DelayMilliseconds":2147483647},"IslandAppearance":{"LastExpandedPage":999,"HiddenWidth":500,"HideDelayMilliseconds":0},"Widgets":{"Layout":null}}""");
        var result = await new JsonSettingsService(_paths).LoadAsync();
        Assert.IsTrue(result.ClipboardPaused);
        Assert.AreEqual(14, result.Version);
        Assert.AreEqual(LyricsProviderKind.NetEase, result.Lyrics.Provider);
        Assert.AreEqual(500, result.IslandAppearance.HideDelayMilliseconds);
        Assert.IsTrue(File.Exists(_paths.Settings));
        Assert.AreEqual(0, Directory.Exists(_paths.Quarantine) ? Directory.GetFiles(_paths.Quarantine).Length : 0);
    }
}
