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
public sealed class AiLyricsAdmissionRegressionTests
{
    private static readonly LyricsQuery Query = new("Song", "Artist", "Album", TimeSpan.FromSeconds(30), "track");
    private static readonly LyricsSettings Enabled = new() { Enabled = true, AiTranslationEnabled = true };

    [TestMethod]
    [DataRow(LyricsProviderKind.NetEase, false)]
    [DataRow(LyricsProviderKind.QqMusic, false)]
    [DataRow(LyricsProviderKind.NetEase, true)]
    [DataRow(LyricsProviderKind.QqMusic, true)]
    public async Task RealProviderPayloadAndOldSourceCacheBypassAllAiBeforePreflight(LyricsProviderKind provider, bool oldCache)
    {
        using var fixture = new Fixture();
        using var handler = new PayloadHandler(provider);
        using var http = new HttpClient(handler);
        var service = new LyricsService(new LyricsProviderRegistry(new LyricsHttpClient(http), () => ""), fixture.Cache);
        var settings = Enabled with { Provider = provider };
        var document = (await service.QueryDetailedAsync(Query, settings, default)).Document;
        Assert.AreEqual("zh-Hans", document.Lines[0].TranslationLanguage);
        if (oldCache)
        {
            var legacy = document with { Lines = document.Lines.Select(l => l with { TranslationLanguage = null }).ToArray() };
            var key = JsonSerializer.Serialize(new
            {
                version = "source-v2", primary = provider, backup = (LyricsProviderKind?)null, settings.SearchRemainingProviders,
                Query.TrackIdentity, Query.Title, Query.Artist, Query.AlbumArtist, Query.Album, durationTicks = Query.Duration.Ticks,
            });
            await fixture.Cache.WriteDocumentAsync(key, legacy, fixture.Cache.Generation, default);
            var calls = handler.Calls;
            document = (await service.QueryDetailedAsync(Query, settings, default)).Document;
            Assert.AreEqual(calls, handler.Calls, "A recoverable v2 cache entry must retain source cache semantics.");
        }
        // The injected preflight would return poison. The persistent legacy AI entry is poison too.
        await new AiLyricsCache(fixture.Cache).WriteAsync(LyricsTranslationPrompt.CacheKey(Query, document, "zh-CN", AiLyricsModelCatalog.ExperimentalPlain.Sha256),
            "[{\"id\":0,\"text\":\"poison\"}]", default);
        var progressCalls = 0;
        var progress = new LyricsTranslationProgressContext(() => TimeSpan.Zero, () => true,
            (_, _) => { progressCalls++; return Task.CompletedTask; });
        var result = await fixture.Service.TranslateIfAvailableAsync(Query, document, settings, "zh-CN", default, progress);
        Assert.AreEqual("我会一直等待你", result.Lines[0].Secondary);
        Assert.AreEqual(LyricsTranslationOrigin.Provider, result.Lines[0].TranslationOrigin);
        Assert.IsNull(result.Lines[1].Secondary, "The whole-song bypass must not fill a provider gap.");
        Assert.AreEqual(0, progressCalls);
        fixture.AssertNoAiCalls();
    }

