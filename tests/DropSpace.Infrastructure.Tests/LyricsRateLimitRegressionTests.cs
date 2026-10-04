using System.Net;
using System.Text;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class LyricsRateLimitRegressionTests
{
    [TestMethod]
    public async Task Api405IsObservableAsRateLimitDespiteHttp200()
    {
        var events = new List<LyricsDiagnostic>();
        using var handler = new RejectedHandler();
        using var client = new HttpClient(handler);
        var service = new LyricsService(new([new NetEaseLyricsProvider(new(client, events.Add))]), events.Add);
        var result = await service.QueryDetailedAsync(new("private-title", "private-artist", "", TimeSpan.Zero),
            new() { SearchRemainingProviders = false }, default);
        Assert.AreEqual(LyricsQueryStatus.Failed, result.Status);
        var transport = events.Single(value => value.Stage == LyricsDiagnosticStage.Search);
        Assert.AreEqual(LyricsDiagnosticOutcome.RateLimited, transport.Outcome);
        Assert.AreEqual(200, transport.HttpStatus);
        Assert.AreEqual(405, transport.ApiCode);
        Assert.AreEqual(LyricsDiagnosticOutcome.RateLimited,
            events.Single(value => value.Stage == LyricsDiagnosticStage.Query).Outcome);
        var json = System.Text.Json.JsonSerializer.Serialize(events);
        Assert.DoesNotContain("private-title", json);
        Assert.DoesNotContain("private-artist", json);
    }

    [TestMethod]
    public async Task RateLimitedPrimaryAllowsActualQqTranslationFromRemainingProviders()
    {
        using var handler = new QqFallbackHandler();
        using var client = new HttpClient(handler);
        var http = new LyricsHttpClient(client);
        var service = new LyricsService(new([new NetEaseLyricsProvider(http), new QqMusicLyricsProvider(http)]));
        var result = await service.QueryDetailedAsync(
            new("Track", "Artist", "", TimeSpan.Zero) { PreferredTranslationLanguage = "zh-Hans" },
            new() { SearchRemainingProviders = true }, default);
        Assert.AreEqual(LyricsQueryStatus.Found, result.Status);
        Assert.AreEqual(LyricsProviderKind.QqMusic, result.Document.Provider);
        Assert.IsTrue(LyricsTranslationPolicy.HasMatchingProviderTranslation(result.Document, "zh-Hans"));
        Assert.IsFalse(result.TranslationLookupIncomplete);
        Assert.AreEqual(3, handler.Calls);
    }

    private sealed class QqFallbackHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            var payload = request.RequestUri!.Host == "music.163.com" ? """{"code":405}"""
                : request.RequestUri.AbsolutePath.Contains("search", StringComparison.Ordinal)
                ? """{"code":0,"data":{"song":{"list":[{"songmid":"fixture","songname":"Track","singer":[{"name":"Artist"}]}]}}}"""
                : """{"code":0,"lyric":"[00:01]Hello my friend","trans":"[00:01]\u4f60\u597d\u6211\u7684\u670b\u53cb"}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { RequestMessage = request, Content = new StringContent(payload, Encoding.UTF8, "application/json") });
        }
    }

    [TestMethod]
    public async Task RateLimitedPrimaryStillAllowsTranslatedBackupImmediately()
    {
        using var handler = new RejectedHandler();
        using var client = new HttpClient(handler);
        var query = new LyricsQuery("Track", "Artist", "", TimeSpan.Zero) { PreferredTranslationLanguage = "zh-Hans" };
        var service = new LyricsService(new([new NetEaseLyricsProvider(new(client)), new TranslatedBackup()]));
        var result = await service.QueryDetailedAsync(query,
            new() { SearchRemainingProviders = false, BackupProvider = LyricsProviderKind.QqMusic }, default);
        Assert.AreEqual(LyricsQueryStatus.Found, result.Status);
        Assert.AreEqual(LyricsProviderKind.QqMusic, result.Document.Provider);
        Assert.IsTrue(LyricsTranslationPolicy.HasMatchingProviderTranslation(result.Document, "zh-Hans"));
        Assert.IsFalse(result.TranslationLookupIncomplete);
    }

    private sealed class RejectedHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent("""{"code":405,"message":"rate limited"}""", Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class TranslatedBackup : ILyricsProvider
    {
        public LyricsProviderKind Kind => LyricsProviderKind.QqMusic;
        public Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken token) =>
            Task.FromResult(new LyricsDocument([new(TimeSpan.Zero, TimeSpan.FromSeconds(10), "Hello my friend", "你好朋友", [])
            { TranslationOrigin = LyricsTranslationOrigin.Provider, TranslationLanguage = "zh-Hans", TranslationLanguageIsExplicit = true }], Kind)
                .Bind(query, query.Title, query.Artist, "", 0, 10, "fixture"));
    }
}
