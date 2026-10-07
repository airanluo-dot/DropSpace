using System.Text.Json;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class IslandAppearanceSettingsTests
{
    [TestMethod]
    public void OlderSettingsKeepAppearanceAndInvalidChoicesRecoverLocally()
    {
        using var legacy = JsonDocument.Parse("{\"Theme\":2,\"IslandAppearance\":{\"CompactScale\":1.5}}");
        var older = IslandAppearanceMigration.Apply(legacy.RootElement.Deserialize<AppSettings>()!, legacy.RootElement).Validate();
        Assert.AreEqual(ThemePreference.Dark, older.IslandAppearance.Theme);
        Assert.AreEqual(IslandContentPriority.Music, older.IslandContentPriority);
        Assert.AreEqual(ThemePreference.Dark, older.Theme);
        Assert.AreEqual(1.5, older.IslandAppearance.CompactScale);
        using var previousDefault = JsonDocument.Parse("{}");
        Assert.AreEqual(ThemePreference.System, IslandAppearanceMigration.Apply(new AppSettings(), previousDefault.RootElement).IslandAppearance.Theme);

        var recovered = (older with
        {
            IslandAppearance = older.IslandAppearance with { Theme = (ThemePreference)99 },
            IslandContentPriority = (IslandContentPriority)99,
        }).Validate();
        Assert.AreEqual(ThemePreference.System, recovered.IslandAppearance.Theme);
        Assert.AreEqual(IslandContentPriority.Music, recovered.IslandContentPriority);
        Assert.AreEqual(ThemePreference.Dark, recovered.Theme);
        Assert.AreEqual(1.5, recovered.IslandAppearance.CompactScale);
    }

    [TestMethod]
    public void OverlappingFormsPreserveIndependentAppearanceAndPriorityThroughSerialization()
    {
        var baseline = new AppSettings();
        var islandEdit = baseline with
        {
            IslandAppearance = baseline.IslandAppearance with { Theme = ThemePreference.Light },
            IslandContentPriority = IslandContentPriority.TemporarySpace,
        };
        var latest = baseline with
        {
            Theme = ThemePreference.Dark,
            IslandAppearance = baseline.IslandAppearance with { CompactScale = 1.5 },
            Lyrics = baseline.Lyrics with { GlowMode = LyricsGlowMode.Music },
            IslandActivity = baseline.IslandActivity with { ShowArtwork = false },
        };
        var merged = SettingsChangePolicy.Merge(baseline, islandEdit, latest).Validate();
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(merged))!.Validate();
        Assert.AreEqual(ThemePreference.Light, restored.IslandAppearance.Theme);
        Assert.AreEqual(IslandContentPriority.TemporarySpace, restored.IslandContentPriority);
        Assert.AreEqual(ThemePreference.Dark, restored.Theme);
        Assert.AreEqual(1.5, restored.IslandAppearance.CompactScale);
        Assert.AreEqual(LyricsGlowMode.Music, restored.Lyrics.GlowMode);
        Assert.IsFalse(restored.IslandActivity.ShowArtwork);

        var staleMainEdit = baseline with { Theme = ThemePreference.Light };
        var subsequent = SettingsChangePolicy.Merge(baseline, staleMainEdit, restored).Validate();
        Assert.AreEqual(ThemePreference.Light, subsequent.Theme);
        Assert.AreEqual(ThemePreference.Light, subsequent.IslandAppearance.Theme);
        Assert.AreEqual(IslandContentPriority.TemporarySpace, subsequent.IslandContentPriority);
        Assert.AreEqual(1.5, subsequent.IslandAppearance.CompactScale);
    }
}
