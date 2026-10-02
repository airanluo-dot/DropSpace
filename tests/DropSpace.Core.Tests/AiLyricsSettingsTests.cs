using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class AiLyricsSettingsTests
{
    [TestMethod]
    [DataRow(12.0)]
    [DataRow(17.375)]
    [DataRow(28.0)]
    public void OneDecimalFontSizePreservesOriginalTranslationRatio(double size)
    {
        var settings = new AppSettings { Lyrics = new() { FontSize = size } }.Validate();
        Assert.AreEqual(size, settings.Lyrics.OriginalFontSize);
        Assert.AreEqual(size * 0.875, settings.Lyrics.TranslationFontSize);
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(settings))!.Validate();
        Assert.AreEqual(size, restored.Lyrics.FontSize);
    }

    [TestMethod]
    public void EveryCatalogModelSurvivesValidationAndPersistence()
    {
        foreach (var model in AiLyricsModelCatalog.All)
        {
            var original = new AppSettings { Lyrics = new() { AiModelId = model.Id, AiTranslationEnabled = true } };
            var restored = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(original))!.Validate();
            Assert.AreEqual(model.Id, restored.Lyrics.AiModelId);
            Assert.IsTrue(restored.Lyrics.AiTranslationEnabled);
        }
        Assert.AreEqual(AiLyricsModelCatalog.ExperimentalPlain.Id,
            new AppSettings { Lyrics = new() { AiModelId = "lightweight" } }.Validate().Lyrics.AiModelId);
    }

    [TestMethod]
    public void OnlyPlainQ8BetaIsSelectableAndLegacyProfilesMigrateWithoutEnablingAi()
    {
        Assert.HasCount(1, AiLyricsModelCatalog.All);
        Assert.AreEqual(AiLyricsModelCatalog.ExperimentalPlain, AiLyricsModelCatalog.All[0]);
        Assert.AreEqual(1_908_528_192, AiLyricsModelCatalog.ExperimentalPlain.Bytes);
        foreach (var old in AiLyricsModelCatalog.Legacy)
        {
            Assert.IsNotNull(AiLyricsModelCatalog.Find(old.Id), "Legacy bytes must remain removable.");
            Assert.IsNull(AiLyricsModelCatalog.FindSelectable(old.Id));
            var migrated = new AppSettings { Lyrics = new() { AiModelId = old.Id, AiTranslationEnabled = false } }.Validate();
            Assert.AreEqual(AiLyricsModelCatalog.ExperimentalPlain.Id, migrated.Lyrics.AiModelId);
            Assert.IsFalse(migrated.Lyrics.AiTranslationEnabled);
        }
        Assert.AreEqual(AiLyricsModelCatalog.ExperimentalPlain.Id, new LyricsSettings().AiModelId);
    }

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
                FontSize = double.NaN,
            },
        });
        Assert.IsTrue(normalized.ClipboardPaused);
        Assert.IsTrue(normalized.Lyrics.AiTranslationEnabled);
        Assert.AreEqual(AiLyricsModelCatalog.ExperimentalPlain.Id, normalized.Lyrics.AiModelId);
        Assert.AreEqual(LyricsGlowMode.Off, normalized.Lyrics.GlowMode);
        Assert.AreEqual(16d, normalized.Lyrics.OriginalFontSize);
        Assert.AreEqual(14d, normalized.Lyrics.TranslationFontSize);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void GpuPreferencePersistsWithoutEnablingAi(bool accelerated)
    {
        var original = new AppSettings { Lyrics = new() { AiLyricsGpuAccelerationEnabled = accelerated } };
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(original))!.Validate();
        Assert.AreEqual(accelerated, restored.Lyrics.AiLyricsGpuAccelerationEnabled);
        Assert.IsFalse(restored.Lyrics.AiTranslationEnabled);
        var old = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"Lyrics\":{\"AiTranslationEnabled\":false}}")!.Validate();
        Assert.IsTrue(old.Lyrics.AiLyricsGpuAccelerationEnabled);
        Assert.IsFalse(old.Lyrics.AiTranslationEnabled);
    }

    [TestMethod]
    public void NormalizationPreservesManualMusicModeWithAiDisabled()
    {
        var settings = new AppSettings { Lyrics = new() { GlowMode = LyricsGlowMode.Music } };
        Assert.AreEqual(LyricsGlowMode.Music, NativeIslandSettingsPolicy.Normalize(settings).Lyrics.GlowMode);
    }
}
