using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class HanAbstentionTests
{
    [TestMethod]
    [DataRow("山谷的石门", false)]
    [DataRow("山谷的石门", true)]
    [DataRow("晴", false)]
    [DataRow("晴", true)]
    [DataRow("这扇窗移动太快", false)]
    [DataRow("这扇窗移动太快", true)]
    [DataRow("我不関焉", false)]
    [DataRow("我不関焉", true)]
    public void AmbiguousHanAbstainsInChineseWithoutClaimingLanguage(string text, bool accepted)
    {
        var document = Document("我们带着蓝色雨伞", text, "我的小船停在岸边", "晴");
        if (accepted) document = document with { Match = new("合成曲", "SingerA", "合成专辑", 40, 12) };
        var evidence = LyricsLanguagePolicy.SourceEvidence(document)[1];
        Assert.IsNull(evidence.Language);
        Assert.AreEqual(LyricsLanguageEvidenceKind.Unknown, evidence.Kind);
        Assert.AreEqual(LyricsTranslationAdmission.Abstain, LyricsLanguagePolicy.GetAdmission(text, "zh-CN", evidence));
        Assert.IsEmpty(LyricsLanguagePolicy.EligibleIndices(document, "zh-CN"));
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 }, LyricsLanguagePolicy.EligibleIndices(document, "en-US"));
        Assert.IsTrue(document.Lines.All(line => line.SourceLanguage is null));
    }

    [TestMethod]
    public void ThreeAdmissionStatesSeparateIdentityFromAbstentionAndForeignTranslation()
    {
        foreach (var (text, target, expected) in new[]
        {
            ("我们带着蓝色雨伞", "zh-TW", LyricsTranslationAdmission.SameLanguage),
            ("山谷的石门", "zh-TW", LyricsTranslationAdmission.Abstain),
            ("山谷的石门", "en-US", LyricsTranslationAdmission.Translate),
            ("I will wait for you", "zh-CN", LyricsTranslationAdmission.Translate),
            ("我的小船 waits for you", "zh-CN", LyricsTranslationAdmission.Translate),
            ("我不関焉かな", "zh-CN", LyricsTranslationAdmission.Translate),
        }) Assert.AreEqual(expected, LyricsLanguagePolicy.GetAdmission(text, target, LyricsLanguagePolicy.Identify(text)), text);
        Assert.AreEqual("ja", LyricsLanguagePolicy.Identify("我不関焉かな").Language);
        Assert.AreEqual(LyricsTranslationAdmission.Translate,
            LyricsLanguagePolicy.GetAdmission("世界", "zh-CN", LyricsLanguagePolicy.Identify("世界", "ja")));
    }

    [TestMethod]
    [DataRow("世界", LyricsTranslationAdmission.Abstain)]
    [DataRow("我的小船 waits for you", LyricsTranslationAdmission.Translate)]
    public void MultilingualTagAloneDoesNotConfirmForeignLanguageForPureHan(string text, LyricsTranslationAdmission expected)
    {
        var evidence = LyricsLanguagePolicy.Identify(text, "mul");
        Assert.AreEqual("mul", evidence.Language);
        Assert.AreEqual(expected, LyricsLanguagePolicy.GetAdmission(text, "zh-CN", evidence));
        var source = Document(text);
        source = source with { Lines = [source.Lines[0] with { SourceLanguage = "mul" }] };
        CollectionAssert.AreEqual(expected == LyricsTranslationAdmission.Translate ? new[] { 0 } : Array.Empty<int>(),
            LyricsLanguagePolicy.EligibleIndices(source, "zh-CN"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TwoImmediateJapaneseNeighboursSupportOnlyTheirBoundedHanUnit(bool untimed)
    {
        string[] text = ["川の水が揺れる", "我不関焉", "君の声が聞こえる", "晴"];
        var document = untimed ? LyricsParser.Parse(string.Join("\n", text), LyricsProviderKind.NetEase) : Document(text);
        if (untimed)
            CollectionAssert.AreEqual(text[..3], LyricsLanguagePolicy.EligibleSegments(document, "zh-CN")[0]);
        else
        {
            Assert.AreEqual("ja", LyricsLanguagePolicy.SourceEvidence(document)[1].Language);
            Assert.AreEqual(LyricsLanguageEvidenceKind.Context, LyricsLanguagePolicy.SourceEvidence(document)[1].Kind);
            CollectionAssert.AreEqual(new[] { 0, 1, 2 }, LyricsLanguagePolicy.EligibleIndices(document, "zh-CN"));
        }
        Assert.IsNull(LyricsLanguagePolicy.Identify(text[1]).Language);
        Assert.IsTrue(document.Lines.All(line => line.SourceLanguage is null));
    }

    [TestMethod]
    [DataRow("川の水が揺れる\n\n世界\n君の声が聞こえる")]
    [DataRow("川の水が揺れる\n世界\n\n君の声が聞こえる")]
    [DataRow("川の水が揺れる\n世界\n東京\n君の声が聞こえる")]
    [DataRow("川の水が揺れる\n世界\nI will wait for you")]
    public void JapaneseEvidenceCannotCrossBlankUnknownOrOtherLanguageBoundaries(string text)
    {
        var document = LyricsParser.Parse(text, LyricsProviderKind.NetEase);
        var segments = LyricsLanguagePolicy.EligibleSegments(document, "zh-CN")[0];
        CollectionAssert.DoesNotContain(segments, "世界");
        CollectionAssert.DoesNotContain(segments, "東京");
    }

    [TestMethod]
    public void JapaneseNeighbourContextDoesNotCrossTimingGapAndExplicitTagsRemainAuthoritative()
    {
        var document = Document("川の水が揺れる", "世界", "君の声が聞こえる");
        document = document with { Lines = [document.Lines[0], document.Lines[1] with
            { Start = TimeSpan.FromSeconds(30), End = TimeSpan.FromSeconds(33) }, document.Lines[2]] };
        CollectionAssert.AreEqual(new[] { 0, 2 }, LyricsLanguagePolicy.EligibleIndices(document, "zh-CN"));
        var explicitJapanese = Document("世界", "我不関焉") with { Lines = [
            Document("世界").Lines[0] with { SourceLanguage = "ja" },
            Document("我不関焉").Lines[0] with { SourceLanguage = "ja" }] };
        CollectionAssert.AreEqual(new[] { 0, 1 }, LyricsLanguagePolicy.EligibleIndices(explicitJapanese, "zh-CN"));
        Assert.IsEmpty(LyricsLanguagePolicy.EligibleIndices(explicitJapanese, "ja"));
    }

    [TestMethod]
    public void OldMixedAiSecondaryIsDiscardedWholeWithoutTouchingOriginalOrProvider()
    {
        var source = Document("山谷的石门\nI will wait for you", "晴", "我们带着蓝色雨伞");
        source = source with { Lines = [source.Lines[0] with { Words = [new("山", TimeSpan.Zero, TimeSpan.FromSeconds(1))],
            Secondary = "旧中文改写和译文", TranslationOrigin = LyricsTranslationOrigin.LocalAi, TranslationLanguage = "zh-CN" },
            source.Lines[1] with { Secondary = "旧短行改写", TranslationOrigin = LyricsTranslationOrigin.LocalAi, TranslationLanguage = "zh-CN" },
            source.Lines[2] with { Secondary = "提供方原译文", TranslationOrigin = LyricsTranslationOrigin.Provider, TranslationLanguage = "zh-CN" }] };
        var cleaned = LyricsLanguagePolicy.RemoveIneligibleLocalTranslations(source, "zh-CN");
        for (var id = 0; id < 2; id++)
        {
            Assert.IsNull(LyricsDisplayPolicy.SecondaryPresentation(cleaned.Lines[id], "zh-CN", true));
            Assert.AreEqual(source.Lines[id].Text, cleaned.Lines[id].Text);
            Assert.AreEqual(source.Lines[id].Start, cleaned.Lines[id].Start);
            Assert.AreEqual(source.Lines[id].End, cleaned.Lines[id].End);
            Assert.AreSame(source.Lines[id].Words, cleaned.Lines[id].Words);
        }
        Assert.AreSame(source.Lines[2], cleaned.Lines[2]);
    }

    private static LyricsDocument Document(params string[] text) => new(text.Select((part, id) =>
        new LyricsLine(TimeSpan.FromSeconds(id * 3), TimeSpan.FromSeconds(id * 3 + 3), part, null, [])).ToArray(), LyricsProviderKind.NetEase);
}
