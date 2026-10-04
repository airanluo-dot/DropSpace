using System.Text.Json;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class LyricsCacheIdentityRegressionTests
{
    [TestMethod]
    public async Task RepeatPlaybackAndRestartUsePersistentSourceWithoutProviderSearch()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var provider = new AlbumArtistProvider();
            var query = new LyricsQuery("Persistent", "Artist", "Album", TimeSpan.FromSeconds(30), "stable-track", "Artist");
            var settings = new LyricsSettings { Enabled = true };
            var first = new LyricsService(new([provider]), new LyricsCache(root));
            Assert.AreEqual(LyricsQueryStatus.Found, (await first.QueryDetailedAsync(query, settings, default)).Status);
            Assert.AreEqual(LyricsQueryStatus.Found, (await first.QueryDetailedAsync(query, settings, default)).Status);
            var restarted = new LyricsService(new([provider]), new LyricsCache(root));
            Assert.AreEqual(LyricsQueryStatus.Found, (await restarted.QueryDetailedAsync(query, settings, default)).Status);
            Assert.AreEqual(1, provider.Calls);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task AlbumArtistCorrectionQueriesAgainWithoutAnExplicitTrackIdentity()
    {
        var provider = new AlbumArtistProvider();
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var service = new LyricsService(new([provider]), new LyricsCache(root));
        var original = new LyricsQuery("Shared title", "", "", TimeSpan.Zero,
            AlbumArtist: "First artist");
        var corrected = original with { AlbumArtist = "Second artist" };

        var first = await service.QueryDetailedAsync(original, new() { Enabled = true }, default);
        var second = await service.QueryDetailedAsync(corrected, new() { Enabled = true }, default);

        Assert.AreEqual(LyricsQueryStatus.Found, first.Status);
        Assert.AreEqual(LyricsQueryStatus.Found, second.Status);
        Assert.AreEqual("Second artist", second.Document.Match!.Artist);
        Assert.AreEqual(2, provider.Calls);
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Beta9PrimaryCacheRetainsSameLanguageButRequeriesForeignOriginal(bool foreignOriginal)
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-Beta9Cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            var query = new LyricsQuery("唯一", "邓紫棋", "", TimeSpan.FromSeconds(180), "synthetic-track")
                { PreferredTranslationLanguage = "zh-Hans" };
            var settings = new LyricsSettings { Enabled = true, SearchRemainingProviders = false };
            var cache = new LyricsCache(root);
            var legacyKey = JsonSerializer.Serialize(new
            {
                version = "source-v4", primary = LyricsProviderKind.NetEase, backup = (LyricsProviderKind?)null,
                settings.SearchRemainingProviders, target = "zh-Hans",
                query.TrackIdentity, query.Title, query.Artist, query.AlbumArtist, query.Album,
                durationTicks = query.Duration.Ticks,
            });
            LyricsDocument Document(string text) => new LyricsDocument(
                [new(TimeSpan.Zero, TimeSpan.FromSeconds(10), text, null, [])], LyricsProviderKind.NetEase)
                { ProviderDataRevision = NetEaseLyricsProvider.DataRevision }
                .Bind(query, query.Title, query.Artist, query.Album, 180, 12, "synthetic-id");
            await cache.WriteDocumentAsync(legacyKey,
                Document(foreignOriginal ? "The night is full of stars." : "我们在这里等你"), cache.Generation, default);
            var provider = new SameLanguageProvider(Document("我们在这里等你"));
            var result = await new LyricsService(new([provider]), cache).QueryDetailedAsync(query, settings, default);
            Assert.AreEqual(LyricsQueryStatus.Found, result.Status);
            Assert.AreEqual("我们在这里等你", result.Document.Lines[0].Text);
            Assert.AreEqual(foreignOriginal ? 1 : 0, provider.Calls);
            Assert.IsFalse(result.TranslationLookupIncomplete);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class SameLanguageProvider(LyricsDocument document) : ILyricsProvider
    {
        public LyricsProviderKind Kind => LyricsProviderKind.NetEase;
        public int Calls;
        public Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(document);
        }
    }

    private sealed class AlbumArtistProvider : ILyricsProvider
    {
        public LyricsProviderKind Kind => LyricsProviderKind.NetEase;
        public int Calls { get; private set; }

        public Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new LyricsDocument(
                [new(TimeSpan.Zero, TimeSpan.FromSeconds(10), query.AlbumArtist, null, [])], Kind)
                .Bind(query, query.Title, query.AlbumArtist, query.Album, 0,
                    LyricsMatcher.Score(query, query.Title, query.AlbumArtist, query.Album, 0),
                    query.AlbumArtist));
        }
    }
}
