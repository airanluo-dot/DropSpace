using System.Net;
using System.Text;
using System.Text.Json;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class LyricsProviderCandidateQualityTests
{
    private static readonly LyricsQuery Query = new("Song", "Artist", "", TimeSpan.FromSeconds(180));
    private const string Plain = "[00:01]The night is full of stars.\n[00:04]We keep walking together.";
    private const string Words = "[00:01]<00:01>The night <00:02>is full of stars.\n[00:04]<00:04>We keep <00:05>walking together.";

    [TestMethod]
    [DataRow(LyricsProviderKind.QqMusic)]
    [DataRow(LyricsProviderKind.NetEase)]
    [DataRow(LyricsProviderKind.Kugou)]
    [DataRow(LyricsProviderKind.Lrclib)]
    [DataRow(LyricsProviderKind.Amll)]
    public async Task RulesPublishesFirstBodyAndChoosesBetterSixthCandidateWithinBudget(LyricsProviderKind kind)
    {
        var searches = 0;
        var downloads = 0;
        var reports = new List<LyricsDocument>();
        using var client = new HttpClient(new Handler(request =>
        {
            var parameters = Parameters(request);
            if (IsSearch(request, parameters))
            {
                searches++;
                return Json(Catalogue(kind, Enumerable.Range(1, 7)));
            }
            downloads++;
            Assert.IsTrue(downloads <= 6, "The seventh lyric must not be downloaded.");
            if (downloads > 1) Assert.IsTrue(reports.Count > 0, "The first body must be published before later downloads.");
            var id = kind == LyricsProviderKind.QqMusic ? parameters.GetProperty("songMID").GetString()! : Parameter(request, "id");
            return Json(Body(kind, id));
        }));
        var provider = Provider(kind, new(client));
        var result = await provider.QueryAsync(Query, default, reports.Add);
        Assert.AreEqual("6", result.Match!.CandidateId);
        Assert.AreEqual("1", reports[0].Match!.CandidateId);
        Assert.AreEqual(6, reports.Count);
        Assert.IsTrue(result.Lines.All(line => line.Words.Count > 0));
        Assert.AreEqual(kind == LyricsProviderKind.Lrclib ? 0 : 6, downloads);
        Assert.AreEqual(1, searches, "A complete compared pool does not need another search term.");
    }

    [TestMethod]
    public async Task LrclibExactBodyStillComparesSearchAndDuplicateIdsDoNotHideLaterCandidates()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler(request =>
        {
            calls++;
            return Json(request.RequestUri!.AbsolutePath == "/api/get"
                ? LrclibItem(1) : new object[] { LrclibItem(1), LrclibItem(1), LrclibItem(2),
                    LrclibItem(3), LrclibItem(4), LrclibItem(5), LrclibItem(6), LrclibItem(7) });
        }));
        var reports = new List<LyricsDocument>();
        var result = await new LrclibLyricsProvider(new(client)).QueryAsync(Query with { Album = "Album" }, default, reports.Add);
        Assert.AreEqual("6", result.Match!.CandidateId);
        Assert.AreEqual(6, reports.Count);
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task LaterRateLimitStopsCollectionWithoutDiscardingProgressiveOriginal()
    {
        var downloads = 0;
        var reports = new List<LyricsDocument>();
        using var client = new HttpClient(new Handler(request =>
        {
            var parameters = Parameters(request);
            if (IsSearch(request, parameters)) return Json(Catalogue(LyricsProviderKind.QqMusic, Enumerable.Range(1, 7)));
            return ++downloads == 1 ? Json(Body(LyricsProviderKind.QqMusic, "1")) : new(HttpStatusCode.TooManyRequests);
        }));
        await Assert.ThrowsAsync<HttpRequestException>(() => new QqMusicLyricsProvider(new(client)).QueryAsync(Query, default, reports.Add));
        Assert.AreEqual(2, downloads);
        Assert.AreEqual("1", reports.Single().Match!.CandidateId);
    }

    private static IProgressiveLyricsProvider Provider(LyricsProviderKind kind, LyricsHttpClient http) => kind switch
    {
        LyricsProviderKind.QqMusic => new QqMusicLyricsProvider(http),
        LyricsProviderKind.NetEase => new NetEaseLyricsProvider(http),
        LyricsProviderKind.Kugou => new KugouLyricsProvider(http),
        LyricsProviderKind.Lrclib => new LrclibLyricsProvider(http),
        LyricsProviderKind.Amll => new AmllLyricsProvider(http),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static object Catalogue(LyricsProviderKind kind, IEnumerable<int> ids) => kind switch
    {
        LyricsProviderKind.QqMusic => new { code = 0, data = new { song = new { list = ids.Select(id => new
            { mid = id.ToString(System.Globalization.CultureInfo.InvariantCulture), name = "Song", singer = new[] { new { name = "Artist" } }, interval = 180 }) } } },
        LyricsProviderKind.NetEase => new { code = 200, result = new { songs = ids.Select(id => new
            { id, name = "Song", artists = new[] { new { name = "Artist" } }, duration = 180000 }) } },
        LyricsProviderKind.Kugou => new { status = 200, candidates = ids.Select(id => new
            { id, accesskey = "fixture-key", song = "Song", singer = "Artist", duration = 180000 }) },
        LyricsProviderKind.Lrclib => ids.Select(LrclibItem).ToArray(),
        LyricsProviderKind.Amll => new { status = 200, data = new { items = ids.Select(id => new
            { id, musicNames = new[] { "Song" }, artistNames = new[] { "Artist" }, albumNames = Array.Empty<string>() }) } },
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static object LrclibItem(int id) => new { id, trackName = "Song", artistName = "Artist", albumName = "Album",
        duration = 180, syncedLyrics = id == 6 ? Words : Plain };

    private static object Body(LyricsProviderKind kind, string id)
    {
        var text = id == "6" ? Words : Plain;
        return kind switch
        {
            LyricsProviderKind.QqMusic => new { code = 0, lyric = text },
            LyricsProviderKind.NetEase => new { code = 200, lrc = new { lyric = text } },
            LyricsProviderKind.Kugou => new { status = 200, content = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) },
            LyricsProviderKind.Amll => new { status = 200, data = new { id, format = "ttml", lyrics = id == "6"
                ? "<tt><body><p begin=\"1s\" end=\"4s\"><span begin=\"0s\" end=\"1s\">The night </span><span begin=\"1s\" end=\"3s\">is full of stars.</span></p><p begin=\"4s\" end=\"7s\"><span begin=\"0s\" end=\"1s\">We keep </span><span begin=\"1s\" end=\"3s\">walking together.</span></p></body></tt>"
                : "<tt><body><p begin=\"1s\" end=\"4s\">The night is full of stars.</p><p begin=\"4s\" end=\"7s\">We keep walking together.</p></body></tt>" } },
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private static JsonElement Parameters(HttpRequestMessage request)
    {
        if (request.Content is null) return default;
        using var body = JsonDocument.Parse(request.Content.ReadAsStringAsync().GetAwaiter().GetResult());
        return body.RootElement.GetProperty("req_1").GetProperty("param").Clone();
    }

    private static bool IsSearch(HttpRequestMessage request, JsonElement parameters) =>
        parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("query", out _) ||
        request.RequestUri!.AbsolutePath.Contains("search", StringComparison.Ordinal);

    private static string Parameter(HttpRequestMessage request, string key) => request.RequestUri!.Query.TrimStart('?')
        .Split('&').Select(value => value.Split('=', 2)).Single(pair => pair[0] == key)[1];
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { token.ThrowIfCancellationRequested(); var result = response(request); result.RequestMessage = request; return Task.FromResult(result); }
    }
}
