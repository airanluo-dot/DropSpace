using System.Net;
using System.Text;
using DropSpace.Core.Lyrics;
using DropSpace.Infrastructure.Lyrics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class NetEaseRequestReuseTests
{
    [TestMethod]
    public async Task ExpiryRefreshAndClearReallyReadFreshResponses()
    {
        var clock = new ManualClock();
        using var handler = new CatalogHandler();
        using var client = new HttpClient(handler);
        var provider = new NetEaseLyricsProvider(new(client), clock);
        await provider.QueryAsync(Query(false), default);
        clock.Now += TimeSpan.FromMinutes(10);
        await provider.QueryAsync(Query(true), default);
        Assert.AreEqual(2, handler.Searches);
        await provider.QueryAsync(Query(true) with { BypassProviderResponseCache = true }, default);
        Assert.AreEqual(3, handler.Searches);
        provider.ClearResponseCache();
        await provider.QueryAsync(Query(false), default);
        Assert.AreEqual(4, handler.Searches);
        Assert.AreEqual(4, handler.Lyrics);
    }

    [TestMethod]
    public async Task CancellingQueuedWaiterDoesNotCancelOwner()
    {
        using var handler = new CatalogHandler { Delay = true };
        using var client = new HttpClient(handler);
        var provider = new NetEaseLyricsProvider(new(client));
        using var stop = new CancellationTokenSource();
        var owner = provider.QueryAsync(Query(false), default);
        var waiter = provider.QueryAsync(Query(true), stop.Token);
        await stop.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => waiter);
        Assert.IsTrue(LyricsTranslationPolicy.HasMatchingProviderTranslation(await owner, "zh-Hans"));
        Assert.AreEqual(1, handler.Searches);
    }

    [TestMethod]
    public async Task CancelledOwnerCannotBlockNewQueryOrOverwriteItsResponse()
    {
        using var handler = new RetiringHandler();
        using var client = new HttpClient(handler);
        var cache = new NetEaseResponseCache(new(client));
        const string url = "https://music.163.com/api/search/get/web?s=Track";
        using var stop = new CancellationTokenSource();
        var old = cache.GetAsync(url, stop.Token);
        await handler.Started.Task;
        var current = cache.GetAsync(url, default);
        await stop.CancelAsync();
        using var fresh = await current.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual("new", fresh.RootElement.GetProperty("result").GetProperty("songs")[0].GetProperty("name").GetString());
        handler.Release.TrySetResult();
        await Assert.ThrowsAsync<OperationCanceledException>(() => old);
        using var saved = await cache.GetAsync(url, default);
        Assert.AreEqual("new", saved.RootElement.GetProperty("result").GetProperty("songs")[0].GetProperty("name").GetString());
        Assert.AreEqual(2, handler.Calls);
    }

    [TestMethod]
    public async Task ClearingWhileRequestIsPendingFencesItsLateSuccess()
    {
        using var handler = new RetiringHandler();
        using var client = new HttpClient(handler);
        var cache = new NetEaseResponseCache(new(client));
        const string url = "https://music.163.com/api/search/get/web?s=Track";
        var old = cache.GetAsync(url, default);
        await handler.Started.Task;
        cache.Clear();
        using var fresh = await cache.GetAsync(url, default);
        handler.Release.TrySetResult();
        using var retired = await old;
        using var saved = await cache.GetAsync(url, default);
        Assert.AreEqual("new", saved.RootElement.GetProperty("result").GetProperty("songs")[0].GetProperty("name").GetString());
        Assert.AreEqual(2, handler.Calls);
    }

    [TestMethod]
    public async Task DocumentOwnershipClearFenceAndEntryEvictionAreIndependent()
    {
        using var handler = new CatalogHandler();
        using var client = new HttpClient(handler);
        var cache = new NetEaseResponseCache(new(client));
        const string url = "https://music.163.com/api/search/get/web?s=Track";
        using var first = await cache.GetAsync(url, default);
        using var second = await cache.GetAsync(url, default);
        first.Dispose();
        Assert.AreEqual(200, second.RootElement.GetProperty("code").GetInt32());
        Assert.AreEqual(1, handler.Searches);
        for (var index = 0; index < 128; index++)
        {
            using var item = await cache.GetAsync(url + index, default);
        }
        using var evicted = await cache.GetAsync(url, default);
        Assert.AreEqual(130, handler.Searches);
    }

    [TestMethod]
    [DataRow("""{"code":405}""")]
    [DataRow("""{"code":200,"result":"opaque"}""")]
    [DataRow("""{"code":200,"result":{"songs":["opaque"]}}""")]
    public async Task RejectionsAndInvalidShapesAreNeverCached(string payload)
    {
        using var handler = new PayloadHandler(payload);
        using var client = new HttpClient(handler);
        var cache = new NetEaseResponseCache(new(client));
        using var first = await cache.GetAsync("https://music.163.com/api/search/get/web?s=Track", default);
        using var second = await cache.GetAsync("https://music.163.com/api/search/get/web?s=Track", default);
        Assert.AreEqual(2, handler.Calls);
    }

    [TestMethod]
    public async Task PayloadByteBudgetEvictsBeforeEntryLimit()
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(new { code = 200, lrc = new { lyric = new string('a', 2 * 1024 * 1024) } });
        using var handler = new PayloadHandler(payload);
        using var client = new HttpClient(handler);
        var cache = new NetEaseResponseCache(new(client));
        for (var index = 0; index < 8; index++)
        {
            using var item = await cache.GetAsync("https://music.163.com/api/song/lyric?id=" + index, default);
        }
        using var evicted = await cache.GetAsync("https://music.163.com/api/song/lyric?id=0", default);
        Assert.AreEqual(9, handler.Calls);
    }

    [TestMethod]
    public async Task RichAndSparseMetadataReuseResponsesButKeepSeparateMatchEvidence()
    {
        using var handler = new CatalogHandler();
        using var client = new HttpClient(handler);
        var provider = new NetEaseLyricsProvider(new(client));
        var sparse = await provider.QueryAsync(Query(false), default);
        var rich = await provider.QueryAsync(Query(true), default);
        Assert.AreEqual(1, handler.Searches);
        Assert.AreEqual(1, handler.Lyrics);
        Assert.AreEqual(10d, sparse.Match!.Score);
        Assert.AreEqual(13d, rich.Match!.Score);
        Assert.AreEqual("sparse", sparse.Match.TrackIdentity);
        Assert.AreEqual("rich", rich.Match.TrackIdentity);
        Assert.IsTrue(LyricsTranslationPolicy.HasMatchingProviderTranslation(rich, "zh-Hans"));
    }

    [TestMethod]
    public async Task ConcurrentIdenticalRequestsReuseSuccessfulResponses()
    {
        using var handler = new CatalogHandler { Delay = true };
        using var client = new HttpClient(handler);
        var provider = new NetEaseLyricsProvider(new(client));
        await Task.WhenAll(provider.QueryAsync(Query(false), default), provider.QueryAsync(Query(true), default));
        Assert.AreEqual(1, handler.Searches);
        Assert.AreEqual(1, handler.Lyrics);
    }

    [TestMethod]
    public async Task OptionalNullFieldsAndTrackRevisitsReuseCompleteSuccessfulPayload()
    {
        using var handler = new PayloadHandler("""{"code":200,"lrc":{"lyric":"[00:01]Hello"},"tlyric":{"lyric":"[00:01]你好"},"yrc":null,"ytlrc":{}}""");
        using var client = new HttpClient(handler);
        var clock = new ManualClock();
        var cache = new NetEaseResponseCache(new(client), clock);
        const string url = "https://music.163.com/api/song/lyric?id=1";
        using var first = await cache.GetAsync(url, default);
        clock.Now += TimeSpan.FromSeconds(45);
        using var second = await cache.GetAsync(url, default);
        Assert.AreEqual(first.RootElement.GetRawText(), second.RootElement.GetRawText());
        Assert.AreEqual(1, handler.Calls);
    }

    [TestMethod]
    public async Task ConcurrentRejectionIsSharedButNextIndependentCallIsFresh()
    {
        using var handler = new SharedRejectionHandler();
        using var client = new HttpClient(handler);
        var cache = new NetEaseResponseCache(new(client));
        const string url = "https://music.163.com/api/search/get/web?s=Track";
        var owner = cache.GetAsync(url, default);
        await handler.Started.Task;
        var waiter = cache.GetAsync(url, default);
        handler.Release.TrySetResult();
        using var first = await owner;
        using var second = await waiter;
        Assert.AreEqual(405, second.RootElement.GetProperty("code").GetInt32());
        Assert.AreEqual(1, handler.Calls);
        using var retry = await cache.GetAsync(url, default);
        Assert.AreEqual(2, handler.Calls);
    }

    [TestMethod]
    public async Task MissingTargetTranslationRefetchesAfterOneSecondWhileTranslationStaysReusable()
    {
        using var handler = new ChangingTranslationHandler();
        using var client = new HttpClient(handler);
        var clock = new ManualClock();
        var cache = new NetEaseResponseCache(new(client), clock);
        const string url = "https://music.163.com/api/song/lyric?id=1";
        using var original = await cache.GetAsync(url, default, translationTarget: "zh-Hans");
        clock.Now += TimeSpan.FromMilliseconds(500);
        using var handoff = await cache.GetAsync(url, default, translationTarget: "zh-Hans");
        Assert.AreEqual(1, handler.Calls);
        clock.Now += TimeSpan.FromMilliseconds(501);
        using var translated = await cache.GetAsync(url, default, translationTarget: "zh-Hans");
        Assert.AreEqual(2, handler.Calls);
        Assert.IsTrue(translated.RootElement.TryGetProperty("tlyric", out _));
        clock.Now += TimeSpan.FromMinutes(2);
        using var revisit = await cache.GetAsync(url, default, translationTarget: "zh-Hans");
        Assert.AreEqual(2, handler.Calls);
        using var changedTarget = await cache.GetAsync(url, default, translationTarget: "ja");
        Assert.AreEqual(3, handler.Calls);
    }

    [TestMethod]
    public async Task SameLanguageOriginalKeepsLongReuseWithoutInventingTranslation()
    {
        using var handler = new PayloadHandler("""{"code":200,"lrc":{"lyric":"[00:01]我们在这里等你\n[00:02]你的世界充满阳光"}}""");
        using var client = new HttpClient(handler);
        var clock = new ManualClock();
        var cache = new NetEaseResponseCache(new(client), clock);
        const string url = "https://music.163.com/api/song/lyric?id=1";
        using var first = await cache.GetAsync(url, default, translationTarget: "zh-Hans");
        clock.Now += TimeSpan.FromSeconds(5);
        using var again = await cache.GetAsync(url, default, translationTarget: "zh-Hans");
        Assert.AreEqual(1, handler.Calls);
        Assert.IsFalse(again.RootElement.TryGetProperty("tlyric", out _));
        using var foreignTarget = await cache.GetAsync(url, default, translationTarget: "en");
        Assert.AreEqual(2, handler.Calls);
        clock.Now += TimeSpan.FromSeconds(2);
        using var retry = await cache.GetAsync(url, default, translationTarget: "en");
        Assert.AreEqual(3, handler.Calls);
    }

    private sealed class ChangingTranslationHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var count = Interlocked.Increment(ref Calls);
            var payload = count == 1
                ? """{"code":200,"lrc":{"lyric":"[00:01]The night is full of stars."}}"""
                : """{"code":200,"lrc":{"lyric":"[00:01]The night is full of stars."},"tlyric":{"lyric":"[00:01]我们一起走向明天。"}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { RequestMessage = request, Content = new StringContent(payload, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class SharedRejectionHandler : HttpMessageHandler
    {
        public int Calls;
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            Started.TrySetResult();
            await Release.Task.WaitAsync(token);
            return new(HttpStatusCode.OK) { RequestMessage = request,
                Content = new StringContent("""{"code":405}""", Encoding.UTF8, "application/json") };
        }
    }

    private static LyricsQuery Query(bool rich) => new("Track", "Artist", rich ? "Album" : "",
        rich ? TimeSpan.FromSeconds(113) : TimeSpan.Zero, rich ? "rich" : "sparse")
        { PreferredTranslationLanguage = "zh-Hans" };

    private sealed class CatalogHandler : HttpMessageHandler
    {
        public int Searches, Lyrics;
        public bool Delay;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var search = request.RequestUri!.AbsolutePath.Contains("search", StringComparison.Ordinal);
            if (search) Interlocked.Increment(ref Searches); else Interlocked.Increment(ref Lyrics);
            if (Delay) await Task.Delay(30, token);
            return new(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(search
                    ? """{"code":200,"result":{"songs":[{"id":1,"name":"Track","artists":[{"name":"Artist"}],"album":{"name":"Album"},"duration":113000}]}}"""
                    : """{"code":200,"lrc":{"lyric":"[00:01]Hello my friend"},"tlyric":{"lyric":"[00:01]你好我的朋友"}}""", Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        public TimeSpan Now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Now.Ticks;
    }

    private sealed class PayloadHandler(string payload) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { RequestMessage = request, Content = new StringContent(payload, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class RetiringHandler : HttpMessageHandler
    {
        public int Calls;
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var old = Interlocked.Increment(ref Calls) == 1;
            if (old) { Started.TrySetResult(); await Release.Task; }
            return new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(new { code = 200, result = new { songs = new[] { new { id = 1, name = old ? "old" : "new" } } } }),
                Encoding.UTF8, "application/json") };
        }
    }
}
