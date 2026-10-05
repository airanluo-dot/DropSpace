using System.Net;
using System.Text;
using System.Text.Json;
using DropSpace.Core.Lyrics;
using DropSpace.Infrastructure.Lyrics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class AmllContractTests
{
    [TestMethod]
    public async Task LrclibRejectsOpaqueWrapperButKeepsLegitimateEmptyArray()
    {
        var calls = 0;
        using var handler = new Handler(_ => { calls++; return Json("{\"message\":\"unavailable\"}"); });
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await new LrclibLyricsProvider(new(client)).QueryAsync(
            Query with { Album = "", Duration = TimeSpan.Zero }, default));
        Assert.AreEqual(1, calls);
        using var empty = new Handler(_ => Json("[]"));
        using var emptyClient = new HttpClient(empty);
        var result = await new LrclibLyricsProvider(new(emptyClient)).QueryAsync(Query with { Album = "", Duration = TimeSpan.Zero }, default);
        Assert.HasCount(0, result.Lines);
    }
    [TestMethod]
    public async Task HttpDiagnosticsDistinguishBusinessRejectionFromSuccessfulTransport()
    {
        foreach (var (url, payload, code) in new[] {
            ("https://c.y.qq.com/search", "{\"code\":0,\"retcode\":1001}", 1001),
            ("https://lyrics.kugou.com/search", "{\"status\":0,\"error_code\":1002}", 1002),
            ("https://api.amll.dev/v1/lyrics/search", "{\"status\":429}", 429),
        })
        {
            var events = new List<LyricsDiagnostic>();
            using var handler = new Handler(_ => Json(payload));
            using var client = new HttpClient(handler);
            using var result = await new LyricsHttpClient(client, events.Add).GetAsync(url, default);
            var recorded = events.Single();
            Assert.AreEqual(200, recorded.HttpStatus);
            Assert.AreEqual(code, recorded.ApiCode);
            Assert.AreEqual(code == 429 ? LyricsDiagnosticOutcome.RateLimited : LyricsDiagnosticOutcome.Rejected, recorded.Outcome);
        }
    }
    private static readonly LyricsQuery Query = new("Song", "Artist", "Album", TimeSpan.FromSeconds(180), "track-one");
    private const string Ttml = "<tt><body><p begin=\"1s\" end=\"2s\">The night is full of stars.</p></body></tt>";

    [TestMethod]
    public async Task TruncatedCatalogueNarrowsOnceAndImmutableLyricsRebindUntilRefresh()
    {
        var searches = 0;
        var downloads = 0;
        using var handler = new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("search", StringComparison.Ordinal))
            {
                searches++;
                var narrow = request.RequestUri.Query.Contains("artistName=Artist", StringComparison.Ordinal);
                return Catalogue(narrow ? "Artist" : "Other artist", true);
            }
            downloads++;
            return Json(JsonSerializer.Serialize(new { status = 200, data = new { id = 1, format = "ttml", lyrics = Ttml } }));
        });
        using var client = new HttpClient(handler);
        var provider = new AmllLyricsProvider(new(client));
        var first = await provider.QueryAsync(Query, default);
        var second = await provider.QueryAsync(Query with { TrackIdentity = "track-two" }, default);
        Assert.AreEqual(4, searches);
        Assert.AreEqual(1, downloads);
        Assert.AreEqual("track-one", first.Match!.TrackIdentity);
        Assert.AreEqual("track-two", second.Match!.TrackIdentity);
        Assert.AreEqual(0d, second.Match.DurationSeconds, "AMLL has no source duration; ignore uncontracted fixture duration.");
        await provider.QueryAsync(Query with { BypassProviderResponseCache = true }, default);
        Assert.AreEqual(6, searches);
        Assert.AreEqual(2, downloads);
    }

    [TestMethod]
    public async Task RejectedOrMalformedWrapperStopsBeforeNarrowingOrDownload()
    {
        foreach (var payload in new[] { """{"status":429,"error":"Too Many Requests"}""", """{"status":200,"data":{"opaque":true}}""" })
        {
            var calls = 0;
            using var handler = new Handler(_ => { calls++; return Json(payload); });
            using var client = new HttpClient(handler);
            await Assert.ThrowsAsync<Exception>(async () => await new AmllLyricsProvider(new(client)).QueryAsync(Query, default));
            Assert.AreEqual(1, calls);
        }
    }

    [TestMethod]
    public async Task UnknownDurationCannotUseContributorOrAliasToEraseVersionConflict()
    {
        foreach (var (artist, titles) in new[] { ("", new[] { "Song" }), ("Artist", new[] { "Song (Live)", "Song" }), ("Artist", new[] { "Song extra" }) })
        {
            var calls = 0;
            using var handler = new Handler(_ =>
            {
                calls++;
                return Json(JsonSerializer.Serialize(new { status = 200, data = new { items = new[] {
                    new { id = 1, musicNames = titles, artistNames = new[] { artist }, authorUsernames = new[] { "Artist" }, albumNames = new[] { "Album" } }
                }, pagination = new { hasMore = false } } }));
            });
            using var client = new HttpClient(handler);
            var result = await new AmllLyricsProvider(new(client)).QueryAsync(Query, default);
            Assert.HasCount(0, result.Lines);
            Assert.AreEqual(1, calls);
        }
    }

    private static HttpResponseMessage Catalogue(string artist, bool more) => Json(JsonSerializer.Serialize(new {
        status = 200, data = new { items = new[] { new { id = 1, musicNames = new[] { "Song" }, artistNames = new[] { artist },
            albumNames = new[] { "Album" }, duration = 999 } }, pagination = new { hasMore = more } }
    }));
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
        { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); var response = respond(request); response.RequestMessage = request; return Task.FromResult(response); }
    }
}
