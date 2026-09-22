using System.Net;
using System.Text;
using DropSpace.Core.Lyrics;
using DropSpace.Infrastructure.Lyrics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class LyricsProviderTransportTests
{
    [TestMethod]
    public async Task UnknownDurationUsesLrclibSearchInsteadOfInvalidExactRequest()
    {
        using var handler = new FixtureHandler(request =>
        {
            Assert.AreEqual("/api/search", request.RequestUri!.AbsolutePath);
            return Json("""[{"id":1,"trackName":"Song","artistName":"Artist","syncedLyrics":"[00:01]line"}]""");
        });
        using var client = new HttpClient(handler);
        var result = await new LrclibLyricsProvider(new(client)).QueryAsync(new("Song", "Artist", "", TimeSpan.Zero), default);
        Assert.AreEqual(1, result.Lines.Count);
    }

    [TestMethod]
    public async Task LrclibBadRequestAndIdentityMismatchBothContinueToSearch()
    {
        foreach (var mismatch in new[] { false, true })
        {
            using var handler = new FixtureHandler(request => request.RequestUri!.AbsolutePath == "/api/get"
                ? mismatch ? Json("""{"id":1,"trackName":"Other","artistName":"Artist","albumName":"Album","duration":180,"syncedLyrics":"[00:01]wrong"}""") : new(HttpStatusCode.BadRequest)
                : Json("""[{"id":2,"trackName":"Song","artistName":"Artist","albumName":"Album","duration":180,"syncedLyrics":"[00:01]correct"}]"""));
            using var client = new HttpClient(handler);
            var result = await new LrclibLyricsProvider(new(client)).QueryAsync(new("Song", "Artist", "Album", TimeSpan.FromSeconds(180)), default);
            Assert.AreEqual("correct", result.Lines.Single().Text);
        }
    }

    [TestMethod]
    public async Task LrclibNeverFabricatesIdentityFromRequestedMetadata()
    {
        using var handler = new FixtureHandler(request => request.RequestUri!.AbsolutePath == "/api/get"
            ? Json("""{"id":1,"syncedLyrics":"[00:01]wrong"}""") : Json("[]"));
        using var client = new HttpClient(handler);
        var result = await new LrclibLyricsProvider(new(client)).QueryAsync(new("Song", "Artist", "Album", TimeSpan.FromSeconds(180)), default);
        Assert.IsEmpty(result.Lines);
    }

    [TestMethod]
    public async Task LrclibSkipsEmptyLyricsBeforeRankingCandidates()
    {
        using var handler = new FixtureHandler(_ => Json("""[{"id":1,"trackName":"Song","artistName":"Artist"},{"id":2,"trackName":"Song","artistName":"Artist","syncedLyrics":"[00:01]correct"}]"""));
        using var client = new HttpClient(handler);
        var result = await new LrclibLyricsProvider(new(client)).QueryAsync(new("Song", "Artist", "", TimeSpan.Zero), default);
        Assert.AreEqual("2", result.Match!.CandidateId);
    }

    [TestMethod]
    public async Task LrclibFallsBackAcrossPublisherArtistSemantics()
    {
        var searches = new List<string>();
        using var handler = new FixtureHandler(request =>
        {
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            searches.Add(query);
            return query.Contains("Catalogue Artist", StringComparison.Ordinal)
                ? Json("""[{"id":2,"trackName":"Song","artistName":"Catalogue Artist","syncedLyrics":"[00:01]correct"}]""")
                : Json("[]");
        });
        using var client = new HttpClient(handler);
        var result = await new LrclibLyricsProvider(new(client)).QueryAsync(
            new("Song", "Displayed Performer", "", TimeSpan.Zero, AlbumArtist: "Catalogue Artist"), default);

        Assert.AreEqual("correct", result.Lines.Single().Text);
        Assert.HasCount(2, searches);
        Assert.IsTrue(searches[0].Contains("Displayed Performer", StringComparison.Ordinal));
        Assert.IsTrue(searches[1].Contains("Catalogue Artist", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task KugouHashSearchKeepsSecondsAndVerifiedAlbumIdentity()
    {
        using var handler = new FixtureHandler(request => request.RequestUri!.Host == "songsearch.kugou.com"
            ? Json("""{"data":{"lists":[{"SongName":"Song","SingerName":"Artist","AlbumName":"Album","Duration":180,"FileHash":"hash"}]}}""")
            : request.RequestUri.AbsolutePath == "/download"
                ? Json("{\"content\":\"" + Convert.ToBase64String(Encoding.UTF8.GetBytes("[00:01]correct")) + "\"}")
                : request.RequestUri.Query.Contains("hash=", StringComparison.Ordinal)
                    ? Json("""{"candidates":[{"id":"id","accesskey":"key","song":"Song","singer":"Artist","duration":180000}]}""")
                    : Json("""{"candidates":[]}"""));
        using var client = new HttpClient(handler);
        var result = await new KugouLyricsProvider(new(client)).QueryAsync(new("Song", "Artist", "Album", TimeSpan.FromSeconds(180)), default);
        Assert.AreEqual("correct", result.Lines.Single().Text);
        Assert.AreEqual("Album", result.Match!.Album);
        Assert.AreEqual(180d, result.Match.DurationSeconds);
    }

    [TestMethod]
    public async Task NetEaseSearchIncludesArtistToAvoidPopularCoverCrowding()
    {
        var queries = new List<string>();
        using var handler = new FixtureHandler(request =>
        {
            queries.Add(Uri.UnescapeDataString(request.RequestUri!.Query));
            Assert.IsTrue(request.RequestUri.Query.Contains("limit=30", StringComparison.Ordinal));
            return Json("""{"result":{"songs":[]}}""");
        });
        using var client = new HttpClient(handler);
        await new NetEaseLyricsProvider(new(client)).QueryAsync(new("Song", "Artist", "", TimeSpan.Zero), default);
        Assert.IsTrue(queries[0].Contains("Song Artist", StringComparison.Ordinal));
        Assert.IsTrue(queries[1].Contains("s=Song", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task NetEaseApplicationErrorIsNotMisclassifiedAsCatalogMiss()
    {
        var calls = 0;
        using var handler = new FixtureHandler(_ =>
        {
            calls++;
            return Json("""{"code":405,"message":"bounded upstream error"}""");
        });
        using var client = new HttpClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => new NetEaseLyricsProvider(new(client))
            .QueryAsync(new("Song", "Artist", "", TimeSpan.Zero), default));
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task NetEaseFallsBackToTitleSearchAndReadsModernAliasSchema()
    {
        var searches = new List<string>();
        using var handler = new FixtureHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("search", StringComparison.Ordinal))
            {
                var terms = Uri.UnescapeDataString(request.RequestUri.Query);
                searches.Add(terms);
                return terms.Contains("Song Artist", StringComparison.Ordinal)
                    ? Json("""{"result":{"songs":[]}}""")
                    : Json("""{"songs":[{"id":7,"name":"歌曲","tns":["Song"],"ar":[{"name":"Artist"}],"al":{"name":"Album"},"dt":181000}]}""");
            }
            return Json("""{"lrc":{"lyric":"[00:01]correct"}}""");
        });
        using var client = new HttpClient(handler);
        var result = await new NetEaseLyricsProvider(new(client)).QueryAsync(
            new("Song", "Artist", "Different release", TimeSpan.FromSeconds(180)), default);

        Assert.AreEqual("correct", result.Lines.Single().Text);
        Assert.AreEqual("Song", result.Match!.Title);
        Assert.AreEqual("7", result.Match.CandidateId);
        Assert.AreEqual(2, searches.Count);
    }

    [TestMethod]
    public async Task NetEaseSkipsEmptyBestCandidateWithinBoundedAlternatives()
    {
        var lyricIds = new List<string>();
        using var handler = new FixtureHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("search", StringComparison.Ordinal))
                return Json("""{"result":{"songs":[{"id":1,"name":"Song","artists":[{"name":"Artist"}],"album":{"name":"Album"},"duration":180000},{"id":2,"name":"Song (2024 Remastered)","artists":[{"name":"Artist"}],"album":{"name":"Album Deluxe"},"duration":181000}]}}""");
            lyricIds.Add(request.RequestUri.Query);
            return request.RequestUri.Query.Contains("id=1", StringComparison.Ordinal)
                ? Json("""{"lrc":{"lyric":""}}""")
                : Json("""{"lrc":{"lyric":"[00:01]usable"}}""");
        });
        using var client = new HttpClient(handler);
        var result = await new NetEaseLyricsProvider(new(client)).QueryAsync(
            new("Song", "Artist", "Album", TimeSpan.FromSeconds(180)), default);

        Assert.AreEqual("usable", result.Lines.Single().Text);
        Assert.AreEqual("2", result.Match!.CandidateId);
        Assert.AreEqual(2, lyricIds.Count);
    }

    [TestMethod]
    public async Task SameOriginRedirectPreservesHeadersAndParsesJsonp()
    {
        var calls = 0;
        using var handler = new FixtureHandler(request =>
        {
            Assert.AreEqual("https://y.qq.com/", request.Headers.Referrer!.AbsoluteUri);
            Assert.IsTrue(request.Headers.UserAgent.Count > 0);
            if (++calls == 1)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new("/new", UriKind.Relative);
                return redirect;
            }
            Assert.AreEqual("/new", request.RequestUri!.AbsolutePath);
            return Json("callback({\"code\":0});");
        });
        using var client = new HttpClient(handler);
        using var result = await new LyricsHttpClient(client).GetAsync("https://c.y.qq.com/old", default, "https://y.qq.com/");
        Assert.AreEqual(0, result.RootElement.GetProperty("code").GetInt32());
    }

    [TestMethod]
    public async Task RedirectCannotLeakMetadataToAnotherHostOrDowngradeTls()
    {
        foreach (var destination in new[] { "https://example.com/", "https://lrclib.net/", "http://c.y.qq.com/", "https://c.y.qq.com:8443/", "https://user@c.y.qq.com/" })
        {
            var calls = 0;
            using var handler = new FixtureHandler(_ =>
            {
                calls++;
                var redirect = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
                redirect.Headers.Location = new(destination);
                return redirect;
            });
            using var client = new HttpClient(handler);
            await Assert.ThrowsAsync<InvalidDataException>(() => new LyricsHttpClient(client).GetAsync("https://c.y.qq.com/old", default));
            Assert.AreEqual(1, calls);
        }
    }

    [TestMethod]
    public async Task RedirectLoopIsBounded()
    {
        var calls = 0;
        using var handler = new FixtureHandler(_ =>
        {
            calls++;
            var redirect = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
            redirect.Headers.Location = new("/loop", UriKind.Relative);
            return redirect;
        });
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => new LyricsHttpClient(client).GetAsync("https://c.y.qq.com/loop", default));
        Assert.AreEqual(4, calls);
    }

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private sealed class FixtureHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var response = respond(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
