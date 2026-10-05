using System.Diagnostics;
using DropSpace.App.Services.Media;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;
using DropSpace.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class LyricsSelectionPublicationRegressionTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReturnedDecisionRetiresBeforeDispatcherCommitWhenCacheOrModelIsRemoved(bool deleteModel)
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-selection-publication-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppStoragePaths(root);
            using var models = new AiModelPackageService(Path.Combine(root, "models"));
            using var backend = new SelectionBackend();
            using var service = new AiLyricsService(paths, new LyricsCache(paths.Lyrics),
                NullLogger<AiLyricsService>.Instance, models, packageResolver: null, backend: backend);
            var query = new LyricsQuery("Song", "Artist", "Album", TimeSpan.FromSeconds(30), "track");
            LyricsDocument Document(LyricsProviderKind provider, string id) => new(
                [new(TimeSpan.Zero, TimeSpan.FromSeconds(4), "Original lyric", null, [])], provider,
                new("Song", "Artist", "Album", 30, 12, id));
            var original = Document(LyricsProviderKind.NetEase, "original");
            // The fixture must satisfy the shared source priority before testing
            // retirement; a worse untranslated source is correctly rejected earlier.
            var alternative = Document(LyricsProviderKind.QqMusic, "alternative") with
            { Lines = [original.Lines[0] with { Secondary = "夜里满布繁星", TranslationOrigin = LyricsTranslationOrigin.Provider,
                TranslationLanguage = "zh-CN", TranslationLanguageIsExplicit = true }] };
            var source = new LyricsQueryResult(original, LyricsQueryStatus.Found)
            {
                SelectionCandidates = new([LyricsCandidateRules.Describe("c0", original, "zh-CN"),
                    LyricsCandidateRules.Describe("c1", alternative, "zh-CN")], Stopwatch.GetTimestamp() + 3 * Stopwatch.Frequency),
            };
            var settings = new LyricsSettings { Enabled = true, SelectionMode = LyricsSelectionMode.AiRanked,
                AiModelId = AiLyricsModelCatalog.ExperimentalPlain.Id };
            var returned = await service.SelectCandidateAsync(query, source, settings, "zh-CN", default);
            Assert.AreEqual(LyricsSelectionOutcome.Selected, returned.Outcome);
            Assert.IsTrue(returned.IsCurrent);
            // This is the queue interval after inference returned, without changing the song/settings.
            if (deleteModel) await service.DeleteModelAsync(settings.AiModelId, default);
            else await service.ClearCacheAsync(default);
            Assert.IsFalse(returned.IsCurrent, "A dispatcher must reject the already returned decision after maintenance.");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class SelectionBackend : IAiLyricsBackend, ILyricsSelectionRuntime
    {
        public string Id => "selection-publication-fixture";
        public bool IsSelectionProfileQualified(string modelHash) => true;
        public bool CanPrepareSelection => false;
        public bool IsSelectionWarm(string modelHash) => true;
        public Task<bool> PrepareSelectionAsync(string path, string modelHash, CancellationToken token) => Task.FromResult(false);
        public Task<string?> TryRunSelectionAsync(string modelHash, string prompt, CancellationToken token) => Task.FromResult<string?>("{\"id\":\"c1\"}");
        public Task<LyricsTranslationResult> TranslateAsync(AiLyricsResolvedPackage package, LyricsQuery query,
            LyricsDocument source, string targetLanguage, CancellationToken token) => throw new InvalidOperationException("Translation is unrelated to this regression.");
        public Task DrainCleanupAsync(CancellationToken token) => Task.CompletedTask;
        public void Dispose() { }
    }
}
