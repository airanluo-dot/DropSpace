using DropSpace.Core.Lyrics;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class LyricsChineseDecorationTests
{
    [TestMethod]
    public void SoundtrackDescriptionDoesNotHideOneCharacterTitle()
    {
        const string decorated = "零(《时光代理人 第三季》Part1片尾曲)";
        var query = new LyricsQuery(decorated, "饭卡", "", TimeSpan.FromSeconds(185));
        Assert.AreEqual("零", LyricsMatcher.SearchTitle(decorated));
        Assert.IsGreaterThan(4d, LyricsMatcher.Score(query, "零", "饭卡", "", 185));
        Assert.AreEqual(0d, LyricsMatcher.Score(query, "零", "其他歌手", "", 185));
    }

    [TestMethod]
    public void FeaturedCreditBeforeLanguageSuffixCanMatchCanonicalTitle()
    {
        const string decorated = "风的来信 (feat. 孙晔) [中文版]";
        var query = new LyricsQuery(decorated, "HOYO-MiX", "", TimeSpan.FromSeconds(197));
        Assert.AreEqual("风的来信", LyricsMatcher.SearchTitle(decorated));
        Assert.AreEqual("风的来信", LyricsMatcher.SearchTitle("风的来信(feat.孙晔)[中文版]"));
        Assert.IsGreaterThan(4d, LyricsMatcher.Score(query, "风的来信", "HOYO-MiX; 孙晔", "", 197));
        Assert.IsTrue(LyricsMatcher.HasVersionConflict(decorated, "风的来信 [英文版]"));
        Assert.IsTrue(LyricsMatcher.HasVersionConflict(decorated, "风的来信 (Live)"));
    }

    [TestMethod]
    public void BilingualCatalogTitleRequiresArtistAndDurationCorroboration()
    {
        var query = new LyricsQuery("风的来信 (feat. 孙晔) [中文版]", "HOYO-MiX", "", TimeSpan.FromSeconds(197));
        Assert.IsGreaterThan(4d, LyricsMatcher.Score(query, "风的来信 A Letter From the Wind", "HOYO-MiX; 孙晔", "", 197));
        Assert.AreEqual(0d, LyricsMatcher.Score(query, "A Letter From the Wind", "HOYO-MiX; Griffin Burns", "", 197));
        Assert.AreEqual(0d, LyricsMatcher.Score(query, "风的来信 A Letter From the Wind", "其他歌手", "", 197));
        Assert.AreEqual(0d, LyricsMatcher.Score(query, "风的来信 A Letter From the Wind", "HOYO-MiX; 孙晔", "", 276));
        Assert.AreEqual(0d, LyricsMatcher.Score(query with { Duration=TimeSpan.Zero }, "风的来信 A Letter From the Wind", "HOYO-MiX; 孙晔", "", 0));
    }

    [TestMethod]
    public void UnrelatedParentheticalTitleContentIsPreserved()
    {
        Assert.AreEqual("如果(你也想我)", LyricsMatcher.SearchTitle("如果(你也想我)"));
        Assert.AreEqual("Song (Live)", LyricsMatcher.SearchTitle("Song (Live)"));
        Assert.IsFalse(LyricsMatcher.AreTitlesEquivalent("Song (Live)", "Song"));
        Assert.IsTrue(LyricsMatcher.HasVersionConflict("歌 [日文版]", "歌 [中文版]"));
    }
}
