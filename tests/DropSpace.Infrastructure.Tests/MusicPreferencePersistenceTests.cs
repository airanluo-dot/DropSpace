using DropSpace.Core.Models;
using DropSpace.Core.Policies;
using DropSpace.Infrastructure.Settings;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class MusicPreferencePersistenceTests
{
    [TestMethod]
    public async Task TranslationChoiceAndMusicWindowPreferenceSurviveMigrationAndReload()
    {
        var paths = new AppStoragePaths(Path.Combine(Path.GetTempPath(), "DropSpace-music-preferences", Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        try
        {
            await File.WriteAllTextAsync(paths.Settings, "{\"Language\":3}");
            var missing = await new JsonSettingsService(paths).LoadAsync();
            Assert.AreEqual(LyricsTranslationTarget.SimplifiedChinese, missing.LyricsTranslationTarget);
            Assert.IsFalse(missing.LyricsTranslationTargetExplicit);
            await File.WriteAllTextAsync(paths.Settings, "{\"Language\":2,\"LyricsTranslationTarget\":0}");
            var legacy = await new JsonSettingsService(paths).LoadAsync();
            Assert.IsTrue(legacy.LyricsTranslationTargetExplicit);
            Assert.AreEqual(LyricsTranslationTarget.English, LyricsTranslationTargetPolicy.ApplyDefault(legacy, ["zh-CN"]).LyricsTranslationTarget);
            var automatic = missing with { IslandAppearance = missing.IslandAppearance with { HideWhileMusicWindowVisible = true } };
            await new JsonSettingsService(paths).SaveAsync(automatic);
            var reloaded = await new JsonSettingsService(paths).LoadAsync();
            Assert.IsTrue(reloaded.IslandAppearance.HideWhileMusicWindowVisible);
            Assert.IsFalse(reloaded.LyricsTranslationTargetExplicit);
            Assert.AreEqual(LyricsTranslationTarget.SimplifiedChinese, reloaded.LyricsTranslationTarget);
            await new JsonSettingsService(paths).SaveAsync(reloaded with { LyricsTranslationTargetExplicit = true, LyricsTranslationTarget = LyricsTranslationTarget.English });
            var chosen = await new JsonSettingsService(paths).LoadAsync();
            Assert.IsTrue(chosen.LyricsTranslationTargetExplicit);
            Assert.AreEqual(LyricsTranslationTarget.English, chosen.LyricsTranslationTarget);
        }
        finally { Directory.Delete(paths.Root, true); }
    }
}
