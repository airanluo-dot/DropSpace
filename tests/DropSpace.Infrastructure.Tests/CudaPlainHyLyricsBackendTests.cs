using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class CudaPlainHyLyricsBackendTests
{
    [TestMethod]
    public async Task ExistingProviderTranslationDoesNotOpenAnyAiResourceEvenThroughInterface()
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-cuda-bypass-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var cpu = new AiLyricsRuntimePackage(_ => throw new AssertFailedException("Provider translations bypass runtime resolution."), root);
        var cuda = new CudaLyricsRuntimePackage(_ => throw new AssertFailedException("Provider translations bypass CUDA resolution."), root);
        var runner = new PersistentPlainLyricsRunner(cpu, cuda, new());
        using IAiLyricsBackend backend = new CudaPlainHyLyricsBackend(new(new(root)), runner, cpu, cuda, root);
        var line = new LyricsLine(TimeSpan.Zero, TimeSpan.FromSeconds(4), "The morning light", "清晨的光", [])
        { TranslationLanguage = "zh-Hans", TranslationOrigin = LyricsTranslationOrigin.Provider, TranslationLanguageIsExplicit = true };
        var source = new LyricsDocument([line], LyricsProviderKind.NetEase);
        var query = new LyricsQuery("Test", "DropSpace", "", TimeSpan.FromSeconds(4));
        try
        {
            Assert.IsNull(await backend.TryGetCachedResultAsync(AiLyricsModelCatalog.ExperimentalPlain.Id, query, source, "zh-Hans", default));
            var untrustedPackage = new AiLyricsResolvedPackage("untrusted", "", "", "", null);
            var progress = new LyricsTranslationProgressContext(() => TimeSpan.Zero, () => true,
                (_, _) => throw new AssertFailedException("Provider translations never produce AI progress."));
            var result = await backend.TranslateAsync(untrustedPackage, query, source, "zh-Hans", default, progress);
            Assert.AreEqual(LyricsTranslationOutcome.NoUsefulTranslation, result.Outcome);
            Assert.AreEqual("清晨的光", result.Document.Lines[0].Secondary);
        }
        finally
        {
            await backend.DrainCleanupAsync(default);
            Directory.Delete(root, true);
        }
    }
}