    [TestMethod]
    public async Task SameLanguageAndCreditsBypassPoisonCacheResolverAndInference()
    {
        using var fixture = new Fixture();
        var source = LyricsParser.Parse("[00:00]作词：某人\n[00:01]我的世界充满阳光\n[00:04]我们仍在这里\n[00:08]作曲：另一人", LyricsProviderKind.NetEase);
        var result = await fixture.Service.TranslateIfAvailableAsync(Query, source, Enabled, "zh-CN", default);
        CollectionAssert.AreEqual(source.Lines.ToArray(), result.Lines.ToArray());
        fixture.AssertNoAiCalls();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ChineseSongAndStaleAiRowsBypassBackendBeforeCacheOrPackageAccess(bool timed)
    {
        using var fixture = new Fixture();
        string[] text = ["SingerA、SingerB - 合成曲 (with SingerB)", "作词：WriterX", "作曲：ComposerY/ComposerZ",
            "SingerA:", "我们带着蓝色雨伞", "山谷的纸船", "我的小船停在岸边", "晴"];
        var source = timed ? new LyricsDocument(text.Select((part, id) => new LyricsLine(TimeSpan.FromSeconds(id * 3),
            TimeSpan.FromSeconds(id * 3 + 3), part, null, [])).ToArray(), LyricsProviderKind.NetEase)
            : LyricsParser.Parse(string.Join("\n", text), LyricsProviderKind.NetEase);
        source = source with { Match = new("合成曲 (with SingerB)", "SingerA、SingerB", "合成专辑", 40, 12) };
        var poisoned = source with { Lines = source.Lines.Select(line => line with
            { Secondary = "合成中文改写，", TranslationOrigin = LyricsTranslationOrigin.LocalAi, TranslationLanguage = "zh-CN" }).ToArray() };
        var result = await fixture.Service.TranslateIfAvailableAsync(Query, poisoned, Enabled, "zh-CN", default);
        for (var id = 0; id < source.Lines.Count; id++)
        {
            Assert.AreEqual(source.Lines[id].Text, result.Lines[id].Text);
            Assert.AreEqual(source.Lines[id].Start, result.Lines[id].Start);
            Assert.AreEqual(source.Lines[id].End, result.Lines[id].End);
            Assert.AreSame(source.Lines[id].Words, result.Lines[id].Words);
            Assert.IsNull(LyricsDisplayPolicy.SecondaryPresentation(result.Lines[id], "zh-CN", true));
        }
        fixture.AssertNoAiCalls();
    }

    [TestMethod]
    [DataRow("[00:01]kimi no na wa", "zh-CN")]
    [DataRow("[00:01]愛", "zh-CN")]
    [DataRow("[00:01]君の声が聞こえる", "zh-CN")]
    [DataRow("[00:01]我会一直等待你", "en-US")]
    [DataRow("[00:01]I will wait for you", "zh-CN")]
    public async Task WrongLanguageRomanizationAndShortHanDoNotBypassAi(string translation, string target)
    {
        using var fixture = new Fixture();
        var source = LyricsParser.Parse("[00:01]unknown source", LyricsProviderKind.NetEase, translation);
        await fixture.Service.TranslateCoreAsync(Query, source, Enabled, target, default);
        Assert.AreEqual(1, fixture.Backend.CacheCalls);
    }

    [TestMethod]
    public async Task ExplicitTtmlTranslationBypassesBeforeModelOrRuntime()
    {
        using var fixture = new Fixture();
        var source = LyricsParser.Parse("""
            <tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttm="http://www.w3.org/ns/ttml#metadata"><body><div>
            <p begin="1s" end="3s">世界<span ttm:role="x-translation" xml:lang="en-US">World</span></p>
            </div></body></tt>
            """, LyricsProviderKind.Amll);
        var result = await fixture.Service.TranslateIfAvailableAsync(Query, source, Enabled, "en", default);
        Assert.AreEqual("World", result.Lines.Single().Secondary);
        fixture.AssertNoAiCalls();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AmbiguousHanPublicationIsCleanAndReadyWithoutCacheResolverOrProgress(bool accepted)
    {
        using var fixture = new Fixture();
        var source = LyricsParser.Parse("[00:01]山谷的石门\n[00:04]晴\n[00:07]我不関焉", LyricsProviderKind.NetEase);
        if (accepted) source = source with { Match = new("合成曲", "SingerA", "合成专辑", 40, 12) };
        source = source with { Lines = source.Lines.Select(line => line with { Secondary = "旧中文改写",
            TranslationOrigin = LyricsTranslationOrigin.LocalAi, TranslationLanguage = "zh-CN" }).ToArray() };
        var progress = new LyricsTranslationProgressContext(() => TimeSpan.Zero, () => true,
            (_, _) => throw new AssertFailedException("Abstention must not publish AI progress."));
        var publication = await fixture.Service.TranslateForPublicationAsync(Query, source, Enabled, "zh-CN", default, progress);
        Assert.IsTrue(publication.IsCurrent);
        Assert.AreEqual(AiLyricsTranslationState.Ready, fixture.Service.TranslationState);
        for (var id = 0; id < source.Lines.Count; id++)
        {
            Assert.IsNull(LyricsDisplayPolicy.SecondaryPresentation(publication.Document.Lines[id], "zh-CN", true));
            Assert.AreEqual(source.Lines[id].Text, publication.Document.Lines[id].Text);
            Assert.AreSame(source.Lines[id].Words, publication.Document.Lines[id].Words);
        }
        fixture.AssertNoAiCalls();
    }

    [TestMethod]
    public async Task DisabledAiPublicationStillDiscardsUnboundMixedSecondaryAndKeepsProvider()
    {
        using var fixture = new Fixture();
        var source = LyricsParser.Parse("[00:01]山谷的石门\n[00:04]I will wait for you", LyricsProviderKind.NetEase);
        source = source with { Lines = [source.Lines[0] with { Text = "山谷的石门\nI will wait for you",
            Secondary = "旧中文改写混合译文", TranslationOrigin = LyricsTranslationOrigin.LocalAi, TranslationLanguage = "zh-CN" },
            source.Lines[1] with { Secondary = "提供方原译文", TranslationOrigin = LyricsTranslationOrigin.Provider,
                TranslationLanguage = "zh-CN", TranslationLanguageIsExplicit = true }] };
        var publication = await fixture.Service.TranslateForPublicationAsync(Query, source,
            Enabled with { AiTranslationEnabled = false }, "zh-CN", default);
        Assert.IsNull(publication.Document.Lines[0].Secondary);
        Assert.AreEqual(source.Lines[0].Text, publication.Document.Lines[0].Text);
        Assert.AreSame(source.Lines[1], publication.Document.Lines[1]);
        fixture.AssertNoAiCalls();
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OriginalOnlyFallbackEntersAiAfterIncompleteProviderSearch(bool incomplete)
    {
        using var fixture = new Fixture();
        var document = LyricsParser.Parse("[00:01]The night is full of stars", LyricsProviderKind.NetEase);
        var source = new LyricsQueryResult(document, LyricsQueryStatus.Found, incomplete);
        Assert.IsTrue(LyricsTranslationPolicy.CanOfferLocalFallback(source));
        var publication = await fixture.Service.TranslateForPublicationAsync(Query, source.Document, Enabled,
            "zh-CN", default);
        Assert.AreEqual(1, fixture.Backend.CacheCalls, "A supplemental lookup timeout must still reach AI admission.");
        Assert.IsTrue(publication.IsCurrent);
        Assert.AreEqual(LyricsTranslationOrigin.LocalAi, publication.Document.Lines[0].TranslationOrigin);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "DropSpace-admission-" + Guid.NewGuid().ToString("N"));
        private readonly AiModelPackageService _models;
        public LyricsCache Cache { get; }
        public CountingBackend Backend { get; } = new();
        public CountingResolver Resolver { get; } = new();
        public AiLyricsService Service { get; }
        public Fixture()
        {
            var paths = new AppStoragePaths(_root);
            Cache = new(paths.Lyrics);
            _models = new(Path.Combine(_root, "models"));
            Service = new(paths, Cache, NullLogger<AiLyricsService>.Instance, _models, Resolver, Backend);
        }
        public void AssertNoAiCalls()
        {
            Assert.AreEqual(0, Backend.CacheCalls);
            Assert.AreEqual(0, Backend.Calls);
            Assert.AreEqual(0, Resolver.Calls);
            Assert.AreEqual(0, Backend.Drains, "No runtime configuration/cleanup is needed for a provider or same-language bypass.");
        }
        public void Dispose()
        {
            Service.Dispose(); _models.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }
    private sealed class CountingResolver : IAiLyricsPackageResolver
    {
        public int Calls { get; private set; }
        public Task<AiLyricsResolvedPackage?> ResolveAsync(string selectionId, LyricsQuery query, LyricsDocument source, string targetLanguage, CancellationToken token)
        { Calls++; return Task.FromResult<AiLyricsResolvedPackage?>(null); }
    }
    private sealed class CountingBackend : IAiLyricsBackend
    {
        public string Id => "counting";
        public int CacheCalls { get; private set; }
        public int Calls { get; private set; }
        public int Drains { get; private set; }
        public Task<LyricsTranslationResult?> TryGetCachedResultAsync(string selectionId, LyricsQuery query, LyricsDocument source, string targetLanguage, CancellationToken token)
        {
            CacheCalls++;
            return Task.FromResult<LyricsTranslationResult?>(new(source with { Lines = source.Lines.Select(l => l with
            { Secondary = "poison", TranslationLanguage = targetLanguage, TranslationOrigin = LyricsTranslationOrigin.LocalAi }).ToArray() }, LyricsTranslationOutcome.Translated));
        }
        public Task<LyricsTranslationResult> TranslateAsync(AiLyricsResolvedPackage package, LyricsQuery query, LyricsDocument source, string targetLanguage, CancellationToken token)
        { Calls++; throw new AssertFailedException("Preflight already supplied poison; inference should not be reached."); }
        public Task DrainCleanupAsync(CancellationToken token) { Drains++; return Task.CompletedTask; }
        public void Dispose() { }
    }
    private sealed class PayloadHandler(LyricsProviderKind provider) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var lyric = request.RequestUri!.AbsolutePath.Contains("lyric", StringComparison.Ordinal);
            var json = provider == LyricsProviderKind.NetEase
                ? lyric ? JsonSerializer.Serialize(new { code = 200, lrc = new { lyric = "[00:01]I will wait for you\n[00:04]Second line" }, tlyric = new { lyric = "[00:01]我会一直等待你" } })
                    : """{"code":200,"result":{"songs":[{"id":1,"name":"Song","artists":[{"name":"Artist"}],"album":{"name":"Album"},"duration":30000}]}}"""
                : lyric ? JsonSerializer.Serialize(new { lyric = "[00:01]I will wait for you\n[00:04]Second line", trans = "[00:01]我会一直等待你" })
                    : """{"data":{"song":{"list":[{"songmid":"id","songname":"Song","singer":[{"name":"Artist"}],"albumname":"Album","interval":30}]}}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
