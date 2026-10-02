using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Settings;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class GlowSettingsPersistenceTests
{
    [TestMethod]
    public async Task LegacyFileDefaultsToSurroundAndBothModesSurviveSeparateStoreReloads()
    {
        var paths = new AppStoragePaths(Path.Combine(Path.GetTempPath(), "DropSpace-glow-settings", Guid.NewGuid().ToString("N")));
        paths.EnsureCreated();
        try
        {
            await File.WriteAllTextAsync(paths.Settings, "{\"Lyrics\":{\"GlowMode\":2,\"AiTranslationEnabled\":false,\"Provider\":1}}");
            var settings = await new JsonSettingsService(paths).LoadAsync();
            Assert.IsFalse(settings.Lyrics.SimplifiedGlow);
            foreach (var simplified in new[] { true, false, true })
            {
                var previous = settings;
                settings = settings with { Lyrics = settings.Lyrics with { SimplifiedGlow = simplified } };
                await new JsonSettingsService(paths).SaveAsync(settings);
                var restored = await new JsonSettingsService(paths).LoadAsync();
                Assert.AreEqual(simplified, restored.Lyrics.SimplifiedGlow);
                Assert.AreEqual(LyricsGlowMode.Music, restored.Lyrics.GlowMode);
                Assert.IsFalse(restored.Lyrics.AiTranslationEnabled);
                Assert.AreEqual(LyricsProviderKind.QqMusic, restored.Lyrics.Provider);
                Assert.IsFalse(LyricsReloadPolicy.RequiresReload(previous, restored));
                Assert.AreEqual(settings.Lyrics, restored.Lyrics);
            }
        }
        finally { Directory.Delete(paths.Root, true); }
    }
}
