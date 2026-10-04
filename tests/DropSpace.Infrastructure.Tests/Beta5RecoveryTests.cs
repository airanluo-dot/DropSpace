using System.Net;
using System.Text;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;
using DropSpace.Infrastructure.Settings;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class Beta5RecoveryTests
{
    [TestMethod]
    public async Task NullLyricsSettingsPreserveOtherPreferencesWithoutCrashing()
    {
        var paths = new AppStoragePaths(Path.Combine(Path.GetTempPath(), "DropSpace-beta5", Guid.NewGuid().ToString("N")));
        try
        {
            paths.EnsureCreated();
            await File.WriteAllTextAsync(paths.Settings, """{"Lyrics":null,"ClipboardPaused":true,"StartWithWindows":false}""");
            var store = new JsonSettingsService(paths);
            var settings = await store.LoadAsync();
            Assert.IsTrue(settings.ClipboardPaused);
            Assert.IsFalse(settings.StartWithWindows);
            Assert.IsFalse(settings.Lyrics.Enabled);
            Assert.IsFalse(settings.Lyrics.SearchRemainingProviders);
            Assert.IsFalse(store.LastLoadRecovery.Recovered);
        }
        finally { if (Directory.Exists(paths.Root)) Directory.Delete(paths.Root, true); }
    }

    [TestMethod]
    public async Task LrclibInvalidFirstIdentifierDoesNotHideValidCandidate()
    {
        using var handler = new Handler(_ => Json("""[{"id":"","trackName":"Song","artistName":"Artist","syncedLyrics":"[00:01]broken"},{"id":2,"trackName":"Song","artistName":"Artist","syncedLyrics":"[00:01]valid"}]"""));
        using var client = new HttpClient(handler);
        var document = await new LrclibLyricsProvider(new(client)).QueryAsync(new("Song", "Artist", "", TimeSpan.Zero), default);
        Assert.AreEqual("2", document.Match?.CandidateId);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AmllBrokenFirstCandidateDoesNotHideValidCandidate(bool malformedXml)
    {
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("search", StringComparison.Ordinal)
            ? Json("""{"data":{"items":[{"id":"one","musicNames":["Song"],"artistNames":["Artist"]},{"id":"two","musicNames":["Song"],"artistNames":["Artist"]}]}}""")
            : request.RequestUri.Query.Contains("id=one", StringComparison.Ordinal)
                ? malformedXml ? Json("""{"data":{"lyrics":"<tt><body>"}}""") : new(HttpStatusCode.ServiceUnavailable)
            : Json("""{"data":{"lyrics":"[00:01]valid"}}"""));
        using var client = new HttpClient(handler);
        var document = await new AmllLyricsProvider(new(client)).QueryAsync(new("Song", "Artist", "", TimeSpan.Zero), default);
        Assert.AreEqual("two", document.Match?.CandidateId);
    }

    [TestMethod]
    public async Task KugouBrokenFirstCandidateDoesNotHideValidCandidate()
    {
        var lyricReads = 0;
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath.EndsWith("search", StringComparison.Ordinal)
            ? Json("""{"candidates":[{"id":"one","accesskey":"key","song":"Song","singer":"Artist"},{"id":"two","accesskey":"key","song":"Song","singer":"Artist"}]}""")
            : ++lyricReads == 1 ? new(HttpStatusCode.ServiceUnavailable)
            : Json("{\"content\":\"" + Convert.ToBase64String(Encoding.UTF8.GetBytes("[00:01]valid")) + "\"}"));
        using var client = new HttpClient(handler);
        var document = await new KugouLyricsProvider(new(client)).QueryAsync(new("Song", "Artist", "", TimeSpan.Zero), default);
        Assert.AreEqual("two", document.Match?.CandidateId);
        Assert.AreEqual(2, lyricReads);
    }

    private static HttpResponseMessage Json(string payload) => new(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = respond(request); response.RequestMessage = request; return Task.FromResult(response);
        }
    }
}
