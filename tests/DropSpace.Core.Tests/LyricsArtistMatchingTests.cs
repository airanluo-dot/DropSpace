using DropSpace.Core.Lyrics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class LyricsArtistMatchingTests
{
    [TestMethod]
    public void GeneralTitleOrthographyPreservesRecordingIdentityGuards()
    {
        var query = new LyricsQuery("小幸运", "田馥甄", "", TimeSpan.FromSeconds(260));
        Assert.IsTrue(LyricsMatcher.AreTitlesEquivalent(query.Title, "小幸運"));
        Assert.IsGreaterThan(4, LyricsMatcher.Score(query, "小幸運", "田馥甄", "", 260));
        Assert.IsTrue(LyricsMatcher.AreTitlesEquivalent("爱与诚", "愛與誠"));
        Assert.IsTrue(LyricsMatcher.AreTitlesEquivalent("爱与诚 [国语版]", "愛與誠 [國語版]"));
        Assert.IsFalse(LyricsMatcher.AreTitlesEquivalent("爱与诚 [国语版]", "愛與誠 [粵語版]"));
        Assert.AreEqual(0d, LyricsMatcher.Score(query, "小幸運 (Live)", "田馥甄", "", 260));
        Assert.AreEqual(0d, LyricsMatcher.Score(query, "小幸運", "Other Artist", "", 260));
        Assert.AreEqual(0d, LyricsMatcher.Score(query, "小幸運", "田馥甄", "", 100));
        Assert.IsFalse(LyricsMatcher.AreTitlesEquivalent("乾杯", "干杯")); // Ambiguous dictionary mapping stays distinct.
    }

    [TestMethod]
    public void GeneralArtistOrthographyAndSourceAliasesPreserveIdentityConflicts()
    {
        foreach (var pair in new[] { ("邓紫棋", "鄧紫棋"), ("张学友", "張學友"), ("陈奕迅", "陳奕迅"), ("郑秀文", "鄭秀文") })
            Assert.IsTrue(LyricsMatcher.AreArtistCreditsCompatible(pair.Item1, pair.Item2));
        var query = new LyricsQuery("唯一", "邓紫棋", "T.I.M.E.", TimeSpan.FromSeconds(253));
        Assert.AreEqual(0d, LyricsMatcher.Score(query, "唯一", "G.E.M.邓紫棋", "T.I.M.E.", 253));
        Assert.IsGreaterThan(4, LyricsMatcher.Score(query, "唯一", "G.E.M.邓紫棋", "T.I.M.E.", 253, ["鄧紫棋"]));
        foreach (var artist in new[] { "告五人", "邓紫棋乐队", "Other 邓紫棋", "G.E.M.", "AC/DC" })
            Assert.AreEqual(0d, LyricsMatcher.Score(query, "唯一", artist, "T.I.M.E.", 253), artist);
        Assert.AreEqual(0d, LyricsMatcher.Score(query, "唯一 (Live)", "G.E.M.", "T.I.M.E.", 253, ["邓紫棋"]));
        Assert.AreEqual(0d, LyricsMatcher.Score(query, "唯一", "G.E.M.", "T.I.M.E.", 100, ["邓紫棋"]));
        Assert.IsFalse(LyricsMatcher.AreArtistCreditsCompatible("AC", "AC/DC"));
    }

    [TestMethod]
    public void FeaturedArtistNameDoesNotBecomeAVersionLabel()
    {
        var query = new LyricsQuery("Night Drive (feat. Oliver)", "Artist", "", TimeSpan.FromSeconds(200));
        Assert.IsGreaterThan(4, LyricsMatcher.Score(query, "Night Drive", "Artist", "", 200));
        Assert.IsTrue(LyricsMatcher.AreTitlesEquivalent(query.Title, "Night Drive"));
    }

    [TestMethod]
    public void VersionWordsStillRejectActualLiveAndRemixRecordings()
    {
        var query = new LyricsQuery("Night Drive", "Artist", "", TimeSpan.FromSeconds(200));
        Assert.AreEqual(0d, LyricsMatcher.Score(query, "Night Drive (Live at Wembley)", "Artist", "", 200));
        Assert.AreEqual(0d, LyricsMatcher.Score(query, "Night Drive (Extended Remix)", "Artist", "", 200));
    }

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

    [TestMethod]
    public void AlbumArtistIsAPlayerAgnosticAlternativeCredit()
    {
        var query = new LyricsQuery("Song", "Featured Performer", "Album", TimeSpan.FromSeconds(180),
            AlbumArtist: "Catalogue Artist");

        Assert.IsGreaterThan(4, LyricsMatcher.Score(query, "Song", "Catalogue Artist", "Album", 180));
        CollectionAssert.Contains(LyricsMatcher.SearchTerms(query).ToArray(), "Song Catalogue Artist");
    }

    [TestMethod]
    public void DisplayReadyPublisherCreditAddsAPlainArtistCandidate()
    {
        var query = new LyricsQuery(
            "Never Gonna Give You Up",
            "Rick Astley — Whenever You Need Somebody",
            string.Empty,
            TimeSpan.FromSeconds(213),
            AlbumArtist: "Rick Astley — Whenever You Need Somebody");

        CollectionAssert.AreEqual(
            new[] { "Rick Astley — Whenever You Need Somebody", "Rick Astley" },
            query.ArtistCandidates.ToArray());
        CollectionAssert.AreEqual(
            new[]
            {
                "Never Gonna Give You Up Rick Astley — Whenever You Need Somebody",
                "Never Gonna Give You Up Rick Astley",
                "Never Gonna Give You Up",
            },
            LyricsMatcher.SearchTerms(query).ToArray());
        Assert.IsGreaterThan(4, LyricsMatcher.Score(
            query,
            "Never Gonna Give You Up",
            "Rick Astley",
            "Whenever You Need Somebody",
            213));
    }

}
