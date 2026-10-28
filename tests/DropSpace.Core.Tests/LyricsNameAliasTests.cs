using System.Text.Json;
using DropSpace.Core.Lyrics;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class LyricsNameAliasTests
{
    [TestMethod]
    public void MappedLookupKeepsOriginalIdentityAndThreeTermBudget()
    {
        var query = new LyricsQuery("Blue and White Porcelain", "Jay Chou", "Original Album", TimeSpan.FromSeconds(238), "original-track");
        var original = query with { };
        CollectionAssert.AreEqual(new[] { "青花瓷 周杰伦", "Blue and White Porcelain Jay Chou", "Blue and White Porcelain" },
            LyricsMatcher.SearchTerms(query).ToArray());
        Assert.AreEqual(original, query);
        CollectionAssert.AreEqual(new[] { "Unlisted Song 周杰伦", "Unlisted Song Jay Chou", "Unlisted Song" },
            LyricsMatcher.SearchTerms(query with { Title = "Unlisted Song" }).ToArray());
        Assert.IsFalse(LyricsMatcher.SearchTerms(query with { Artist = "Jay Chou Tribute" }).Any(term => term.Contains("周杰伦", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void PinnedTitlesMatchBidirectionallyWithArtistAndDurationEvidence()
    {
        foreach (var (english, chinese) in new[]
        {
            ("Qi-Li-Xiang", "七里香"), ("Blue and White Porcelain", "青花瓷"),
            ("All the Way North", "一路向北"), ("Nunchucks", "双截棍"),
        })
        {
            var query = new LyricsQuery(english, "Jay Chou", "", TimeSpan.FromSeconds(240));
            Assert.IsGreaterThan(4d, LyricsMatcher.Score(query, chinese, "周杰倫", "", 241), english);
            Assert.IsGreaterThan(4d, LyricsMatcher.Score(query with { Title = chinese, Artist = "周杰伦" }, english, "Jay Chou", "", 241), english);
            Assert.IsFalse(LyricsMatcher.AreTitlesEquivalent(english, chinese), "Title-only comparison has no artist-scoped alias authority.");
        }
    }

    [TestMethod]
    public void AliasCannotRescueArtistVersionLanguageOrDurationConflicts()
    {
        var query = new LyricsQuery("Sunny Day", "Jay Chou", "", TimeSpan.FromSeconds(269))
        { CollectSelectionCandidates = true };
        foreach (var (title, artist, duration) in new[]
        {
            ("晴天", "Other Artist", 269d), ("晴天", "周杰伦; Guest", 269d),
            ("晴天 (Live)", "周杰伦", 269d), ("晴天 (Remix)", "周杰伦", 269d),
            ("晴天 [粤语版]", "周杰伦", 269d), ("晴天", "周杰伦", 340d),
            ("晴天", "周杰伦", 0d), ("晴天", "周杰伦", double.NaN),
        })
        {
            Assert.AreEqual(0d, LyricsMatcher.Score(query, title, artist, "", duration), $"{title}/{artist}/{duration}");
            Assert.AreEqual(0d, LyricsMatcher.CandidateScore(query, title, artist, "", duration), "Collection must not weaken the mapped-title gate.");
        }
        Assert.AreEqual(0d, LyricsMatcher.Score(query with { Duration = TimeSpan.Zero }, "晴天", "周杰伦", "", 269));
        Assert.AreEqual(0d, LyricsMatcher.Score(query with { Artist = "Other Artist" }, "晴天", "Other Artist", "", 269));
        Assert.AreEqual(0d, LyricsMatcher.Score(query with { Title = "Sunny Day [国语版]" }, "晴天", "周杰伦", "", 269));
    }

    [TestMethod]
    public void ArtistAliasPreservesEveryCollaborationCredit()
    {
        var query = new LyricsQuery("Song", "Jay Chou; Guest", "", TimeSpan.FromSeconds(200));
        Assert.IsGreaterThan(4d, LyricsMatcher.Score(query, "Song", "Guest; 周杰伦", "", 200));
        Assert.AreEqual(0d, LyricsMatcher.Score(query, "Song", "周杰伦", "", 200));
        Assert.AreEqual(0d, LyricsMatcher.Score(query, "Song", "周杰伦; Other", "", 200));
        Assert.IsFalse(LyricsMatcher.AreArtistCreditsCompatible("Jay Chou", "Jay Chou Tribute"));
        Assert.IsFalse(LyricsMatcher.AreArtistCreditsCompatible("Jay", "Jay Chou"));
    }

    [TestMethod]
    public void FeaturedTitleMappingsExpandLookupWithoutInventingCreditEquivalence()
    {
        var query = new LyricsQuery("Shouldn't Be (feat. a-mei)", "Jay Chou", "", TimeSpan.FromSeconds(250));
        Assert.AreEqual("不该 周杰伦", LyricsMatcher.SearchTerms(query)[0]);
        Assert.AreEqual(0d, LyricsMatcher.Score(query, "不该（with 张惠妹）", "周杰伦", "", 250));
        Assert.AreEqual(0d, LyricsMatcher.Score(query, "不该", "周杰伦", "", 250));
        Assert.AreEqual(0d, LyricsMatcher.Score(query with { Title = "Waiting For You" }, "等你下课", "周杰伦", "", 250));
    }

    [TestMethod]
    public void CatalogueContainsOnlyPinnedNameDataAndDoesNotAuthorizeFuzzyTitles()
    {
        using var stream = typeof(LyricsMatcher).Assembly.GetManifestResourceStream("DropSpace.Core.Lyrics.Data.RecordingNameAliases.json");
        Assert.IsNotNull(stream);
        using var document = JsonDocument.Parse(stream);
        CollectionAssert.AreEquivalent(new[] { "ver", "singers" }, document.RootElement.EnumerateObject().Select(value => value.Name).ToArray());
        var singers = document.RootElement.GetProperty("singers");
        Assert.AreEqual(1, singers.EnumerateObject().Count());
        var singer = singers.GetProperty("Jay Chou");
        CollectionAssert.AreEquivalent(new[] { "name", "songs" }, singer.EnumerateObject().Select(value => value.Name).ToArray());
        Assert.AreEqual("周杰伦", singer.GetProperty("name").GetString());
        Assert.AreEqual(153, singer.GetProperty("songs").EnumerateObject().Count());
        var query = new LyricsQuery("Sunny Day", "Jay Chou", "", TimeSpan.FromSeconds(269));
        Assert.AreEqual(0d, LyricsMatcher.Score(query with { Title = "Suny Day" }, "晴天", "周杰伦", "", 269));
        Assert.AreEqual(0d, LyricsMatcher.Score(query, "晴天里", "周杰伦", "", 269));
        Assert.AreEqual(0d, LyricsMatcher.Score(query with { Title = new string('x', 4096) }, "晴天", "周杰伦", "", 269));
    }
}
