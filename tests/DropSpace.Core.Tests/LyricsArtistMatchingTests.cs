using DropSpace.Core.Lyrics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class LyricsArtistMatchingTests
{
    [TestMethod]
    public void FirstArtistFromSmtcPrefersOriginalOverShorterLiveCredits()
    {
        var query = new LyricsQuery("Color Your Night", "Lotus Juice", string.Empty, TimeSpan.Zero);
        var original = LyricsMatcher.Score(query, query.Title, "Lotus Juice; 高橋あず美; アトラスサウンドチーム; ATLUS GAME MUSIC", "", 227);
        var live = LyricsMatcher.Score(query, "Color Your Night (Live at KT Zepp Yokohama 2024.6.8 Yoru Koen)", "高橋あず美; Lotus Juice", "", 230);
        Assert.IsGreaterThan(live, original);
    }

    [TestMethod]
    public void SharedGivenNameIsNotAnExactArtistCredit()
    {
        var query = new LyricsQuery("Song", "Alice Johnson", "", TimeSpan.Zero);
        var correct = LyricsMatcher.Score(query, "Song", "Alice Johnson; Guest", "", 0);
        var wrong = LyricsMatcher.Score(query, "Song", "Alice Cooper; Guest", "", 0);
        Assert.IsGreaterThan(wrong, correct);
    }

    [TestMethod]
    public void TitleOnlyMetadataCannotAuthorizeALyricsCandidate()
    {
        var query = new LyricsQuery("Song", string.Empty, string.Empty, TimeSpan.Zero);

        Assert.AreEqual(0, LyricsMatcher.Score(query, "Song", string.Empty, string.Empty, 0));
    }

    [TestMethod]
    public void CandidateWithoutIdentityCannotAuthorizeAnAlbumDisambiguatedQuery()
    {
        var query = new LyricsQuery("Song", string.Empty, "Album", TimeSpan.Zero);

        Assert.AreEqual(0, LyricsMatcher.Score(query, "Song", string.Empty, string.Empty, 0));
    }

    [TestMethod]
    public void LiveCandidateIsRejectedForAPlainTitle()
    {
        var query = new LyricsQuery("Song", "Artist", string.Empty, TimeSpan.Zero);

        Assert.AreEqual(0, LyricsMatcher.Score(query, "Song (Live)", "Artist", string.Empty, 0));
    }

    [TestMethod]
    public void CandidateWithAnIncompatibleKnownDurationIsRejected()
    {
        var query = new LyricsQuery("Song", "Artist", string.Empty, TimeSpan.FromSeconds(180));

        Assert.AreEqual(0, LyricsMatcher.Score(query, "Song", "Artist", string.Empty, 240));
    }

    [TestMethod]
    public void CandidateWithAConflictingKnownAlbumIsRejected()
    {
        var query = new LyricsQuery("Song", "Artist", "Album A", TimeSpan.Zero);

        Assert.AreEqual(0, LyricsMatcher.Score(query, "Song", "Artist", "Album B", 0));
    }

    [TestMethod]
    public void CandidateWithoutAlbumIdentityCannotAuthorizeAnAlbumDisambiguatedQuery()
    {
        var query = new LyricsQuery("Song", "Artist", "Album A", TimeSpan.FromSeconds(180));

        Assert.AreEqual(0, LyricsMatcher.Score(query, "Song", "Artist", string.Empty, 180));
    }
}
