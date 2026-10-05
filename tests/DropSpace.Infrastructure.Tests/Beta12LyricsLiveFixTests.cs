using System.Net;
using System.Text;
using System.Text.Json;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class Beta12LyricsLiveFixTests
{
    [TestMethod]
    public async Task TranslationUpgradeIsPublishedBeforeUnrelatedProviderFinishes()
    {
        var query = new LyricsQuery("Song", "Artist", "", TimeSpan.FromSeconds(180)) { PreferredTranslationLanguage = "zh-Hans" };
        var held = new TaskCompletionSource<LyricsDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        var preview = new TaskCompletionSource<LyricsDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        var original = new LyricsDocument([new(TimeSpan.Zero, TimeSpan.FromSeconds(10), "We keep on walking together", null, [])], LyricsProviderKind.NetEase)
            .Bind(query, "Song", "Artist", "", 180, 12, "original");
        var translated = original with { Provider = LyricsProviderKind.Kugou, Lines = [original.Lines[0] with
            { Secondary = "我们继续一起走下去", TranslationOrigin = LyricsTranslationOrigin.Provider, TranslationLanguage = "zh-Hans", TranslationLanguageIsExplicit = true }] };
        var service = new LyricsService(new([new Stub(LyricsProviderKind.NetEase, Task.FromResult(original)),
            new Stub(LyricsProviderKind.Kugou, Task.FromResult(translated)), new Stub(LyricsProviderKind.QqMusic, held.Task)]));
        var pending = service.QueryDetailedAsync(query, new() { Enabled = true, SearchRemainingProviders = true }, default,
            reportOriginal: doc => { if (doc.Lines.Any(line => line.Secondary is not null)) preview.TrySetResult(doc); });
        try
        {
            Assert.AreEqual(LyricsProviderKind.Kugou, (await preview.Task.WaitAsync(TimeSpan.FromSeconds(1))).Provider);
            Assert.IsFalse(pending.IsCompleted);
        }
        finally { held.TrySetResult(LyricsDocument.Empty); }
        Assert.AreEqual(LyricsProviderKind.Kugou, (await pending).Document.Provider);
    }

    [TestMethod]
    public async Task SearchCooldownDoesNotSuppressKnownLyricRead()
    {
        var calls = 0;
        using var handler = new Handler(request => { calls++; return Json(request.RequestUri!.AbsolutePath.Contains("search", StringComparison.Ordinal)
            ? """{"code":405}""" : """{"code":200,"lrc":{"lyric":"[00:01]original"}}"""); });
        using var client = new HttpClient(handler);
        var http = new LyricsHttpClient(client);
        using var rejected = await http.GetAsync("https://music.163.com/api/search/get/web?s=Song", default);
        using var lyric = await http.GetAsync("https://music.163.com/api/song/lyric?id=1", default);
        Assert.AreEqual(200, lyric.RootElement.GetProperty("code").GetInt32());
        await Assert.ThrowsAsync<HttpRequestException>(async () => { using var ignored = await http.GetAsync("https://music.163.com/api/search/get/web?s=Other", default); });
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task QqCurrentEnvelopeKeepsFullCreditsAndNativeTranslation()
    {
        using var handler = new Handler(request =>
        {
            if (request.Content!.ReadAsStringAsync().GetAwaiter().GetResult().Contains("DoSearchForQQMusicDesktop", StringComparison.Ordinal))
            {
                Assert.AreEqual(HttpMethod.Post, request.Method);
                return Json("""{"code":0,"req_1":{"code":0,"data":{"meta":{"ret":0,"is_filter":0},"body":{"song":{"list":[{"mid":"one","name":"Song","singer":[{"name":"Artist"},{"name":"Guest"}],"album":{"name":"Album"},"interval":180}]}}}}}""");
            }
            return Json("""{"code":0,"lyric":"[00:01]We keep on walking together","trans":"[00:01]我们继续一起走下去"}""");
        });
        using var client = new HttpClient(handler);
        var result = await new QqMusicLyricsProvider(new(client)).QueryAsync(new("Song", "Artist / Guest", "Album", TimeSpan.FromSeconds(180))
            { PreferredTranslationLanguage = "zh-Hans" }, default);
        Assert.AreEqual("one", result.Match?.CandidateId);
        Assert.AreEqual("Artist; Guest", result.Match?.Artist);
        Assert.AreEqual("我们继续一起走下去", result.Lines.Single().Secondary);
    }

    [TestMethod]
    public void QqFilteredSuccessEnvelopeIsNotGenuineNoMatch()
    {
        using var payload = JsonDocument.Parse("""{"code":0,"req_1":{"code":0,"data":{"meta":{"ret":0,"is_filter":-2},"body":{"song":{"list":[]}}}}}""");
        Assert.Throws<LyricsProviderRejectedException>(() => QqMusicLyricsProvider.SearchSongs(payload.RootElement));
    }

    [TestMethod]
    public async Task KugouSeparateVersionSuffixSelectsTheCorrectRecording()
    {
        using var handler = new Handler(request =>
        {
            if (request.RequestUri!.Host == "songsearch.kugou.com")
                return Json("""{"status":1,"error_code":0,"data":{"lists":[{"SongName":"Shake It Off","SingerName":"Taylor Swift","Duration":219,"FileHash":"original"},{"SongName":"Shake It Off","Suffix":"(Taylor's Version)","SingerName":"Taylor Swift","Duration":219,"FileHash":"rerecording"}]}}""");
            if (request.RequestUri.AbsolutePath == "/download")
                return Json(JsonSerializer.Serialize(new { status = 200, errcode = 200, content = Convert.ToBase64String(Encoding.UTF8.GetBytes("[00:01]We keep on walking together")) }));
            if (request.RequestUri.Query.Contains("hash=", StringComparison.Ordinal))
            {
                Assert.IsTrue(request.RequestUri.Query.Contains("hash=rerecording", StringComparison.Ordinal));
                return Json("""{"status":200,"errcode":200,"candidates":[{"id":"correct","accesskey":"fixture","song":"Shake It Off (Taylor's Version)","singer":"Taylor Swift","duration":219245}]}""");
            }
            return Json("""{"status":200,"errcode":200,"candidates":[]}""");
        });
        using var client = new HttpClient(handler);
        var result = await new KugouLyricsProvider(new(client)).QueryAsync(new("Shake It Off (Taylor's Version)", "Taylor Swift", "", TimeSpan.FromSeconds(219)), default);
        Assert.AreEqual("correct", result.Match?.CandidateId);
        Assert.IsNotEmpty(result.Lines);
    }

    private static HttpResponseMessage Json(string payload) => new(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { var response = respond(request); response.RequestMessage = request; return Task.FromResult(response); }
    }
    private sealed class Stub(LyricsProviderKind kind, Task<LyricsDocument> result) : ILyricsProvider
    {
        public LyricsProviderKind Kind => kind;
        public Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken token) => result;
    }
}
