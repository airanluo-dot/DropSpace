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
        Assert.HasCount(2, AiLyricsModelCatalog.All);
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
    public void Optional7BProfilePinsOfficialBytesAndDoesNotChangeDefaultOrInferenceContract()
    {
        var model = AiLyricsModelCatalog.ExperimentalLargePlain;
        Assert.AreEqual(7_981_928_896L, model.Bytes);
        Assert.AreEqual("58b3ad55dd6f6fa08c695cddc34fb5f8f708a844f78ae10508071914b0ed67c0", model.Sha256);
        Assert.AreEqual("https://huggingface.co/tencent/Hy-MT2-7B-GGUF/resolve/ab8472660ac61fac25f1af43fac2599d52a8a775/HY-MT2-7B-Q8_0.gguf", model.DownloadUri.AbsoluteUri);
        Assert.AreSame(model, AiLyricsModelCatalog.FindSelectableByHash(model.Sha256.ToUpperInvariant()));
        Assert.IsNull(AiLyricsModelCatalog.FindSelectableByHash(AiLyricsModelCatalog.Standard.Sha256));
        Assert.IsNull(AiLyricsModelCatalog.FindSelectableByHash(null));
        Assert.AreEqual(AiLyricsModelCatalog.ExperimentalPlain.Id, new LyricsSettings().AiModelId);
        var runtime = new string('a', 64);
        var oldIdentity = PlainHyLyricsProtocol.InferenceIdentity(runtime);
        Assert.AreEqual("f52dcf4ea70c653eaea13c67b51ad82ec5d80cd33b7554a684393111a638fec3", oldIdentity,
            "Adding a model must not change the frozen 1.8B inference identity.");
        Assert.AreEqual(oldIdentity, PlainHyLyricsProtocol.InferenceIdentity(runtime, AiLyricsModelCatalog.ExperimentalPlain.Sha256));
        Assert.AreNotEqual(oldIdentity, PlainHyLyricsProtocol.InferenceIdentity(runtime, model.Sha256));
        Assert.AreEqual(PlainHyLyricsProtocol.InferenceIdentity(runtime, model.Sha256),
            PlainHyLyricsProtocol.InferenceIdentity(runtime.ToUpperInvariant(), model.Sha256.ToUpperInvariant()));
        Assert.ThrowsExactly<ArgumentException>(() => PlainHyLyricsProtocol.InferenceIdentity(runtime, AiLyricsModelCatalog.Standard.Sha256));
    }

    [TestMethod]
    public void OldSettingsDefaultToNoAiOrGlow()
    {
        var settings = new AppSettings();
        Assert.IsFalse(settings.Lyrics.AiTranslationEnabled);
        Assert.AreEqual(LyricsGlowMode.Off, settings.Lyrics.GlowMode);
    }

    [TestMethod]
    public void AiLabelDefaultsOnAndPersistsBothValuesWithoutChangingTranslationPreferences()
    {
        var old = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"Lyrics\":{\"SecondaryLyrics\":true}}")!.Validate();
        Assert.IsTrue(old.Lyrics.ShowAiLyricsLabel);
        foreach (var visible in new[] { true, false })
        {
            var settings = old with { Lyrics = old.Lyrics with
            {
                ShowAiLyricsLabel = visible, AiTranslationEnabled = true, GlowMode = LyricsGlowMode.AiLyrics,
                Provider = LyricsProviderKind.QqMusic, BackupProvider = LyricsProviderKind.NetEase, FontSize = 17.375,
            } };
            var restored = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(settings))!.Validate();
            Assert.AreEqual(settings.Lyrics, restored.Lyrics);
            Assert.AreEqual(visible, restored.Lyrics.ShowAiLyricsLabel);
            Assert.IsFalse(LyricsReloadPolicy.RequiresReload(settings,
                settings with { Lyrics = settings.Lyrics with { ShowAiLyricsLabel = !visible } }));
        }
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
