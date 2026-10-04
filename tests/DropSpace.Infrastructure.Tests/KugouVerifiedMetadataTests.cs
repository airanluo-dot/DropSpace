using System.Net;
using System.Text;
using System.Text.Json;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class KugouVerifiedMetadataTests
{
    private const string CatalogTitle = "风的来信 A Letter From the Wind";
    private static LyricsQuery Query => new("风的来信 (feat. 孙晔) [中文版]", "HOYO-MiX", "", TimeSpan.FromSeconds(196), "apple-wind");

    [TestMethod]
    public async Task HashBoundMissingDurationSurvivesProviderServiceAndCache()
    {
        var downloads = 0;
        using var client = new HttpClient(new Handler(r =>
        {
            if (r.RequestUri!.Host == "songsearch.kugou.com") return Catalog();
            if (r.RequestUri.AbsolutePath == "/download") { downloads++; return Body(); }
            return r.RequestUri.Query.Contains("hash=") ? Candidate(CatalogTitle, "HOYO-MiX", 0) : Empty();
        }));
        var service = new LyricsService(new([new KugouLyricsProvider(new(client))]));
        var settings = new LyricsSettings { Enabled = true, Provider = LyricsProviderKind.Kugou, SearchRemainingProviders = false };
        var result = await service.QueryDetailedAsync(Query, settings, default);
        Assert.AreEqual(LyricsQueryStatus.Found, result.Status);
        Assert.AreEqual(197d, result.Document.Match!.DurationSeconds);
        Assert.AreEqual("814788332", result.Document.Match.CandidateId);
        Assert.AreEqual(2, result.Document.Lines.Count);
        Assert.AreEqual(LyricsQueryStatus.Found, (await service.QueryDetailedAsync(Query, settings, default)).Status);
        Assert.AreEqual(1, downloads);
    }

    [TestMethod]
    [DataRow("Other song", "HOYO-MiX", 0)]
    [DataRow(CatalogTitle, "Other artist", 0)]
    [DataRow(CatalogTitle, "HOYO-MiX", 310000)]
    [DataRow("风的来信 [英文版]", "HOYO-MiX", 0)]
    public async Task HashDoesNotEraseConflictingLyricMetadata(string title, string artist, int duration)
    {
        var downloads=0;
        using var client=new HttpClient(new Handler(r =>
        {
            if(r.RequestUri!.Host=="songsearch.kugou.com") return Catalog();
            if(r.RequestUri.AbsolutePath=="/download") { downloads++; return Body(); }
            return r.RequestUri.Query.Contains("hash=") ? Candidate(title,artist,duration) : Empty();
        }));
        var result=await new KugouLyricsProvider(new(client)).QueryAsync(Query,default);
        Assert.AreEqual(0,result.Lines.Count);
        Assert.AreEqual(0,downloads);
    }

    [TestMethod]
    public async Task EmptyFirstCatalogHashDoesNotHideSecondRecording()
    {
        var hashes=new List<string>();
        using var client=new HttpClient(new Handler(r =>
        {
            if(r.RequestUri!.Host=="songsearch.kugou.com") return Catalog(two:true);
            if(r.RequestUri.AbsolutePath=="/download") return Body();
            if(r.RequestUri.Query.Contains("hash="))
            {
                hashes.Add(r.RequestUri.Query);
                return r.RequestUri.Query.Contains("second") ? Candidate(CatalogTitle,"HOYO-MiX",197000) : Empty();
            }
            return Empty();
        }));
        var result=await new KugouLyricsProvider(new(client)).QueryAsync(Query,default);
        Assert.AreEqual(2,result.Lines.Count);
        Assert.AreEqual(2,hashes.Count);
    }

    private static HttpResponseMessage Catalog(bool two=false) => Json(JsonSerializer.Serialize(new { data=new { lists=(two?new[]{"first","second"}:new[]{"first"}).Select(hash=>new { SongName=CatalogTitle,SingerName="HOYO-MiX、孙晔",AlbumName="原神-风的来信 A Letter From The Wind",Duration=197,FileHash=hash }) } }));
    private static HttpResponseMessage Candidate(string title,string artist,int duration) => Json(JsonSerializer.Serialize(new { candidates=new[]{new { id="814788332",accesskey="fixture-key",song=title,singer=artist,duration }} }));
    private static HttpResponseMessage Empty()=>Json("{\"candidates\":[]}");
    private static HttpResponseMessage Body()=>Json(JsonSerializer.Serialize(new { content=Convert.ToBase64String(Encoding.UTF8.GetBytes("[00:01.00]原创测试第一句\n[00:02.00]原创测试第二句")) }));
    private static HttpResponseMessage Json(string s)=>new(HttpStatusCode.OK){ Content=new StringContent(s,Encoding.UTF8,"application/json") };
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> respond):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken token)
        { token.ThrowIfCancellationRequested(); var response=respond(r); response.RequestMessage=r; return Task.FromResult(response); }
    }
}
