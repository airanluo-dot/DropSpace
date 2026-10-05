using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class KugouKrcAdapterTests
{
    private const string Search = """{"status":200,"candidates":[{"id":"id","accesskey":"key","song":"Song","singer":"Artist","duration":180000}]}""";
    private static readonly LyricsQuery Query = new("Song", "Artist", "", TimeSpan.FromSeconds(180));

    [TestMethod]
    public async Task KrcUsesRelativeWordsAndCompleteTranslationWithoutAnotherDownload()
    {
        var languages = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { content = new[] {
            new { type = 0, lyricContent = new[] { new[] { "roman", "words" } } },
            new { type = 1, lyricContent = new[] { new[] { "我们在这里等你" } } },
        } }));
        var packed = Pack("[language:" + languages + "]\n[1000,2000]<0,800,0>The night <900,1000,0>is full of stars.");
        var downloads = 0;
        using var handler = new Handler(request => request.RequestUri!.AbsolutePath == "/search" ? Json(Search) : Download(request));
        HttpResponseMessage Download(HttpRequestMessage request)
        {
            downloads++;
            Assert.IsTrue(request.RequestUri!.Query.Contains("fmt=krc", StringComparison.Ordinal));
            return Json(JsonSerializer.Serialize(new { status = 200, content = packed }));
        }
        using var client = new HttpClient(handler);
        var document = await new KugouLyricsProvider(new(client)).QueryAsync(Query with { PreferredTranslationLanguage = "zh-Hans" }, default);
        Assert.AreEqual(1, downloads);
        var line = document.Lines.Single();
        Assert.AreEqual("我们在这里等你", line.Secondary);
        Assert.AreEqual(LyricsTranslationOrigin.Provider, line.TranslationOrigin);
        Assert.AreEqual(TimeSpan.FromMilliseconds(1900), line.Words[1].Start);
        Assert.AreEqual(TimeSpan.FromMilliseconds(2900), line.Words[1].End);
        Assert.IsTrue(LyricsTranslationPolicy.HasMatchingProviderTranslation(document, "zh-Hans"));
    }

    [TestMethod]
    public async Task MalformedOrInflationBombKrcFallsBackInsideExistingDownloadBudget()
    {
        foreach (var packed in new[] { "not-base64", Pack(new string('x', 200_000)) })
        {
            var downloads = 0;
            using var handler = new Handler(request => request.RequestUri!.AbsolutePath == "/search" ? Json(Search) : Download(request));
            HttpResponseMessage Download(HttpRequestMessage request)
            {
                downloads++;
                return Json(JsonSerializer.Serialize(new { status = 200, content = request.RequestUri!.Query.Contains("fmt=krc", StringComparison.Ordinal)
                    ? packed : Convert.ToBase64String(Encoding.UTF8.GetBytes("[00:01]original survives")) }));
            }
            using var client = new HttpClient(handler);
            var result = await new KugouLyricsProvider(new(client)).QueryAsync(Query, default);
            Assert.AreEqual("original survives", result.Lines.Single().Text);
            Assert.AreEqual(2, downloads);
        }
    }

    [TestMethod]
    public async Task BusinessRejectionAndMalformedSearchDoNotProbeMoreRequests()
    {
        foreach (var payload in new[] { """{"status":405,"candidates":[]}""", """{"status":200,"opaque":"denied"}""" })
        {
            var calls = 0;
            using var handler = new Handler(_ => { calls++; return Json(payload); });
            using var client = new HttpClient(handler);
            await Assert.ThrowsAsync<Exception>(async () => await new KugouLyricsProvider(new(client)).QueryAsync(Query, default));
            Assert.AreEqual(1, calls);
        }
        var downloads = 0;
        using var rejected = new Handler(request => request.RequestUri!.AbsolutePath == "/search" ? Json(Search) : Reject());
        HttpResponseMessage Reject() { downloads++; return Json("""{"status":405,"content":""}"""); }
        using var rejectedClient = new HttpClient(rejected);
        await Assert.ThrowsAsync<HttpRequestException>(async () => await new KugouLyricsProvider(new(rejectedClient)).QueryAsync(Query, default));
        Assert.AreEqual(1, downloads);
    }

    private static string Pack(string text)
    {
        using var output = new MemoryStream();
        output.Write("krc1"u8);
        using (var zipped = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            zipped.Write(Encoding.UTF8.GetBytes(text));
        var bytes = output.ToArray();
        byte[] key = [64, 71, 97, 119, 94, 50, 116, 71, 81, 54, 49, 45, 206, 210, 110, 105];
        for (var index = 4; index < bytes.Length; index++) bytes[index] ^= key[(index - 4) % key.Length];
        return Convert.ToBase64String(bytes);
    }
    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
        { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = respond(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
