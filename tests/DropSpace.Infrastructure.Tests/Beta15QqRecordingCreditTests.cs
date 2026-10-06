using System.Net;
using System.Text;
using System.Text.Json;
using DropSpace.Core.Lyrics;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class Beta15QqRecordingCreditTests
{
    private static LyricsQuery Wind(string singer, string language) => new($"风的来信 (feat. {singer}) [{language}版]",
        "HOYO-MiX — 原神 - 风的来信 (《原神》六周年主题曲) - EP", "", TimeSpan.FromSeconds(197), "wind-test");

    [TestMethod]
    public async Task SameIdEarlyVocalProofCompletesChineseIdentityWithoutReplacingRawArtist()
    {
        using var handler = new FixtureHandler(1, _ => "[00:02.61]演唱 Artist：孙晔Gary\n[00:15.00]用于验证的歌词");
        using var client = new HttpClient(handler);
        var document = await new QqMusicLyricsProvider(new(client)).QueryAsync(Wind("孙晔", "中文"), default);
        Assert.AreEqual("one", document.Match!.CandidateId);
        Assert.AreEqual("HOYO-MiX", document.Match.Artist);
        Assert.AreEqual("HOYO-MiX; 孙晔Gary", document.Match.ArtistAliases.Single());
        Assert.IsTrue(LyricsMatcher.Score(Wind("孙晔", "中文"), document.Match.Title, document.Match.Artist,
            document.Match.Album, document.Match.DurationSeconds, document.Match.ArtistAliases) >= 4);
        Assert.AreEqual(1, handler.LyricReads);
    }

    [TestMethod]
    public async Task ChineseResponseCannotPublishForEnglishVersionAndNextCorrectRecordingSurvives()
    {
        using var handler = new FixtureHandler(2, id => id == "one"
            ? "[00:02.61]演唱 Artist：孙晔Gary\n[00:15.00]用于验证的歌词"
            : "[00:02.61]演唱 Artist：Griffin Burns\n[00:15.00]A fixture lyric");
        using var client = new HttpClient(handler);
        var reported = new List<LyricsDocument>();
        var document = await new QqMusicLyricsProvider(new(client)).QueryAsync(Wind("Griffin Burns", "英文"), default, reported.Add);
        Assert.AreEqual("two", document.Match!.CandidateId);
        Assert.AreEqual(1, reported.Count);
        Assert.AreEqual("two", reported[0].Match!.CandidateId);
        Assert.AreEqual(2, handler.LyricReads);
    }

    [TestMethod]
    [DataRow("[00:15.00]A fixture lyric")]
    [DataRow("[00:15.00]A fixture lyric\n[00:20.00]演唱 Artist：Griffin Burns")]
    [DataRow("[00:02.61]作曲 Composer：Griffin Burns\n[00:15.00]A fixture lyric")]
    public async Task MissingLateOrNonVocalCreditIsNeverRecordingEvidence(string body)
    {
        using var handler = new FixtureHandler(1, _ => body);
        using var client = new HttpClient(handler);
        var reported = new List<LyricsDocument>();
        var document = await new QqMusicLyricsProvider(new(client)).QueryAsync(Wind("Griffin Burns", "英文"), default, reported.Add);
        Assert.IsEmpty(document.Lines);
        Assert.IsEmpty(reported);
        Assert.AreEqual(1, handler.LyricReads);
    }

    private sealed class FixtureHandler(int candidateCount, Func<string, string> lyric) : HttpMessageHandler
    {
        public int LyricReads { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var call = body.RootElement.GetProperty("req_1");
            object response;
            if (call.GetProperty("module").GetString() == "music.search.SearchCgiService")
                response = new { code = 0, req_1 = new { code = 0, data = new { body = new { song = new { list =
                    Enumerable.Range(0, candidateCount).Select(index => new { mid = index == 0 ? "one" : "two",
                        name = "风的来信 A Letter From the Wind", singer = new[] { new { name = "HOYO-MiX" } },
                        album = new { name = "原神 - 风的来信" }, interval = 197 }).ToArray() } } } } };
            else
            {
                LyricReads++;
                response = new { code = 0, req_1 = new { code = 0, data = new { lyric = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(lyric(call.GetProperty("param").GetProperty("songMID").GetString()!))), trans = "" } } };
            }
            return new(HttpStatusCode.OK) { RequestMessage = request,
                Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json") };
        }
    }
}
