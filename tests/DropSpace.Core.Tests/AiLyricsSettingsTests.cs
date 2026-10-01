using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class AiLyricsSettingsTests
{
    [TestMethod]
    public void OldSettingsDefaultToNoAiOrGlow()
    {
        var settings = new AppSettings();
        Assert.IsFalse(settings.Lyrics.AiTranslationEnabled);
        Assert.AreEqual(LyricsGlowMode.Off, settings.Lyrics.GlowMode);
    }

    [TestMethod]
    public void InvalidValuesNormalizeWithoutResettingOtherPreferences()
    {
        var normalized = NativeIslandSettingsPolicy.Normalize(new AppSettings
        {
            ClipboardPaused = true,
            Lyrics = new()
            {
                AiTranslationEnabled = true,
                AiModelId = "../../unsafe",
                GlowMode = (LyricsGlowMode)99,
                OriginalFontSize = double.NaN,
                TranslationFontSize = 900,
            },
        });
        Assert.IsTrue(normalized.ClipboardPaused);
        Assert.IsTrue(normalized.Lyrics.AiTranslationEnabled);
        Assert.AreEqual("hy-mt2-standard", normalized.Lyrics.AiModelId);
        Assert.AreEqual(LyricsGlowMode.Off, normalized.Lyrics.GlowMode);
        Assert.AreEqual(16d, normalized.Lyrics.OriginalFontSize);
        Assert.AreEqual(24d, normalized.Lyrics.TranslationFontSize);
    }

    [TestMethod]
    public void NormalizationPreservesManualMusicModeWithAiDisabled()
    {
        var settings = new AppSettings { Lyrics = new() { GlowMode = LyricsGlowMode.Music } };
        Assert.AreEqual(LyricsGlowMode.Music, NativeIslandSettingsPolicy.Normalize(settings).Lyrics.GlowMode);
    }
}
