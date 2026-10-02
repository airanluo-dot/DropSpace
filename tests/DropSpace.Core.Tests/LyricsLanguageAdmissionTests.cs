using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class LyricsLanguageAdmissionTests
{
    [TestMethod]
    [DataRow("我爱你直到明天", "zh-Hans")]
    [DataRow("你们在这里等我", "zh-Hans")]
    [DataRow("我的世界充满阳光", "zh-Hans")]
    [DataRow("輕輕的風穿過了山谷", "zh-Hant")]
    [DataRow("我們依然在這裡", "zh-Hant")]
    [DataRow("I will stay with you", "en")]
    [DataRow("You're the light in my life", "en")]
    [DataRow("君の声が聞こえる", "ja")]
    [DataRow("너를 기다리고 있어", "ko")]
    public void PositiveLanguageEvidenceRequiresMoreThanScript(string text, string expected)
    {
        var evidence = LyricsLanguagePolicy.Identify(text);
        Assert.IsTrue(evidence.IsConfident);
        Assert.AreEqual(expected, evidence.Language);
        Assert.AreEqual(LyricsLanguageEvidenceKind.Lexical, evidence.Kind);
    }

    [TestMethod]
    [DataRow("愛")]
    [DataRow("世界")]
    [DataRow("東京")]
    [DataRow("一切合切")]
    [DataRow("目的地到着")]
    [DataRow("春夏秋冬")]
    [DataRow("kimi no na wa")]
    [DataRow("wo ai ni")]
    [DataRow("Already English")]
    [DataRow("A")]
    [DataRow("My was znamy")]
    [DataRow("La vie est belle")]
    [DataRow("我的世界 with you")]
    public void AmbiguousRomanizedAndMixedLinesRemainEligible(string text)
    {
        Assert.IsFalse(LyricsLanguagePolicy.Identify(text).IsConfident);
        CollectionAssert.AreEqual(new[] { 0 }, LyricsLanguagePolicy.EligibleIndices(Document(text), "zh-CN"));
    }

    [TestMethod]
    [DataRow("作词：张三")]
    [DataRow("作 词 : 张三")]
    [DataRow("作曲/李四")]
    [DataRow("作词 张三")]
    [DataRow("作曲 李四")]
    [DataRow("編曲：山田")]
    [DataRow("Lyrics by: Someone")]
    [DataRow("Written by Someone")]
    public void CreditsNeverContributeLanguageOrInferenceIds(string text)
    {
        Assert.IsTrue(LyricsLanguagePolicy.IsCredit(text));
        Assert.IsFalse(LyricsLanguagePolicy.Identify(text, "zh-CN").IsConfident);
        Assert.IsEmpty(LyricsLanguagePolicy.EligibleIndices(Document(text), "en"));
    }

    [TestMethod]
    public void DocumentContextSupportsGrammarButNeverUsesChineseMajorityForForeignSegments()
    {
        var document = Document("作词：张三", "我们仍在这里", "那年夏天的风", "你的声音很温柔", "I will stay with you", "愛", "君の声が聞こえる", "你们仍在等待");
        var evidence = LyricsLanguagePolicy.SourceEvidence(document);
        Assert.AreEqual(LyricsLanguageEvidenceKind.Context, evidence[2].Kind);
        Assert.IsTrue(evidence[2].IsConfident);
        Assert.IsFalse(evidence[5].IsConfident, "A short Japanese Han word must not inherit a Chinese majority.");
        CollectionAssert.AreEqual(new[] { 4, 5, 6 }, LyricsLanguagePolicy.EligibleIndices(document, "zh-CN"));
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 5, 6, 7 }, LyricsLanguagePolicy.EligibleIndices(document, "en"));
    }

    [TestMethod]
    public void ContextNeverCrossesAnUnknownLineLanguageBoundaryOrSectionGap()
    {
        var document = Document("我们仍在这里", "那年夏天的风", "春夏秋冬", "你的声音很温柔");
        Assert.IsFalse(LyricsLanguagePolicy.SourceEvidence(document)[1].IsConfident);
        document = Document("我们仍在这里", "那年夏天的风", "I will stay with you");
        Assert.IsFalse(LyricsLanguagePolicy.SourceEvidence(document)[1].IsConfident);
        document = Document("我们仍在这里", "那年夏天的风", "你的声音很温柔");
        document = document with { Lines = [document.Lines[0], document.Lines[1] with { Start = TimeSpan.FromSeconds(30) }, document.Lines[2]] };
        Assert.IsFalse(LyricsLanguagePolicy.SourceEvidence(document)[1].IsConfident);
    }

    [TestMethod]
    public void SourceChineseVariantsRemainOriginalButProviderLanguageStillMatchesSelectedVariant()
    {
        Assert.IsEmpty(LyricsLanguagePolicy.EligibleIndices(Document("我們依然在這裡"), "zh-CN"));
        var provider = LyricsParser.Parse("[00:01]I will wait for you", LyricsProviderKind.NetEase, "[00:01]我們依然在這裡");
        Assert.IsFalse(LyricsTranslationPolicy.HasMatchingProviderTranslation(provider, "zh-CN"));
        Assert.IsTrue(LyricsTranslationPolicy.HasMatchingProviderTranslation(provider, "zh-TW"));
    }

    [TestMethod]
    public void ProviderInferenceDoesNotTagShortUnknownLinesOrFillGaps()
    {
        var document = LyricsParser.Parse("[00:01]first\n[00:02]second\n[00:03]third", LyricsProviderKind.QqMusic,
            "[00:01]我会一直等待你\n[00:02]愛");
        Assert.AreEqual("zh-Hans", document.Lines[0].TranslationLanguage);
        Assert.IsNull(document.Lines[1].TranslationLanguage);
        Assert.IsNull(document.Lines[2].Secondary);
        Assert.IsTrue(LyricsTranslationPolicy.HasMatchingProviderTranslation(document, "zh-CN"));
    }

    [TestMethod]
    public void MixedTranslationLanguagesAndRomanizationDoNotBecomeTargetLanguage()
    {
        foreach (var secondary in new[] { "[00:01]kimi no na wa\n[00:02]ai no uta", "[00:01]我会一直等待你\n[00:02]君の声が聞こえる", "[00:01]我会一直等待你\n[00:02]I will stay with you" })
        {
            var document = LyricsParser.Parse("[00:01]first\n[00:02]second", LyricsProviderKind.NetEase, secondary);
            Assert.IsFalse(LyricsTranslationPolicy.HasMatchingProviderTranslation(document, "zh-CN"));
        }
    }

    [TestMethod]
    public void TtmlExplicitSourceAndSecondaryLanguagesHaveSeparateAuthority()
    {
        var document = LyricsParser.Parse("""
            <tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttm="http://www.w3.org/ns/ttml#metadata" xml:lang="ja"><body><div>
            <p begin="1s" end="3s">世界<span ttm:role="x-translation" xml:lang="en">World</span></p>
            <p begin="4s" end="5s" xml:lang="zh-CN">春夏秋冬</p>
            </div></body></tt>
            """, LyricsProviderKind.Amll);
        Assert.AreEqual("ja", document.Lines[0].SourceLanguage);
        Assert.AreEqual("zh-Hans", document.Lines[1].SourceLanguage);
        Assert.IsTrue(LyricsTranslationPolicy.HasMatchingProviderTranslation(document, "en-US"));
        CollectionAssert.AreEqual(new[] { 0 }, LyricsLanguagePolicy.EligibleIndices(document, "zh-CN"));
    }

    [TestMethod]
    public void TtmlPrimarySpanOverridesAndMixedSpansCannotInheritAChineseBypass()
    {
        var document = LyricsParser.Parse("""
            <tt xmlns="http://www.w3.org/ns/ttml" xml:lang="zh-CN"><body><div>
            <p begin="1s" end="3s"><span xml:lang="en">I will stay with you</span></p>
            <p begin="4s" end="6s">我的世界<span xml:lang="en">I will stay with you</span></p>
            <p begin="7s" end="9s">我的世界充满阳光</p>
            </div></body></tt>
            """, LyricsProviderKind.Amll);
        Assert.AreEqual("en", document.Lines[0].SourceLanguage);
        Assert.AreEqual("mul", document.Lines[1].SourceLanguage);
        CollectionAssert.AreEqual(new[] { 0, 1 }, LyricsLanguagePolicy.EligibleIndices(document, "zh-CN"));
    }

    [TestMethod]
    public void ExternalTtmlSecondaryRetainsItsOwnExplicitShortTranslationLanguage()
    {
        var document = LyricsParser.Parse("[00:01]世界", LyricsProviderKind.NetEase,
            "<tt xmlns=\"http://www.w3.org/ns/ttml\" xml:lang=\"en\"><body><p begin=\"1s\">World</p></body></tt>");
        Assert.AreEqual("en", document.Lines[0].TranslationLanguage);
        Assert.IsTrue(LyricsTranslationPolicy.HasMatchingProviderTranslation(document, "en-US"));
    }

    [TestMethod]
    public void TranslatedCreditsAreNotAWholeSongProviderBypass()
    {
        var source = Document("作词：Someone", "I will stay with you");
        source = source with { Lines = [source.Lines[0] with { Secondary = "词作者", TranslationOrigin = LyricsTranslationOrigin.Provider, TranslationLanguage = "zh-CN" }, source.Lines[1]] };
        Assert.IsFalse(LyricsTranslationPolicy.HasMatchingProviderTranslation(source, "zh-CN"));
    }

    private static LyricsDocument Document(params string[] texts) => new(texts.Select((text, i) =>
        new LyricsLine(TimeSpan.FromSeconds(i * 2), TimeSpan.FromSeconds(i * 2 + 2), text, null, [])).ToArray(), LyricsProviderKind.LocalLrc);
}
