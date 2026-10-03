using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class LyricsAdmissionBoundaryRegressionTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SharedModifierCannotTurnJapaneseHanIntoChineseNeighbourEvidence(bool accepted)
    {
        var source = Document("我会把木箱搬给你", "圧倒的存在", "你们守在小桥旁", "川の水が揺れる");
        if (accepted) source = source with { Match = Match() };
        Assert.IsNull(LyricsLanguagePolicy.Identify(source.Lines[1].Text).Language);
        Assert.IsFalse(LyricsLanguagePolicy.SourceEvidence(source)[1].IsConfident);
        CollectionAssert.AreEqual(new[] { 1, 3 }, LyricsLanguagePolicy.EligibleIndices(source, "zh-CN"));
    }

    [TestMethod]
    [DataRow("圧倒的存在")]
    [DataRow("文学的表現")]
    [DataRow("目的地到着")]
    [DataRow("山谷的石门")]
    public void OpaqueLongHanVetoesWholeSongPromotionWithoutAWordBlacklist(string opaque)
    {
        var source = Document("我们带着蓝色雨伞", "我的小船停在岸边", opaque, "晴") with { Match = Match() };
        CollectionAssert.Contains(LyricsLanguagePolicy.EligibleIndices(source, "zh-CN"), 2);
        CollectionAssert.Contains(LyricsLanguagePolicy.EligibleIndices(source, "zh-CN"), 3);
    }

    [TestMethod]
    public void IndependentWeakChineseEvidenceStillUsesBoundedNeighbours()
    {
        var source = Document("我们带着蓝色雨伞", "山谷的纸船", "我的小船停在岸边", "川の水が揺れる");
        var weak = LyricsLanguagePolicy.Identify(source.Lines[1].Text);
        Assert.AreEqual("zh-Hans", weak.Language);
        Assert.IsFalse(weak.IsConfident);
        Assert.AreEqual(LyricsLanguageEvidenceKind.Context, LyricsLanguagePolicy.SourceEvidence(source)[1].Kind);
        CollectionAssert.AreEqual(new[] { 3 }, LyricsLanguagePolicy.EligibleIndices(source, "zh-CN"));
    }

    [TestMethod]
    public void SimplifiedChineseFormSupportsWeakModifierWithoutClassifyingSharedJapaneseHan()
    {
        const string text = "留下三百年的约束";
        var weak = LyricsLanguagePolicy.Identify(text);
        Assert.AreEqual("zh-Hans", weak.Language);
        Assert.IsFalse(weak.IsConfident);
        Assert.IsNull(LyricsLanguagePolicy.Identify("圧倒的存在").Language);
        var source = Document(text, "我们带着蓝色雨伞", "我的小船停在岸边", "晴") with { Match = Match() };
        Assert.IsEmpty(LyricsLanguagePolicy.EligibleIndices(source, "zh-CN"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ExplicitChineseTagsHavePerLineAuthorityButCannotManufactureLexicalAnchors(bool separated)
    {
        var source = Document("東京", separated ? "大阪\n\n" : "大阪", "世界") with { Match = Match() };
        source = source with { Lines = [source.Lines[0] with { SourceLanguage = "zh-CN" },
            source.Lines[1] with { SourceLanguage = "zh-CN" },
            source.Lines[2] with { Start = separated ? TimeSpan.FromSeconds(90) : source.Lines[2].Start,
                End = separated ? TimeSpan.FromSeconds(93) : source.Lines[2].End }] };
        CollectionAssert.AreEqual(new[] { 2 }, LyricsLanguagePolicy.EligibleIndices(source, "zh-CN"));
        Assert.AreEqual(LyricsLanguageEvidenceKind.Explicit, LyricsLanguagePolicy.SourceEvidence(source)[0].Kind);
        Assert.AreEqual("zh-CN", source.Lines[1].SourceLanguage);
    }

    [TestMethod]
    [DataRow("Yes - Love")]
    [DataRow("Love — Yes")]
    [DataRow("Yes–Love")]
    public void AcceptedCompleteArtistAndTitlePairExcludesOnlyTheHeader(string header)
    {
        var source = Document(header, "I will wait for you") with { Match = Match("Love", "Yes") };
        CollectionAssert.AreEqual(new[] { 1 }, LyricsLanguagePolicy.EligibleIndices(source, "zh-CN"));
        Assert.AreEqual(header, source.Lines[0].Text);
    }

    [TestMethod]
    [DataRow("Yes - I will love you")]
    [DataRow("I will love you - Yes")]
    public void ArtistAndTitleSubstringsCannotEraseASungForeignLine(string text)
    {
        var source = Document(text) with { Match = Match("Love", "Yes") };
        CollectionAssert.AreEqual(new[] { 0 }, LyricsLanguagePolicy.EligibleIndices(source, "zh-CN"));
        CollectionAssert.AreEqual(new[] { text }, LyricsLanguagePolicy.EligibleSegments(source, "zh-CN")[0]);
    }

    [TestMethod]
    public void HeaderDetectionRequiresAcceptedScoreAndNeverScansLaterPhysicalLines()
    {
        var weak = Document("Yes - Love") with { Match = Match("Love", "Yes") with { Score = 3 } };
        CollectionAssert.AreEqual(new[] { 0 }, LyricsLanguagePolicy.EligibleIndices(weak, "zh-CN"));
        var block = LyricsParser.Parse("Yes Love\n- I will wait for you\nYes - Love", LyricsProviderKind.NetEase)
            with { Match = Match("Love", "Yes") };
        CollectionAssert.AreEqual(new[] { "Yes Love", "- I will wait for you", "Yes - Love" },
            LyricsLanguagePolicy.EligibleSegments(block, "zh-CN")[0]);
        var headerBlock = LyricsParser.Parse("Yes - Love\nYes - I will love you", LyricsProviderKind.NetEase)
            with { Match = Match("Love", "Yes") };
        CollectionAssert.AreEqual(new[] { "Yes - I will love you" }, LyricsLanguagePolicy.EligibleSegments(headerBlock, "zh-CN")[0]);
    }

    [TestMethod]
    [DataRow("No:")]
    [DataRow("Why:")]
    [DataRow("UnknownSinger:")]
    public void BareColonWordsAndUnknownAliasesStayEligible(string text)
    {
        foreach (var source in new[] { Document(text), Document(text) with { Match = Match("Love", "Yes") } })
            CollectionAssert.AreEqual(new[] { 0 }, LyricsLanguagePolicy.EligibleIndices(source, "zh-CN"));
    }

    [TestMethod]
    [DataRow("SingerA", "SingerA:")]
    [DataRow("SingerA / SingerB", "SingerB：")]
    [DataRow("SingerA、SingerB", "SingerA:")]
    public void CompleteKnownArtistIdentitySupportsAPerformerLabel(string artist, string label)
    {
        var source = Document(label, "I will wait for you") with { Match = Match("Love", artist) };
        CollectionAssert.AreEqual(new[] { 1 }, LyricsLanguagePolicy.EligibleIndices(source, "zh-CN"));
        var weak = source with { Match = source.Match! with { Score = 3 } };
        CollectionAssert.Contains(LyricsLanguagePolicy.EligibleIndices(weak, "zh-CN"), 0);
        Assert.AreEqual(label, source.Lines[0].Text);
    }

    private static LyricsMatchInfo Match(string title = "合成曲", string artist = "SingerA") => new(title, artist, "合成专辑", 40, 12);
    private static LyricsDocument Document(params string[] text) => new(text.Select((part, id) =>
        new LyricsLine(TimeSpan.FromSeconds(id * 3), TimeSpan.FromSeconds(id * 3 + 3), part, null, [])).ToArray(), LyricsProviderKind.NetEase);
}
