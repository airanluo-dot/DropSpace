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
