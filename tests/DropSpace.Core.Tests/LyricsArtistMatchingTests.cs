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
}
