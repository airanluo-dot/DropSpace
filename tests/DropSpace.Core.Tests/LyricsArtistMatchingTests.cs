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
    public void FeaturedArtistAndRemasterDecorationsDoNotHideTheSameSong()
    {
        var query = new LyricsQuery("Midnight Drive (feat. Guest)", "Artist feat. Guest", "Original Album", TimeSpan.FromSeconds(240));

        Assert.IsGreaterThan(4, LyricsMatcher.Score(query, "Midnight Drive - 2024 Remastered", "Artist; Guest", "Deluxe Reissue", 258));
        Assert.AreEqual("Midnight Drive", LyricsMatcher.SearchTitle("Midnight Drive (feat. Guest) - 网易云音乐"));
        Assert.AreEqual("With Arms Wide Open", LyricsMatcher.SearchTitle("With Arms Wide Open"));
        Assert.AreEqual("Clean", LyricsMatcher.SearchTitle("Clean"));
    }

    [TestMethod]
    public void MinorTitleTypoCanUseStrongArtistAndDurationEvidence()
    {
        var query = new LyricsQuery("Beautiful Night", "Artist", string.Empty, TimeSpan.FromSeconds(200));

        Assert.IsGreaterThan(4, LyricsMatcher.Score(query, "Beautful Night", "Artist", string.Empty, 201));
    }

    [TestMethod]
    public void ArtistCreditSeparatorsAreCompatibleWithoutSubstringMatching()
    {
        foreach (var artist in new[] { "Artist / Guest", "Artist; Guest", "Artist、Guest", "Artist feat. Guest", "Artist x Guest" })
            Assert.IsTrue(LyricsMatcher.AreArtistCreditsCompatible("Artist", artist), artist);
        Assert.IsTrue(LyricsMatcher.AreArtistCreditsCompatible("周杰伦", "周杰伦/温岚"));
        Assert.IsFalse(LyricsMatcher.AreArtistCreditsCompatible("AC", "AC/DC"));
        Assert.IsFalse(LyricsMatcher.AreArtistCreditsCompatible("Artist / One", "Artist / Two"));
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
    public void ConflictingReleaseAlbumIsSoftWhenTitleAndArtistAreStrong()
    {
        var query = new LyricsQuery("Song", "Artist", "Album A", TimeSpan.Zero);

        Assert.IsGreaterThan(4, LyricsMatcher.Score(query, "Song", "Artist", "Album B", 0));
    }

    [TestMethod]
    public void MissingProviderAlbumIsSoftWhenArtistAndDurationAreStrong()
    {
        var query = new LyricsQuery("Song", "Artist", "Album A", TimeSpan.FromSeconds(180));

        Assert.IsGreaterThan(4, LyricsMatcher.Score(query, "Song", "Artist", string.Empty, 180));
    }

    [TestMethod]
    public void AlbumOrDurationCannotRescueAConflictingArtistOrVersion()
    {
        var query = new LyricsQuery("Song", "Artist", "Album", TimeSpan.FromSeconds(180));

        Assert.AreEqual(0, LyricsMatcher.Score(query, "Song", "Other", "Album", 180));
        Assert.AreEqual(0, LyricsMatcher.Score(query, "Song (Live)", "Artist", "Album", 180));
        Assert.AreEqual(0, LyricsMatcher.Score(query, "Song", "Artist", "Album", 240));
    }
}
