using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class LyricsRequestHealthTests
{
    [TestMethod]
    public async Task RepeatedOutageIsIsolatedBySourceAndStageAndExplicitRefreshRecovers()
    {
        var clock = new Clock();
        var fail = true;
        using var handler = new Handler(request => Response(fail && request.RequestUri!.Host == "lrclib.net" &&
            request.RequestUri.AbsolutePath.Contains("search", StringComparison.Ordinal) ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK,
            """[{"id":1,"trackName":"Song","artistName":"Artist","duration":180,"syncedLyrics":"[00:01]We sing together"}]"""));
        using var client = new HttpClient(handler);
        var http = new LyricsHttpClient(client, timeProvider: clock);
        for (var index = 0; index < 2; index++) await Assert.ThrowsAsync<HttpRequestException>(async () =>
        { using var ignored = await http.GetAsync("https://lrclib.net/api/search?q=Song", default); });
        fail = false;
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        { using var ignored = await http.GetAsync("https://lrclib.net/api/search?q=Other", default); });
        Assert.AreEqual(2, handler.Calls);
        using var lyric = await http.GetAsync("https://lrclib.net/api/get?id=1", default);
        using var other = await http.GetAsync("https://api.amll.dev/api/search?q=Song", default);
        Assert.AreEqual(4, handler.Calls);
        var service = new LyricsService(new LyricsProviderRegistry(http, () => ""));
        var result = await service.QueryDetailedAsync(new("Song", "Artist", "", TimeSpan.FromSeconds(180)),
            new() { Provider = LyricsProviderKind.Lrclib, BackupProvider = null, SearchRemainingProviders = false }, default, refresh: true);
        Assert.AreEqual(LyricsQueryStatus.Found, result.Status);
        Assert.IsTrue(handler.Calls > 4);

        fail = true;
        for (var index = 0; index < 2; index++) await Assert.ThrowsAsync<HttpRequestException>(async () =>
        { using var ignored = await http.GetAsync("https://lrclib.net/api/search?q=Song", default); });
        fail = false;
        clock.Advance(TimeSpan.FromSeconds(2));
        using var recovered = await http.GetAsync("https://lrclib.net/api/search?q=Song", default);
    }

    [TestMethod]
    public async Task LongRetryAfterDateIsHonoredAndRefreshCannotBypassRateLimit()
    {
        var clock = new Clock();
        var rateLimited = true;
        using var handler = new Handler(_ =>
        {
            var response = Response(rateLimited ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK, "[]");
            if (rateLimited) response.Headers.RetryAfter = new RetryConditionHeaderValue(clock.GetUtcNow().AddHours(1));
            return response;
        });
        using var client = new HttpClient(handler);
        var http = new LyricsHttpClient(client, timeProvider: clock);
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        { using var ignored = await http.GetAsync("https://lrclib.net/api/search?q=Song", default); });
        rateLimited = false;
        http.RetryTransientFailures(LyricsProviderKind.Lrclib);
        clock.Advance(TimeSpan.FromSeconds(3599));
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        { using var ignored = await http.GetAsync("https://lrclib.net/api/search?q=Song", default); });
        Assert.AreEqual(1, handler.Calls);
        clock.Advance(TimeSpan.FromSeconds(1));
        using var recovered = await http.GetAsync("https://lrclib.net/api/search?q=Song", default);
        Assert.AreEqual(2, handler.Calls);
    }

    [TestMethod]
    public async Task EmptyResultsAndCallerCancellationNeverOpenOutageCircuit()
    {
        using var cancellation = new CancellationTokenSource();
        var cancel = true;
        using var handler = new Handler(_ =>
        {
            if (cancel) { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); }
            return Response(HttpStatusCode.OK, "[]");
        });
        using var client = new HttpClient(handler);
        var http = new LyricsHttpClient(client);
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        { using var ignored = await http.GetAsync("https://lrclib.net/api/search?q=Song", cancellation.Token); });
        cancel = false;
        for (var index = 0; index < 4; index++)
        { using var empty = await http.GetAsync("https://lrclib.net/api/search?q=Song", default); Assert.AreEqual(0, empty.RootElement.GetArrayLength()); }
        Assert.AreEqual(5, handler.Calls);
    }

    [TestMethod]
    public async Task EarlierConcurrentSuccessCannotClearNewerOutageCooldown()
    {
        var held = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new ConcurrentHandler(held.Task);
        using var client = new HttpClient(handler);
        var http = new LyricsHttpClient(client);
        var earlier = http.GetAsync("https://lrclib.net/api/search?q=Earlier", default);
        try
        {
            for (var index = 0; index < 2; index++) await Assert.ThrowsAsync<HttpRequestException>(async () =>
            { using var ignored = await http.GetAsync("https://lrclib.net/api/search?q=Newer", default); });
        }
        finally { held.TrySetResult(Response(HttpStatusCode.OK, "[]")); }
        using var completed = await earlier;
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        { using var ignored = await http.GetAsync("https://lrclib.net/api/search?q=StillBlocked", default); });
        Assert.AreEqual(3, handler.Calls);
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string content) => new(status)
    { Content = new StringContent(content, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; var response = respond(request); response.RequestMessage = request; return Task.FromResult(response); }
    }

    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);
        public void Advance(TimeSpan duration) => _ticks += duration.Ticks;
    }

    private sealed class ConcurrentHandler(Task<HttpResponseMessage> first) : HttpMessageHandler
    {
        internal int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = Interlocked.Increment(ref Calls) == 1 ? await first : Response(HttpStatusCode.ServiceUnavailable, "{}");
            response.RequestMessage = request;
            return response;
        }
    }
}
