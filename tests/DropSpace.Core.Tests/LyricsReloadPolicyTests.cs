using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class LyricsReloadPolicyTests
{
    [TestMethod]
    public void VisualAdjustmentsDoNotClearOrRefetchLyrics()
    {
        var before = new AppSettings();
        var after = before with { Lyrics = before.Lyrics with { FontSize = 22, GlowMode = LyricsGlowMode.Music, DelayMilliseconds = 500, SecondaryLyrics = true } };
        Assert.IsFalse(LyricsReloadPolicy.RequiresReload(before, after));
    }

    [TestMethod]
    public void LanguageModelAndProviderChangesRetireOldTranslations()
    {
        var before = new AppSettings();
        Assert.IsTrue(LyricsReloadPolicy.RequiresReload(before, before with { Language = AppLanguagePreference.English }));
        Assert.IsTrue(LyricsReloadPolicy.RequiresReload(before, before with { Lyrics = before.Lyrics with { AiModelId = "lightweight" } }));
        Assert.IsTrue(LyricsReloadPolicy.RequiresReload(before, before with { Lyrics = before.Lyrics with { AiLyricsGpuAccelerationEnabled = false } }));
        Assert.IsTrue(LyricsReloadPolicy.RequiresReload(before, before with { Lyrics = before.Lyrics with { AiTranslationEnabled = true } }));
        Assert.IsTrue(LyricsReloadPolicy.RequiresReload(before, before with { Lyrics = before.Lyrics with { Provider = LyricsProviderKind.QqMusic } }));
    }
}
