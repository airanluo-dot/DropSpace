using DropSpace.App.Services.Media;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;
using DropSpace.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    public async Task UnavailableBetaDoesNotConsumeLegacyCopyCacheOrPause()
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

    [TestMethod]
    public async Task InjectedBackendUsesVerifiedResolutionAndPublishesOnlyItsCompletedDocument()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppStoragePaths(root);
            using var models = new AiModelPackageService(Path.Combine(root, "models"));
            var expected = new LyricsDocument([new LyricsLine(TimeSpan.Zero, TimeSpan.FromSeconds(1), "original", "translated", [])
                { TranslationOrigin = LyricsTranslationOrigin.LocalAi, TranslationLanguage = "zh" }], LyricsProviderKind.LocalLrc);
            using var backend = new FakeBackend(expected);
            using var service = new AiLyricsService(paths, new LyricsCache(paths.Lyrics), NullLogger<AiLyricsService>.Instance,
                models, new FakeResolver(), backend);
            var result = await service.TranslateCoreAsync(new("song", "artist", "", TimeSpan.FromSeconds(1)),
                expected with { Lines = [expected.Lines[0] with { Secondary = null, TranslationOrigin = LyricsTranslationOrigin.None, TranslationLanguage = null }] },
                new() { Enabled = true, AiTranslationEnabled = true }, "zh", default);
            Assert.AreSame(expected, result);
            Assert.AreEqual(1, backend.Calls);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task InjectedCandidateDoesNotConsumeGgufCacheOrRequireShippingCatalogEntry()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppStoragePaths(root); var shared = new LyricsCache(paths.Lyrics);
            var query = new LyricsQuery("song", "artist", "album", TimeSpan.FromSeconds(1));
            var source = new LyricsDocument([new(TimeSpan.Zero, TimeSpan.FromSeconds(1), "source", null, [])], LyricsProviderKind.LocalLrc);
            await new AiLyricsCache(shared).WriteAsync(LyricsTranslationPrompt.CacheKey(query, source, "en", AiLyricsModelCatalog.Standard.Sha256),
                "[{\"id\":0,\"text\":\"wrong backend cache\"}]", default);
            var expected = source with { Lines = [source.Lines[0] with { Secondary = "candidate result", TranslationLanguage = "en", TranslationOrigin = LyricsTranslationOrigin.LocalAi }] };
            using var backend = new FakeBackend(expected);
            using var models = new AiModelPackageService(Path.Combine(root, "models"));
            using var service = new AiLyricsService(paths, shared, NullLogger<AiLyricsService>.Instance, models, new FakeResolver(), backend);
            var result = await service.TranslateCoreAsync(query, source,
                new() { Enabled = true, AiTranslationEnabled = true, AiModelId = "unpublished-fixture" }, "en", default);
            Assert.AreSame(expected, result);
            Assert.AreEqual(1, backend.Calls);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task InjectedBackendCleanupFencesCacheClearAndRetainsDiOwnership()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var paths = new AppStoragePaths(root); var shared = new LyricsCache(paths.Lyrics);
            using var backend = new FakeBackend(LyricsDocument.Empty) { Cleanup = cleanup.Task };
            using var models = new AiModelPackageService(Path.Combine(root, "models"));
            var service = new AiLyricsService(paths, shared, NullLogger<AiLyricsService>.Instance, models, new FakeResolver(), backend);
            var maintenance = service.ClearCacheAsync(default);
            Assert.IsFalse(maintenance.IsCompleted);
            Assert.AreEqual(1, backend.Drains);
            cleanup.SetResult();
            await maintenance;
            service.Dispose();
            Assert.IsFalse(backend.Disposed, "The DI container owns an injected backend.");
        }
        finally { cleanup.TrySetResult(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ShippingCompositionSliceResolvesOnlyPlainBetaAndDeclinesLegacyCache()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppStoragePaths(root);
            var services = new ServiceCollection();
            services.AddSingleton(paths);
            services.AddSingleton(new LyricsCache(paths.Lyrics));
            services.AddSingleton<ILogger<AiLyricsService>>(NullLogger<AiLyricsService>.Instance);
            services.AddSingleton(provider => new AiModelPackageService(Path.Combine(provider.GetRequiredService<AppStoragePaths>().Root, "AiLyrics", "Models")));
            services.AddSingleton(provider => new AiLyricsRuntimePackage(typeof(AiLyricsService).Assembly,
                Path.Combine(provider.GetRequiredService<AppStoragePaths>().Root, "AiLyrics", "Runtime")));
            services.AddSingleton<IAiLyricsPackageResolver, PlainHyLyricsPackageResolver>();
            services.AddSingleton(provider => new AiLyricsCache(provider.GetRequiredService<LyricsCache>()));
            services.AddSingleton<PlainHyLyricsCoordinator>();
            services.AddSingleton<AiLyricsRuntimeOptions>();
            services.AddSingleton<PersistentPlainLyricsRunner>();
            services.AddSingleton<IAiLyricsBackend>(provider => new PlainHyLyricsBackend(
                provider.GetRequiredService<PlainHyLyricsCoordinator>(), provider.GetRequiredService<PersistentPlainLyricsRunner>(),
                provider.GetRequiredService<AiLyricsRuntimePackage>(),
                Path.Combine(paths.Root, "AiLyrics", "Staging")));
            services.AddSingleton<AiLyricsService>();
            using var container = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
            var service = container.GetRequiredService<AiLyricsService>();
            Assert.IsInstanceOfType<PlainHyLyricsBackend>(container.GetRequiredService<IAiLyricsBackend>());
            Assert.IsInstanceOfType<PlainHyLyricsPackageResolver>(container.GetRequiredService<IAiLyricsPackageResolver>());
            var query = new LyricsQuery("song", "artist", "", TimeSpan.FromSeconds(1));
            var source = new LyricsDocument([new(TimeSpan.Zero, TimeSpan.FromSeconds(1), "source", null, [])], LyricsProviderKind.LocalLrc);
            await container.GetRequiredService<AiLyricsCache>().WriteAsync(
                LyricsTranslationPrompt.CacheKey(query, source, "en", AiLyricsModelCatalog.Standard.Sha256),
                "[{\"id\":0,\"text\":\"cached translation\"}]", default);
            var result = await service.TranslateIfAvailableAsync(query, source, new() { Enabled = true, AiTranslationEnabled = true }, "en", default);
            Assert.AreSame(source, result);
            Assert.AreEqual(AiLyricsTranslationState.Ready, service.TranslationState);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task LegacyDownloadIsRejectedBeforeAnyNetworkOrConsentFlow()
    {
        using var service = Create();
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.DownloadAsync(AiLyricsModelCatalog.Standard.Id,
            true, null, default));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => service.DownloadAsync(AiLyricsModelCatalog.Compact.Id,
            true, null, default));
    }

    private sealed class FakeResolver : IAiLyricsPackageResolver
    {
        public Task<AiLyricsResolvedPackage?> ResolveAsync(string selectionId, LyricsQuery query, LyricsDocument source, string targetLanguage, CancellationToken token) =>
            Task.FromResult<AiLyricsResolvedPackage?>(new("fake-v1", AiLyricsModelCatalog.Standard.Sha256, "model", "runtime", null));
    }

    private sealed class FakeBackend(LyricsDocument result) : IAiLyricsBackend
    {
        public string Id => "fake-v1";
        public int Calls { get; private set; }
        public int Drains { get; private set; }
        public bool Disposed { get; private set; }
        public Task Cleanup { get; init; } = Task.CompletedTask;
        public Task<LyricsTranslationResult> TranslateAsync(AiLyricsResolvedPackage package, LyricsQuery query,
            LyricsDocument source, string targetLanguage, CancellationToken token)
        { Calls++; return Task.FromResult(new LyricsTranslationResult(result, LyricsTranslationOutcome.Translated)); }
        public Task DrainCleanupAsync(CancellationToken token) { Drains++; return Cleanup.WaitAsync(token); }
        public void Dispose() { Disposed = true; }
    }
}
