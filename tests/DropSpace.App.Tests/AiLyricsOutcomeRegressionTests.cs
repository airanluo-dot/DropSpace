using DropSpace.App.Services.Media;
using DropSpace.Core.Lyrics;
using DropSpace.Infrastructure.Lyrics;
using DropSpace.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class AiLyricsOutcomeRegressionTests
{
    [TestMethod]
    public void NeutralOutcomeNeitherCompletesNorIncrementsOrResetsFailureCircuit()
    {
        using var service = Create();
        service.CompleteTranslation(0, 0, LyricsTranslationOutcome.Failed);
        service.CompleteTranslation(0, 0, LyricsTranslationOutcome.Failed);
        for (var index = 0; index < 3; index++)
        {
            service.CompleteTranslation(0, 0, LyricsTranslationOutcome.NoUsefulTranslation);
            Assert.AreEqual(AiLyricsTranslationState.Ready, service.TranslationState);
            Assert.IsFalse(service.TranslationPaused);
        }
        service.CompleteTranslation(0, 0, LyricsTranslationOutcome.Failed);
        Assert.IsTrue(service.TranslationPaused, "Neutral results must not reset the two earlier failures.");
    }

    [TestMethod]
    public void UsefulOutcomeCompletesAndResetsFailureCircuit()
    {
        using var service = Create();
        service.CompleteTranslation(0, 0, LyricsTranslationOutcome.Failed);
        service.CompleteTranslation(0, 0, LyricsTranslationOutcome.Failed);
        service.CompleteTranslation(0, 0, LyricsTranslationOutcome.Translated);
        Assert.AreEqual(AiLyricsTranslationState.Completed, service.TranslationState);
        service.CompleteTranslation(0, 0, LyricsTranslationOutcome.Failed);
        Assert.IsFalse(service.TranslationPaused);
    }

    [TestMethod]
    public async Task AllCopyCacheRemainsReadyWithoutInstalledModelOrRepeatedInference()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppStoragePaths(root); var cache = new LyricsCache(paths.Lyrics);
            using var service = new AiLyricsService(paths, cache, NullLogger<AiLyricsService>.Instance);
            var query = new LyricsQuery("Song", "Artist", "", TimeSpan.FromSeconds(3));
            var source = new LyricsDocument([new(TimeSpan.Zero, TimeSpan.FromSeconds(3), "Hello", null, [])], DropSpace.Core.Models.LyricsProviderKind.LocalLrc);
            await new AiLyricsCache(cache).WriteAsync(LyricsTranslationPrompt.CacheKey(query, source, "en", AiLyricsModelCatalog.Standard.Sha256), "[{\"id\":0,\"text\":\"Hello\"}]", default);
            for (var index = 0; index < 3; index++)
            {
                var result = await service.TranslateCoreAsync(query, source, new() { Enabled = true, AiTranslationEnabled = true }, "en", default);
                Assert.AreSame(source, result);
                Assert.AreEqual(AiLyricsTranslationState.Ready, service.TranslationState);
                Assert.IsFalse(service.TranslationPaused);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static AiLyricsService Create()
    {
        var paths = new AppStoragePaths(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        return new(paths, new LyricsCache(paths.Lyrics), NullLogger<AiLyricsService>.Instance);
    }
}
