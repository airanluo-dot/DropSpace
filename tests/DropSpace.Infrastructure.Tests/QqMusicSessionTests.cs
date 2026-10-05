using System.Net;
using System.Text;
using System.Text.Json;
using DropSpace.Core.Lyrics;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class QqMusicSessionTests
{
    private string _directory = "";
    [TestInitialize]
    public void Initialize()
    {
        if (!OperatingSystem.IsWindows()) Assert.Inconclusive("Windows user encryption is required.");
        _directory = Path.Combine(Path.GetTempPath(), "DropSpace-QqSession-" + Guid.NewGuid().ToString("N"));
    }
    [TestCleanup]
    public void Cleanup() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    private static QqMusicCookie[] Cookies() =>
    [
        new() { Name = "qqmusic_key", Value = "fixture-music-ticket", Domain = ".y.qq.com", Expires = DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeSeconds() },
        new() { Name = "uin", Value = "o123456789", Domain = ".qq.com" },
        new() { Name = "skey", Value = "unrelated-qq-account-ticket", Domain = ".qq.com" },
        new() { Name = "qqmusic_key", Value = "untrusted-domain-ticket", Domain = ".example.com" },
    ];

    [TestMethod]
    public async Task SavedSessionSurvivesReloadWithoutPlaintextOrCrossSourceCredentials()
    {
        var session = new QqMusicSession(_directory);
        await session.SaveAsync(Cookies());
        var stored = await File.ReadAllBytesAsync(Path.Combine(_directory, "session.bin"));
        Assert.IsFalse(Encoding.UTF8.GetString(stored).Contains("fixture-music-ticket", StringComparison.Ordinal));
        var restored = new QqMusicSession(_directory);
        await restored.LoadAsync();
        Assert.AreEqual(QqMusicSessionState.Saved, restored.State);
        Assert.AreEqual(2, restored.GetBrowserCookies().Length);
        Assert.AreEqual("123456789", restored.Identity().Uin);
        Assert.IsTrue(restored.CookieHeader(new("https://u.y.qq.com/cgi-bin/musicu.fcg")).Contains("fixture-music-ticket", StringComparison.Ordinal));
        foreach (var host in new[] { "music.163.com", "lyrics.kugou.com", "lrclib.net", "api.amll.dev", "graph.qq.com", "u.y.qq.com.example.com" })
            Assert.AreEqual("", restored.CookieHeader(new("https://" + host + "/")));
        Assert.AreEqual("", restored.CookieHeader(new("http://u.y.qq.com/")));
        Assert.AreEqual("", restored.CookieHeader(new("https://u.y.qq.com:8443/")));
        await restored.SaveAsync([new() { Name = "qqmusic_key", Value = "host-only-ticket", Domain = "y.qq.com" }]);
        Assert.AreEqual("", restored.CookieHeader(new("https://u.y.qq.com/")));
    }

    [TestMethod]
    public async Task ServerExpiryAndSignOutDoNotPermitLateSessionResurrection()
    {
        var session = new QqMusicSession(_directory);
        await session.SaveAsync(Cookies());
        await session.ApplyResponseCookiesAsync(new("https://u.y.qq.com/"),
            ["qqmusic_key=expired; Domain=.y.qq.com; Path=/; Expires=Thu, 01 Jan 1970 00:00:00 GMT"], session.Generation, default);
        Assert.AreEqual(QqMusicSessionState.Expired, session.State);
        Assert.Throws<LyricsProviderRejectedException>(session.EnsureUsable);
        await session.SaveAsync(Cookies());
        var oldGeneration = session.Generation;
        await session.SignOutAsync();
        await session.ApplyResponseCookiesAsync(new("https://u.y.qq.com/"),
            ["qqmusic_key=late-ticket; Domain=.y.qq.com; Path=/"], oldGeneration, default);
        session.ReportAccess(oldGeneration, true);
        Assert.AreEqual(QqMusicSessionState.SignedOut, session.State);
        Assert.IsFalse(File.Exists(Path.Combine(_directory, "session.bin")));
    }

    [TestMethod]
    public async Task ActualProviderUsesSavedMusicSessionAndKeepsNativeTranslation()
    {
        var session = new QqMusicSession(_directory);
        await session.SaveAsync(Cookies());
        using var client = new HttpClient(new Handler(async request =>
        {
            Assert.IsTrue(request.Headers.GetValues("Cookie").Single().Contains("fixture-music-ticket", StringComparison.Ordinal));
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.AreEqual("123456789", payload.RootElement.GetProperty("comm").GetProperty("uin").GetString());
            Assert.AreEqual(session.Identity().Gtk, payload.RootElement.GetProperty("comm").GetProperty("g_tk").GetInt32());
            var requestBody = payload.RootElement.GetProperty("req_1");
            if (requestBody.GetProperty("method").GetString() == "DoSearchForQQMusicDesktop")
            {
                return """{"code":0,"req_1":{"code":0,"data":{"body":{"song":{"list":[{"mid":"one","name":"Song","singer":[{"name":"Artist"}],"interval":180}]}}}}}""";
            }
            Assert.AreEqual("GetPlayLyricInfo", requestBody.GetProperty("method").GetString());
            Assert.AreEqual("one", requestBody.GetProperty("param").GetProperty("songMID").GetString());
            Assert.AreEqual(1, requestBody.GetProperty("param").GetProperty("trans").GetInt32());
            return JsonSerializer.Serialize(new { code = 0, req_1 = new { code = 0, data = new {
                lyric = Convert.ToBase64String(Encoding.UTF8.GetBytes("[00:00]Written by: Fixture\n[00:01]We keep on walking together")),
                trans = Convert.ToBase64String(Encoding.UTF8.GetBytes("[00:00]//\n[00:01]我们继续一起走下去")) } } });
        }));
        var result = await new QqMusicLyricsProvider(new(client, qqMusicSession: session)).QueryAsync(
            new("Song", "Artist", "", TimeSpan.FromSeconds(180)) { PreferredTranslationLanguage = "zh-Hans" }, default);
        Assert.IsNull(result.Lines[0].Secondary);
        Assert.AreEqual("我们继续一起走下去", result.Lines[1].Secondary);
        Assert.AreEqual(QqMusicSessionState.Connected, session.State);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<string>> callback) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            new(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(await callback(request), Encoding.UTF8, "application/json") };
    }
}
