using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class LyricsCacheIdentityRegressionTests
{
    [TestMethod]
    public async Task AlbumArtistCorrectionQueriesAgainWithoutAnExplicitTrackIdentity()
    {
        var provider = new AlbumArtistProvider();
        var service = new LyricsService(new([provider]));
        var original = new LyricsQuery("Shared title", "", "", TimeSpan.Zero,
            AlbumArtist: "First artist");
        var corrected = original with { AlbumArtist = "Second artist" };

        var first = await service.QueryDetailedAsync(original, new() { Enabled = true }, default);
        var second = await service.QueryDetailedAsync(corrected, new() { Enabled = true }, default);

        Assert.AreEqual(LyricsQueryStatus.Found, first.Status);
        Assert.AreEqual(LyricsQueryStatus.Found, second.Status);
        Assert.AreEqual("Second artist", second.Document.Match!.Artist);
        Assert.AreEqual(2, provider.Calls);
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
