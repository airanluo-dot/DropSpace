using System.Reflection;
using DropSpace.App.Services.Media;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;
using DropSpace.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class LyricsSelectionQualificationRegressionTests
{
    [TestMethod]
    public async Task UnqualifiedProfilesPreserveNativeCacheAndNeverPrepareInferOrDrainTranslation()
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-selection-qualification-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppStoragePaths(root);
            using var models = new AiModelPackageService(Path.Combine(root, "models"));
            using var backend = new UnqualifiedBackend();
            using var service = new AiLyricsService(paths, new LyricsCache(paths.Lyrics),
                NullLogger<AiLyricsService>.Instance, models, packageResolver: new MissingPackageResolver(), backend: backend);
            var query = new LyricsQuery("Song", "Artist", "Album", TimeSpan.FromSeconds(30), "qualification:track")
                { PreferredTranslationLanguage = "zh" };
            var original = new LyricsDocument([new(TimeSpan.Zero, TimeSpan.FromSeconds(4), "The stars shine tonight", "今夜繁星闪耀", [])
                { TranslationOrigin = LyricsTranslationOrigin.Provider, TranslationLanguage = "zh", TranslationLanguageIsExplicit = true }],
                LyricsProviderKind.NetEase, new("Song", "Artist", "Album", 30, 12, "native"))
                { ProviderDataRevision = 2 };
            var provider = new Provider(original);
            var sourceService = new LyricsService(new([provider]));
            var settings = new LyricsSettings { SearchRemainingProviders = false, AiTranslationEnabled = true };
            var untranslated = original with { Lines = [original.Lines[0] with
                { Secondary = null, TranslationOrigin = LyricsTranslationOrigin.None, TranslationLanguage = null, TranslationLanguageIsExplicit = null }] };
            await service.TranslateForPublicationAsync(query, untranslated, settings, "zh", default);
            var configuredDrains = backend.Drains;
            Assert.IsTrue(configuredDrains > 0, "Exercise an already configured translation owner, not only an uninitialized service.");
            foreach (var mode in Enum.GetValues<LyricsSelectionMode>())
            {
                var requested = settings with { SelectionMode = mode };
                var effective = service.SourceSelectionSettings(requested);
                Assert.IsFalse(service.CanSelectCandidates(requested));
                Assert.AreEqual(LyricsSelectionMode.Rules, effective.SelectionMode);
                Assert.AreEqual(mode, requested.SelectionMode, "Persisted user preference must not be rewritten.");
                Assert.IsTrue(effective.AiTranslationEnabled, "Translation remains independently enabled.");
                var source = await sourceService.QueryDetailedAsync(query, effective, default);
                Assert.AreEqual("native", source.Document.Match!.CandidateId);
                Assert.AreEqual("今夜繁星闪耀", source.Document.Lines[0].Secondary);
                service.QueueSelectionPreparation(requested);
                service.QueueSelectionPreparation(requested);
                var preparation = (Task)typeof(AiLyricsService).GetField("_selectionPreparation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
                await preparation.WaitAsync(TimeSpan.FromSeconds(3));
                if (mode != LyricsSelectionMode.Rules)
                {
                    var result = await service.SelectCandidateAsync(query, source, requested, "zh", default);
                    Assert.AreEqual(LyricsSelectionOutcome.Unqualified, result.Outcome);
                    Assert.AreSame(source.Document, result.Document);
                }
            }
            Assert.AreEqual(1, provider.Calls, "Switching unavailable AI modes must reuse the trusted native source cache.");
            Assert.IsFalse(provider.CollectedWeakCandidates);
            Assert.AreEqual(0, backend.Preparations);
            Assert.AreEqual(0, backend.Inferences);
            Assert.AreEqual(configuredDrains, backend.Drains, "Unsupported selection must not drain the already configured translation owner.");
            foreach (var model in AiLyricsModelCatalog.All)
                Assert.IsNull(AiLyricsSelectionModelCatalog.FindQualifiedByHash(model.Sha256));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class Provider(LyricsDocument document) : ILyricsProvider
    {
        public LyricsProviderKind Kind => LyricsProviderKind.NetEase;
        public int Calls { get; private set; }
        public bool CollectedWeakCandidates { get; private set; }
        public Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken token)
        { Calls++; CollectedWeakCandidates |= query.CollectSelectionCandidates; return Task.FromResult(document); }
    }

    private sealed class MissingPackageResolver : IAiLyricsPackageResolver
    {
        public Task<AiLyricsResolvedPackage?> ResolveAsync(string id, LyricsQuery query, LyricsDocument source,
            string target, CancellationToken token) => Task.FromResult<AiLyricsResolvedPackage?>(null);
    }

    private sealed class UnqualifiedBackend : IAiLyricsBackend, ILyricsSelectionRuntime
    {
        public string Id => "unqualified-selection-fixture";
        public int Preparations { get; private set; }
        public int Inferences { get; private set; }
        public int Drains { get; private set; }
        public bool CanPrepareSelection => true;
        public bool IsSelectionWarm(string modelHash) => true;
        public Task<bool> PrepareSelectionAsync(string path, string modelHash, CancellationToken token)
        { Preparations++; return Task.FromResult(true); }
        public Task<string?> TryRunSelectionAsync(string modelHash, string prompt, CancellationToken token)
        { Inferences++; return Task.FromResult<string?>("{\"id\":\"c0\"}"); }
        public Task<LyricsTranslationResult> TranslateAsync(AiLyricsResolvedPackage package, LyricsQuery query,
            LyricsDocument source, string targetLanguage, CancellationToken token) => throw new InvalidOperationException();
        public Task DrainCleanupAsync(CancellationToken token) { Drains++; return Task.CompletedTask; }
        public void Dispose() { }
    }
}
