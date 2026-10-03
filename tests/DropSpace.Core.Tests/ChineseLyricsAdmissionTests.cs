using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class ChineseLyricsAdmissionTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ChineseContextExcludesShortLinesAndMatchedMetadataOnlyAtAdmission(bool timed)
    {
        var source = ChineseSource(timed);
        Assert.IsEmpty(LyricsLanguagePolicy.EligibleIndices(source, "zh-CN"));
        Assert.IsTrue(LyricsLanguagePolicy.EligibleSegments(source, "zh-CN").All(parts => parts.Length == 0));
        Assert.IsTrue(source.Lines.All(line => line.SourceLanguage is null));
        if (timed)
            Assert.IsFalse(LyricsLanguagePolicy.SourceEvidence(source)[7].IsConfident,
                "Whole-song admission must not change bounded source-language evidence.");
        var isolated = Document("晴");
        Assert.IsFalse(LyricsLanguagePolicy.Identify("晴").IsConfident);
        CollectionAssert.AreEqual(new[] { 0 }, LyricsLanguagePolicy.EligibleIndices(isolated, "zh-CN"));
        Assert.IsNotEmpty(LyricsLanguagePolicy.EligibleIndices(source, "en-US"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RepeatedLinesAndCreditNamesCannotManufactureChineseSongEvidence(bool credits)
    {
        var source = credits ? Document("作词：我们带着蓝色雨伞", "作曲：我的小船停在岸边", "晴")
            : Document("我们带着蓝色雨伞", "我们带着蓝色雨伞", "晴");
        source = source with { Match = new("合成曲", "甲歌手", "合成专辑", 40, 12) };
        CollectionAssert.Contains(LyricsLanguagePolicy.EligibleIndices(source, "zh-CN"), 2);
    }

    [TestMethod]
    public void UnboundChineseFragmentsRetainUnknownShortAndSectionBoundaries()
    {
        var source = ChineseSource(true);
        source = source with { Lines = source.Lines.Skip(4).ToArray(), Match = null };
        CollectionAssert.Contains(LyricsLanguagePolicy.EligibleIndices(source, "zh-CN"), 3);
        var block = LyricsParser.Parse("我们带着蓝色雨伞\n\n山谷的纸船\n我的小船停在岸边", LyricsProviderKind.NetEase);
        CollectionAssert.AreEqual(new[] { "山谷的纸船" }, LyricsLanguagePolicy.EligibleSegments(block.Lines[0], "zh-CN"));
    }

    [TestMethod]
    [DataRow("I will watch the boats with you")]
    [DataRow("川の水が揺れる")]
    [DataRow("작은 배를 기다려요")]
    [DataRow("我的小船 waits for you")]
    [DataRow("kimi no na wa")]
    public void ForeignAndMixedSegmentsPreventWholeSongChineseContext(string foreign)
    {
        var source = ChineseSource(true);
        source = source with { Lines = [.. source.Lines, Line(foreign, 8)] };
        var eligible = LyricsLanguagePolicy.EligibleIndices(source, "zh-CN");
        CollectionAssert.Contains(eligible, 8);
        CollectionAssert.Contains(eligible, 7, "Unknown Han cannot inherit a Chinese majority in a mixed song.");
        CollectionAssert.DoesNotContain(eligible, 0, "Matched artist/title metadata is not a lyric.");
        CollectionAssert.DoesNotContain(eligible, 3, "A standalone performer label is not a lyric.");
        var untimed = LyricsParser.Parse(string.Join("\n", source.Lines.Select(line => line.Text)), LyricsProviderKind.NetEase)
            with { Match = source.Match };
        CollectionAssert.Contains(LyricsLanguagePolicy.EligibleSegments(untimed, "zh-CN")[0], foreign);
    }

    [TestMethod]
    [DataRow("愛")]
    [DataRow("世界")]
    [DataRow("目的地到着")]
    [DataRow("春夏秋冬")]
    public void SharedHanStaysUnknownAndExplicitJapaneseNeverInheritsChinese(string text)
    {
        Assert.IsFalse(LyricsLanguagePolicy.Identify(text).IsConfident);
        CollectionAssert.AreEqual(new[] { 0 }, LyricsLanguagePolicy.EligibleIndices(Document(text), "zh-CN"));
        var source = ChineseSource(true);
        source = source with { Lines = [.. source.Lines, Line(text, 8) with { SourceLanguage = "ja" }] };
        CollectionAssert.Contains(LyricsLanguagePolicy.EligibleIndices(source, "zh-CN"), 8);
        Assert.AreEqual("ja", LyricsLanguagePolicy.SourceEvidence(source)[8].Language);
        Assert.AreEqual("ja", source.Lines[8].SourceLanguage);
    }

    [TestMethod]
    [DataRow("我真的搬不动木箱")]
    [DataRow("还是不适应你离开")]
    public void ChineseClauseGrammarBypassesWithoutBorrowingDocumentMetadata(string text)
    {
        Assert.IsTrue(LyricsLanguagePolicy.Identify(text).IsConfident);
        Assert.IsEmpty(LyricsLanguagePolicy.EligibleIndices(Document(text), "zh-CN"));
        CollectionAssert.AreEqual(new[] { 0 }, LyricsLanguagePolicy.EligibleIndices(Document(text), "en-US"));
    }

    [TestMethod]
    public void UnmatchedLatinHeaderCannotEraseForeignLyricsOrManufactureChineseEvidence()
    {
        var source = ChineseSource(true);
        source = source with { Lines = [source.Lines[0] with { Text = "I will watch the boats with you" }, .. source.Lines.Skip(1)] };
        CollectionAssert.Contains(LyricsLanguagePolicy.EligibleIndices(source, "zh-CN"), 0);
        CollectionAssert.Contains(LyricsLanguagePolicy.EligibleIndices(source, "zh-CN"), 7);
    }

    private static LyricsDocument ChineseSource(bool timed)
    {
        string[] text = ["SingerA、SingerB - 合成曲 (with SingerB)", "作词：WriterX", "作曲：ComposerY/ComposerZ",
            "SingerA:", "我们带着蓝色雨伞", "山谷的纸船", "我的小船停在岸边", "晴"];
        var source = timed ? Document(text) : LyricsParser.Parse(string.Join("\n", text), LyricsProviderKind.NetEase);
        return source with { Match = new("合成曲 (with SingerB)", "SingerA、SingerB", "合成专辑", 40, 12) };
    }

    private static LyricsDocument Document(params string[] text) => new(text.Select(Line).ToArray(), LyricsProviderKind.NetEase);
    private static LyricsLine Line(string text, int i) => new(TimeSpan.FromSeconds(i * 3), TimeSpan.FromSeconds(i * 3 + 3), text, null, []);
}
