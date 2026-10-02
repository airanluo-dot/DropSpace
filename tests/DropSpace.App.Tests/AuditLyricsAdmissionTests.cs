using System.Net;
using System.Text;
using System.Text.Json;
using DropSpace.App.Services.Media;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;
using DropSpace.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class AuditLyricsAdmissionTests
{
    private static readonly LyricsQuery Query = new("Song", "Artist", "Album", TimeSpan.FromSeconds(30), "audit-track");
    private static readonly LyricsSettings Enabled = new() { Enabled = true, AiTranslationEnabled = true };

    [TestMethod]
    [DataRow(LyricsProviderKind.NetEase, false)]
    [DataRow(LyricsProviderKind.QqMusic, false)]
    [DataRow(LyricsProviderKind.NetEase, true)]
    [DataRow(LyricsProviderKind.QqMusic, true)]
    public async Task RealPayloadWithRetainedBabyAndLegacyNullLanguagesSkipsEveryAiBoundary(
        LyricsProviderKind provider, bool legacySourceCache)
    {
        using var fixture = new Fixture();
        using var handler = new PayloadHandler(provider);
        using var client = new HttpClient(handler);
        var lyrics = new LyricsService(new LyricsProviderRegistry(new LyricsHttpClient(client), () => ""), fixture.Cache);
        var settings = Enabled with { Provider = provider };
        var queried = await lyrics.QueryDetailedAsync(Query, settings, default);
        Assert.AreEqual(LyricsQueryStatus.Found, queried.Status);
        var source = queried.Document;
        Assert.HasCount(2, source.Lines);
        Assert.AreEqual("zh-Hans", source.Lines[0].TranslationLanguage);
        Assert.IsNull(source.Lines[1].TranslationLanguage, "Baby is not positive language evidence.");
        if (legacySourceCache)
        {
            var key = JsonSerializer.Serialize(new
            {
                version = "source-v2", primary = provider, backup = (LyricsProviderKind?)null, settings.SearchRemainingProviders,
                Query.TrackIdentity, Query.Title, Query.Artist, Query.AlbumArtist, Query.Album, durationTicks = Query.Duration.Ticks,
            });
            await fixture.Cache.WriteDocumentAsync(key,
                source with { Lines = source.Lines.Select(line => line with { TranslationLanguage = null }).ToArray() }, fixture.Cache.Generation, default);
            var networkCalls = handler.Calls;
            source = (await lyrics.QueryDetailedAsync(Query, settings, default)).Document;
            Assert.AreEqual(networkCalls, handler.Calls, "Classifiable old source cache must not force an online refetch.");
            Assert.AreEqual("zh-Hans", source.Lines[0].TranslationLanguage);
            Assert.IsNull(source.Lines[1].TranslationLanguage);
        }
        var aiCache = new AiLyricsCache(fixture.Cache);
        await aiCache.WriteAsync(LyricsTranslationPrompt.CacheKey(Query, source, "zh-CN", AiLyricsModelCatalog.ExperimentalPlain.Sha256),
            "[{\"id\":0,\"text\":\"POISON\"},{\"id\":1,\"text\":\"AI must not replace Baby\"}]", default);
        var progressCalls = 0;
        var progress = new LyricsTranslationProgressContext(() => TimeSpan.Zero, () => true,
            (_, _) => { progressCalls++; return Task.CompletedTask; });
        var result = await fixture.Service.TranslateIfAvailableAsync(Query, source, settings, "zh-CN", default, progress);
        Assert.AreEqual("我会一直等待你", result.Lines[0].Secondary);
        Assert.AreEqual("Baby", result.Lines[1].Secondary);
        Assert.IsTrue(result.Lines.All(line => line.TranslationOrigin == LyricsTranslationOrigin.Provider));
        Assert.AreEqual("zh-Hans", result.Lines[0].TranslationLanguage);
        Assert.IsNull(result.Lines[1].TranslationLanguage);
        Assert.AreEqual(0, progressCalls);
        fixture.AssertNoAiCalls();
    }

    [TestMethod]
    [DataRow("I love you")]
    [DataRow("I need you")]
    [DataRow("Let it be")]
    [DataRow("Your blue cup waits beside the window.")]
    [DataRow("You said the northern road was closed.")]
    [DataRow("Your amber lantern glows beside the window.")]
    [DataRow("You said the winding path was blocked.")]
    public async Task SameTargetEnglishOriginalSkipsCacheResolverInferenceAndProgress(string text)
    {
        using var fixture = new Fixture();
        var source = LyricsParser.Parse("作词：Someone\n" + text + "\n作曲：Someone", LyricsProviderKind.NetEase);
        var progressCalls = 0;
        var progress = new LyricsTranslationProgressContext(() => TimeSpan.Zero, () => true,
            (_, _) => { progressCalls++; return Task.CompletedTask; });
        var result = await fixture.Service.TranslateIfAvailableAsync(Query, source, Enabled, "en-US", default, progress);
        CollectionAssert.AreEqual(source.Lines.ToArray(), result.Lines.ToArray());
        Assert.IsNull(result.Lines.Single().Secondary);
        Assert.AreEqual(0, progressCalls);
        fixture.AssertNoAiCalls();
    }

    [TestMethod]
    [DataRow(LyricsProviderKind.NetEase, false, "Your blue cup waits beside the window.")]
    [DataRow(LyricsProviderKind.NetEase, true, "Your blue cup waits beside the window.")]
    [DataRow(LyricsProviderKind.QqMusic, false, "Your blue cup waits beside the window.")]
    [DataRow(LyricsProviderKind.QqMusic, true, "Your blue cup waits beside the window.")]
    [DataRow(LyricsProviderKind.NetEase, false, "You said the northern road was closed.")]
    [DataRow(LyricsProviderKind.NetEase, true, "You said the northern road was closed.")]
    [DataRow(LyricsProviderKind.QqMusic, false, "You said the northern road was closed.")]
    [DataRow(LyricsProviderKind.QqMusic, true, "You said the northern road was closed.")]
    public async Task EnglishProviderContentWordsSurviveFreshAndLegacyPayloadsWithoutAnyAiCalls(
        LyricsProviderKind provider, bool legacySourceCache, string translation)
    {
        using var fixture = new Fixture();
        using var handler = new PayloadHandler(provider, originalOverride: "[00:01]你的蓝色杯子放在窗边",
            translationOverride: "[00:01]" + translation);
        using var client = new HttpClient(handler);
        var lyrics = new LyricsService(new LyricsProviderRegistry(new LyricsHttpClient(client), () => ""), fixture.Cache);
        var settings = Enabled with { Provider = provider };
        var queried = await lyrics.QueryDetailedAsync(Query, settings, default);
        Assert.AreEqual(LyricsQueryStatus.Found, queried.Status);
        var source = queried.Document;
        if (legacySourceCache)
        {
            var key = JsonSerializer.Serialize(new
            {
                version = "source-v2", primary = provider, backup = (LyricsProviderKind?)null, settings.SearchRemainingProviders,
                Query.TrackIdentity, Query.Title, Query.Artist, Query.AlbumArtist, Query.Album, durationTicks = Query.Duration.Ticks,
            });
            await fixture.Cache.WriteDocumentAsync(key, source with
                { Lines = source.Lines.Select(line => line with { TranslationLanguage = null }).ToArray() }, fixture.Cache.Generation, default);
            var requests = handler.Calls;
            source = (await lyrics.QueryDetailedAsync(Query, settings, default)).Document;
            Assert.AreEqual(requests, handler.Calls);
        }
        Assert.IsTrue(LyricsTranslationPolicy.HasMatchingProviderTranslation(source, "en-US"));
        var aiCache = new AiLyricsCache(fixture.Cache);
        await aiCache.WriteAsync(LyricsTranslationPrompt.CacheKey(Query, source, "en-US", AiLyricsModelCatalog.ExperimentalPlain.Sha256),
            "[{\"id\":0,\"text\":\"The model must not rewrite the provider translation\"}]", default);
        var progressCalls = 0;
        var progress = new LyricsTranslationProgressContext(() => TimeSpan.Zero, () => true,
            (_, _) => { progressCalls++; return Task.CompletedTask; });
        var result = await fixture.Service.TranslateIfAvailableAsync(Query, source, settings, "en-US", default, progress);
        CollectionAssert.AreEqual(source.Lines.ToArray(), result.Lines.ToArray());
        Assert.AreEqual("你的蓝色杯子放在窗边", result.Lines[0].Text);
        Assert.AreEqual(translation, result.Lines[0].Secondary);
        Assert.AreEqual(LyricsTranslationOrigin.Provider, result.Lines[0].TranslationOrigin);
        Assert.AreEqual(0, progressCalls);
        fixture.AssertNoAiCalls();
    }

    [TestMethod]
    [DataRow(LyricsProviderKind.NetEase, false)]
    [DataRow(LyricsProviderKind.QqMusic, false)]
    [DataRow(LyricsProviderKind.NetEase, true)]
    [DataRow(LyricsProviderKind.QqMusic, true)]
    public async Task UntimedRealPayloadAndOldCacheKeepSourceTranslationBesideLondonWithoutAnyAiCalls(
        LyricsProviderKind provider, bool legacySourceCache)
    {
        using var fixture = new Fixture();
        using var handler = new PayloadHandler(provider, untimed: true);
        using var client = new HttpClient(handler);
        var lyrics = new LyricsService(new LyricsProviderRegistry(new LyricsHttpClient(client), () => ""), fixture.Cache);
        var settings = Enabled with { Provider = provider };
        var queried = await lyrics.QueryDetailedAsync(Query, settings, default);
        Assert.AreEqual(LyricsQueryStatus.Found, queried.Status);
        var source = queried.Document;
        Assert.HasCount(1, source.Lines);
        Assert.AreEqual(TimeSpan.Zero, source.Lines[0].Start);
        Assert.AreEqual(Query.Duration, source.Lines[0].End);
        if (legacySourceCache)
        {
            var key = JsonSerializer.Serialize(new
            {
                version = "source-v2", primary = provider, backup = (LyricsProviderKind?)null, settings.SearchRemainingProviders,
                Query.TrackIdentity, Query.Title, Query.Artist, Query.AlbumArtist, Query.Album, durationTicks = Query.Duration.Ticks,
            });
            await fixture.Cache.WriteDocumentAsync(key,
                source with { Lines = [source.Lines[0] with { TranslationLanguage = null }] }, fixture.Cache.Generation, default);
            var networkCalls = handler.Calls;
            source = (await lyrics.QueryDetailedAsync(Query, settings, default)).Document;
            Assert.AreEqual(networkCalls, handler.Calls, "The untimed source-v2 cache must be reclassified without another provider request.");
        }
        Assert.IsTrue(LyricsTranslationPolicy.HasMatchingProviderTranslation(source, "zh-CN"));
        Assert.IsFalse(LyricsTranslationPolicy.HasMatchingProviderTranslation(source, "en-US"));
        var aiCache = new AiLyricsCache(fixture.Cache);
        var poisonKey = LyricsTranslationPrompt.CacheKey(Query, source, "zh-CN", AiLyricsModelCatalog.ExperimentalPlain.Sha256);
        const string poison = "[{\"id\":0,\"text\":\"POISON replaces the complete untimed source\"}]";
        await aiCache.WriteAsync(poisonKey, poison, default);
        var progressCalls = 0;
        var progress = new LyricsTranslationProgressContext(() => TimeSpan.Zero, () => true,
            (_, _) => { progressCalls++; return Task.CompletedTask; });
        var result = await fixture.Service.TranslateIfAvailableAsync(Query, source, settings, "zh-CN", default, progress);
        Assert.HasCount(1, result.Lines);
        Assert.AreEqual("I miss your smile" + Environment.NewLine + "London", result.Lines[0].Text);
        Assert.AreEqual("我的世界充满阳光" + Environment.NewLine + "London", result.Lines[0].Secondary);
        Assert.AreEqual(LyricsTranslationOrigin.Provider, result.Lines[0].TranslationOrigin);
        Assert.AreEqual(source.Lines[0].Start, result.Lines[0].Start);
        Assert.AreEqual(source.Lines[0].End, result.Lines[0].End);
        Assert.AreEqual(0, progressCalls);
        fixture.AssertNoAiCalls();
        Assert.AreEqual(poison, await aiCache.ReadAsync(poisonKey, default), "A source bypass must not overwrite even an unused AI cache entry.");
    }

    [TestMethod]
    [DataRow("I love you, kimi ga suki")]
    [DataRow("I love you, wo hen xiang ni")]
    [DataRow("wo hen xiang ni; I need you")]
    [DataRow("I love you and kimi ga suki")]
    [DataRow("I wo yi zhi zai deng ni need you")]
    [DataRow("I love you (suki)")]
    [DataRow("I love you | suki")]
    public async Task MixedOriginalCannotSkipActualAppAdmissionEvenWithChineseCredits(string text)
    {
        using var fixture = new Fixture();
        var source = LyricsParser.Parse("作词：某人\n" + text + "\n作曲：某人", LyricsProviderKind.NetEase);
        var result = await fixture.Service.TranslateIfAvailableAsync(Query, source, Enabled, "en-US", default);
        fixture.AssertAiAdmission();
        CollectionAssert.AreEqual(new[] { text }, LyricsLanguagePolicy.EligibleSegments(source.Lines[0], "en-US"));
        CollectionAssert.AreEqual(source.Lines.ToArray(), result.Lines.ToArray());
    }

    [TestMethod]
    [DataRow(LyricsProviderKind.NetEase, 0)]
    [DataRow(LyricsProviderKind.QqMusic, 0)]
    [DataRow(LyricsProviderKind.NetEase, 1)]
    [DataRow(LyricsProviderKind.QqMusic, 1)]
    [DataRow(LyricsProviderKind.NetEase, 2)]
    [DataRow(LyricsProviderKind.QqMusic, 2)]
    public async Task MixedProviderPayloadAndLegacyWrongTagDoNotBypassTheUntranslatedRow(
        LyricsProviderKind provider, int cacheMode)
    {
        foreach (var translation in new[] { "I love you, kimi ga suki", "I love you, wo hen xiang ni",
            "wo bu xiang zou I need you", "I love you but anata ni aitai", "I love you | suki" })
        {
            using var fixture = new Fixture();
            using var handler = new PayloadHandler(provider, originalOverride: "[00:01]君が好き\n[00:04]我的世界充满阳光",
                translationOverride: "[00:01]" + translation);
            using var client = new HttpClient(handler);
            var lyrics = new LyricsService(new LyricsProviderRegistry(new LyricsHttpClient(client), () => ""), fixture.Cache);
            var settings = Enabled with { Provider = provider };
            var source = (await lyrics.QueryDetailedAsync(Query, settings, default)).Document;
            Assert.HasCount(2, source.Lines);
            if (cacheMode != 0)
            {
                var key = SourceCacheKey(settings, provider);
                await fixture.Cache.WriteDocumentAsync(key, source with { Lines =
                    [source.Lines[0] with { TranslationLanguage = "en", TranslationLanguageIsExplicit = cacheMode == 1 ? null : false }, source.Lines[1]] },
                    fixture.Cache.Generation, default);
                if (cacheMode == 1)
                {
                    handler.FailRequests = true;
                    Assert.AreEqual(LyricsQueryStatus.Failed, (await lyrics.QueryDetailedAsync(Query, settings, default)).Status,
                        "A failed migration fetch must not return a legacy unproven tag as trusted evidence.");
                    fixture.AssertNoAiCalls();
                    handler.FailRequests = false;
                }
                var requests = handler.Calls;
                source = (await lyrics.QueryDetailedAsync(Query, settings, default)).Document;
                if (cacheMode == 1) Assert.IsTrue(handler.Calls > requests, "Unproven legacy tags require one successful provider refetch.");
                else Assert.AreEqual(requests, handler.Calls, "Known inferred tags can be reclassified without a provider request.");
                requests = handler.Calls;
                source = (await lyrics.QueryDetailedAsync(Query, settings, default)).Document;
                Assert.AreEqual(requests, handler.Calls, "The migrated source cache remains reusable.");
            }
            Assert.IsNull(source.Lines[0].TranslationLanguage);
            Assert.IsFalse(LyricsTranslationPolicy.HasMatchingProviderTranslation(source, "en-US"));
            CollectionAssert.AreEqual(new[] { 0, 1 }, LyricsLanguagePolicy.EligibleIndices(source, "en-US"));
            await fixture.Service.TranslateIfAvailableAsync(Query, source, settings, "en-US", default);
            fixture.AssertAiAdmission();
        }
    }

    [TestMethod]
    [DataRow(LyricsProviderKind.NetEase)]
    [DataRow(LyricsProviderKind.QqMusic)]
    public async Task ExplicitShortTtmlTranslationSurvivesLegacyMigrationAndNewSourceCache(LyricsProviderKind provider)
    {
        using var fixture = new Fixture();
        using var handler = new PayloadHandler(provider, originalOverride: "[00:01]世界",
            translationOverride: "<tt xmlns=\"http://www.w3.org/ns/ttml\" xml:lang=\"en\"><body><p begin=\"1s\">World</p></body></tt>");
        using var client = new HttpClient(handler);
        var lyrics = new LyricsService(new LyricsProviderRegistry(new LyricsHttpClient(client), () => ""), fixture.Cache);
        var settings = Enabled with { Provider = provider };
        var source = (await lyrics.QueryDetailedAsync(Query, settings, default)).Document;
        await fixture.Cache.WriteDocumentAsync(SourceCacheKey(settings, provider), source with { Lines =
            [source.Lines[0] with { TranslationLanguageIsExplicit = null }] }, fixture.Cache.Generation, default);
        var requests = handler.Calls;
        source = (await lyrics.QueryDetailedAsync(Query, settings, default)).Document;
        Assert.IsTrue(handler.Calls > requests);
        requests = handler.Calls;
        source = (await lyrics.QueryDetailedAsync(Query, settings, default)).Document;
        Assert.AreEqual(requests, handler.Calls);
        Assert.AreEqual(true, source.Lines[0].TranslationLanguageIsExplicit);
        Assert.AreEqual("World", source.Lines[0].Secondary);
        await fixture.Service.TranslateIfAvailableAsync(Query, source, settings, "en-US", default);
        fixture.AssertNoAiCalls();
    }

    private static string SourceCacheKey(LyricsSettings settings, LyricsProviderKind provider) => JsonSerializer.Serialize(new
    {
        version = "source-v2", primary = provider, backup = (LyricsProviderKind?)null, settings.SearchRemainingProviders,
        Query.TrackIdentity, Query.Title, Query.Artist, Query.AlbumArtist, Query.Album, durationTicks = Query.Duration.Ticks,
    });

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "DropSpace-audit-app-" + Guid.NewGuid().ToString("N"));
        private readonly AiModelPackageService _models;
        private readonly BoundarySpy _spy = new();
        public LyricsCache Cache { get; }
        public AiLyricsService Service { get; }
        public Fixture()
        {
            var paths = new AppStoragePaths(_root);
            Cache = new(paths.Lyrics);
            _models = new(Path.Combine(_root, "models"));
            Service = new(paths, Cache, NullLogger<AiLyricsService>.Instance, _models, _spy, _spy);
        }
        public void AssertNoAiCalls()
        {
            Assert.AreEqual(0, _spy.CacheCalls);
            Assert.AreEqual(0, _spy.ResolverCalls);
            Assert.AreEqual(0, _spy.InferenceCalls);
            Assert.AreEqual(0, _spy.DrainCalls);
        }
        public void AssertAiAdmission()
        {
            Assert.AreEqual(1, _spy.CacheCalls);
            Assert.AreEqual(1, _spy.ResolverCalls);
            Assert.AreEqual(0, _spy.InferenceCalls, "The spy returns no package; no actual model runs in this regression.");
        }
        public void Dispose()
        {
            Service.Dispose(); _models.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }

    private sealed class BoundarySpy : IAiLyricsBackend, IAiLyricsPackageResolver
    {
        public string Id => "audit-spy";
        public int CacheCalls { get; private set; }
        public int ResolverCalls { get; private set; }
        public int InferenceCalls { get; private set; }
        public int DrainCalls { get; private set; }
        public Task<LyricsTranslationResult?> TryGetCachedResultAsync(string selectionId, LyricsQuery query, LyricsDocument source,
            string targetLanguage, CancellationToken token)
        { CacheCalls++; return Task.FromResult<LyricsTranslationResult?>(null); }
        public Task<AiLyricsResolvedPackage?> ResolveAsync(string selectionId, LyricsQuery query, LyricsDocument source,
            string targetLanguage, CancellationToken token)
        { ResolverCalls++; return Task.FromResult<AiLyricsResolvedPackage?>(null); }
        public Task<LyricsTranslationResult> TranslateAsync(AiLyricsResolvedPackage package, LyricsQuery query,
            LyricsDocument source, string targetLanguage, CancellationToken token)
        { InferenceCalls++; return Task.FromResult(new LyricsTranslationResult(source, LyricsTranslationOutcome.Failed)); }
        public Task DrainCleanupAsync(CancellationToken token) { DrainCalls++; return Task.CompletedTask; }
        public void Dispose() { }
    }

    private sealed class PayloadHandler(LyricsProviderKind provider, bool untimed = false,
        string? originalOverride = null, string? translationOverride = null) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool FailRequests { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (FailRequests) throw new HttpRequestException("Synthetic provider outage during legacy-cache migration.");
            var lyric = request.RequestUri!.AbsolutePath.Contains("lyric", StringComparison.Ordinal);
            var original = originalOverride ?? (untimed ? "I miss your smile\nLondon" : "[00:01]I will wait for you\n[00:04]Baby");
            var translated = translationOverride ?? (untimed ? "我的世界充满阳光\nLondon" : "[00:01]我会一直等待你\n[00:04]Baby");
            var json = provider == LyricsProviderKind.NetEase
                ? lyric ? JsonSerializer.Serialize(new { code = 200, lrc = new { lyric = original }, tlyric = new { lyric = translated } })
                    : """{"code":200,"result":{"songs":[{"id":1,"name":"Song","artists":[{"name":"Artist"}],"album":{"name":"Album"},"duration":30000}]}}"""
                : lyric ? JsonSerializer.Serialize(new { lyric = original, trans = translated })
                    : """{"data":{"song":{"list":[{"songmid":"id","songname":"Song","singer":[{"name":"Artist"}],"albumname":"Album","interval":30}]}}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { RequestMessage = request, Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
