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
public sealed class LyricsSelectionModelRoleRegressionTests
{
    [TestMethod]
    public async Task SelectionUsesIndependentRoleAndRoleChangeRetiresReturnedPublication()
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-selection-role-" + Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppStoragePaths(root);
            using var models = new AiModelPackageService(Path.Combine(root, "models"));
            using var backend = new SelectionBackend();
            using var service = new AiLyricsService(paths, new LyricsCache(paths.Lyrics),
                NullLogger<AiLyricsService>.Instance, models, packageResolver: null, backend: backend);
            var query = new LyricsQuery("Song", "Artist", "Album", TimeSpan.FromSeconds(30), "track");
            var original = new LyricsDocument([new(TimeSpan.Zero, TimeSpan.FromSeconds(4), "Original", null, [])],
                LyricsProviderKind.NetEase, new("Song", "Artist", "Album", 30, 12, "original"));
            var source = new LyricsQueryResult(original, LyricsQueryStatus.Found)
            {
                SelectionCandidates = new([LyricsCandidateRules.Describe("c0", original, "zh-CN")],
                    Stopwatch.GetTimestamp() + 3 * Stopwatch.Frequency),
            };
            var settings = new LyricsSettings
            {
                SelectionMode = LyricsSelectionMode.AiRanked,
                AiModelId = AiLyricsModelCatalog.ExperimentalLargePlain.Id,
                AiSelectionModelId = AiLyricsSelectionModelCatalog.Default.Id,
            };
            var returned = await service.SelectCandidateAsync(query, source, settings, "zh-CN", default);
            Assert.AreEqual(LyricsSelectionOutcome.Unavailable, returned.Outcome);
            Assert.AreEqual(AiLyricsSelectionModelCatalog.Default.Sha256, backend.SelectedModelHash);
            Assert.IsTrue(returned.IsCurrent);
            // Invalidate synchronously before queued configuration or dispatcher work executes.
            service.QueueSelectionPreparation(settings with
                { AiSelectionModelId = AiLyricsModelCatalog.ExperimentalLargePlain.Id });
            Assert.IsFalse(returned.IsCurrent);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class SelectionBackend : IAiLyricsBackend, ILyricsSelectionRuntime
    {
        public string Id => "selection-model-role-fixture";
        public string? SelectedModelHash { get; private set; }
        public bool IsSelectionProfileQualified(string modelHash) => true;
        public bool CanPrepareSelection => false;
        public bool IsSelectionWarm(string modelHash) => false;
        public Task<bool> PrepareSelectionAsync(string path, string modelHash, CancellationToken token) => Task.FromResult(false);
        public Task<string?> TryRunSelectionAsync(string modelHash, string prompt, CancellationToken token)
        {
            SelectedModelHash = modelHash;
            return Task.FromResult<string?>(null);
        }
        public Task<LyricsTranslationResult> TranslateAsync(AiLyricsResolvedPackage package, LyricsQuery query,
            LyricsDocument source, string targetLanguage, CancellationToken token) => throw new InvalidOperationException();
        public Task DrainCleanupAsync(CancellationToken token) => Task.CompletedTask;
        public void Dispose() { }
    }
}
