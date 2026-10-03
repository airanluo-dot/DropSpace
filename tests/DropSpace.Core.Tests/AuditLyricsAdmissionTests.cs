using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class AuditLyricsAdmissionTests
{
    [TestMethod]
    [DataRow("I love you")]
    [DataRow("I need you")]
    [DataRow("Let it be")]
    [DataRow("LET THEM GO")]
    public void ShortEnglishGrammarIsConfidentAndSameTargetHasNoEligibleId(string text)
    {
        var evidence = LyricsLanguagePolicy.Identify(text);
        Assert.AreEqual("en", evidence.Language);
        Assert.IsTrue(evidence.IsConfident);
        var document = LyricsParser.Parse(text, LyricsProviderKind.NetEase);
        Assert.IsEmpty(LyricsLanguagePolicy.EligibleIndices(document, "en-US"));
        CollectionAssert.AreEqual(new[] { 0 }, LyricsLanguagePolicy.EligibleIndices(document, "zh-CN"));
    }

    [TestMethod]
    [DataRow("kimi no na wa")]
    [DataRow("wo ai ni")]
    [DataRow("My was znamy")]
    [DataRow("La vie est belle")]
    [DataRow("I love you 愛")]
    [DataRow("I love you, je t’aime")]
    [DataRow("I love you wo ai ni")]
    [DataRow("I love you kimi o ai shiteru")]
    [DataRow("Your blue cup waits beside the window wo ai ni.")]
    [DataRow("You said the northern road was closed, je t’aime.")]
    [DataRow("YOU SAID THE NORTHERN ROAD WAS CLOSED, JE T'AIME.")]
    [DataRow("Your amber lantern glows beside the window, mi amor.")]
    public void ForeignLatinRomanizationAndMixedScriptsStayUnknown(string text)
    {
        var evidence = LyricsLanguagePolicy.Identify(text);
        Assert.IsNull(evidence.Language);
        Assert.IsFalse(evidence.IsConfident);
        var document = LyricsParser.Parse(text, LyricsProviderKind.NetEase);
        CollectionAssert.AreEqual(new[] { 0 }, LyricsLanguagePolicy.EligibleIndices(document, "en-US"));
    }

    [TestMethod]
    [DataRow("Your blue cup waits beside the window.")]
    [DataRow("You said the northern road was closed.")]
    [DataRow("Your amber lantern glows beside the window.")]
    [DataRow("You said the winding path was blocked.")]
    [DataRow("YOUR TURQUOISE TELESCOPE RESTS BESIDE THE OBSERVATORY.")]
    public void OpenEnglishContentWordsPreserveOriginalAndProviderLanguageEvidence(string text)
    {
        Assert.AreEqual("en", LyricsLanguagePolicy.Identify(text).Language);
        Assert.IsTrue(LyricsLanguagePolicy.Identify(text).IsConfident);
        var original = LyricsParser.Parse("[00:01]" + text, LyricsProviderKind.NetEase);
        Assert.IsEmpty(LyricsLanguagePolicy.EligibleIndices(original, "en-US"));
        Assert.AreEqual(text, original.Lines[0].Text);
        var provider = LyricsParser.Parse("[00:01]你的蓝色杯子放在窗边", LyricsProviderKind.QqMusic, "[00:01]" + text);
        Assert.AreEqual("en", provider.Lines[0].TranslationLanguage);
        Assert.IsTrue(LyricsTranslationPolicy.HasMatchingProviderTranslation(provider, "en-US"));
        Assert.AreEqual(text, LyricsDisplayPolicy.Secondary(provider.Lines[0], "en-US", true));
        Assert.AreEqual(LyricsTranslationOrigin.Provider, provider.Lines[0].TranslationOrigin);
    }

    [TestMethod]
    public void EveryClauseMustSupplyItsOwnLanguageEvidenceWithoutBorrowingAnEnglishPrefix()
    {
        string[] foreign = ["kimi ga suki", "boku mo kimi ga suki", "watashi wa anata ga suki", "anata ni aitai", "kimi to aruita",
            "wo hen xiang ni", "wo bu xiang zou", "ni hai ai wo", "wo ye zai deng ni", "wo zhen de xiang ni", "wo yi zhi zai deng ni"];
        string[] separators = [", ", "; ", ": ", ". ", "! ", "? ", " — ", " / ", " | ", "｜", "… ", "，", "；"];
        foreach (var clause in foreign)
        {
            var texts = separators.SelectMany(separator => new[] { "I love you" + separator + clause, clause + separator + "I love you" })
                .Concat(["I love you " + clause, clause + " I love you", "I " + clause + " love you",
                    "I love you and " + clause, "I love you but " + clause, "I love you (" + clause + ")"]);
            foreach (var text in texts)
            {
                Assert.IsFalse(LyricsLanguagePolicy.Identify(text).IsConfident, text);
                var original = LyricsParser.Parse("作词：某人\n" + text + "\n作曲：某人", LyricsProviderKind.NetEase);
                CollectionAssert.AreEqual(new[] { 0 }, LyricsLanguagePolicy.EligibleIndices(original, "en-US"), text);
                CollectionAssert.AreEqual(new[] { text }, LyricsLanguagePolicy.EligibleSegments(original.Lines[0], "en-US"), text);
                var provider = LyricsParser.Parse("[00:01]君が好き\n[00:04]我的世界充满阳光", LyricsProviderKind.QqMusic, "[00:01]" + text);
                Assert.IsNull(provider.Lines[0].TranslationLanguage, text);
                Assert.IsFalse(LyricsTranslationPolicy.HasMatchingProviderTranslation(provider, "en-US"), text);
            }
        }
    }

    [TestMethod]
    [DataRow("I love you, suki")]
    [DataRow("Baby; I need you")]
    [DataRow("I love you (London)")]
    [DataRow("I love you | suki")]
    [DataRow("Baby｜I need you")]
    [DataRow("I love you ‘suki’")]
    [DataRow("'Baby' I need you")]
    public void UnknownClauseRemainsUnknownWithoutInventingForeignLanguage(string text)
    {
        Assert.IsNull(LyricsLanguagePolicy.Identify(text).Language);
    }

    [TestMethod]
    [DataRow("I love you, I need you")]
    [DataRow("I love you; you are my light")]
    [DataRow("Your turquoise telescope rests beside the observatory.")]
    [DataRow("The women carry my lantern")]
    [DataRow("The ore glitters beside my lantern")]
    [DataRow("The hen rests beside my telescope")]
    [DataRow("You're the light in my life")]
    public void IndependentEnglishClausesAndHomographsRetainOpenVocabulary(string text)
    {
        Assert.AreEqual("en", LyricsLanguagePolicy.Identify(text).Language);
        Assert.IsEmpty(LyricsLanguagePolicy.EligibleIndices(LyricsParser.Parse(text, LyricsProviderKind.NetEase), "en-US"));
    }

    [TestMethod]
    public void InferredTagsAreReevaluatedButExplicitTtmlTagsRemainAuthoritative()
    {
        var source = LyricsParser.Parse("[00:01]君が好き", LyricsProviderKind.NetEase, "[00:01]I love you, wo hen xiang ni");
        var stale = source with { Lines = [source.Lines[0] with { TranslationLanguage = "en", TranslationLanguageIsExplicit = false }] };
        Assert.IsFalse(LyricsTranslationPolicy.HasMatchingProviderTranslation(stale, "en-US"));
        Assert.IsNull(LyricsLanguagePolicy.IdentifyProviderTranslations(stale).Lines[0].TranslationLanguage);
        var explicitSource = LyricsParser.Parse("[00:01]世界", LyricsProviderKind.NetEase,
            "<tt xmlns=\"http://www.w3.org/ns/ttml\" xml:lang=\"en\"><body><p begin=\"1s\">World</p></body></tt>");
        Assert.AreEqual(true, explicitSource.Lines[0].TranslationLanguageIsExplicit);
        Assert.IsTrue(LyricsTranslationPolicy.HasMatchingProviderTranslation(LyricsLanguagePolicy.IdentifyProviderTranslations(explicitSource), "en-US"));
    }

    [TestMethod]
    [DataRow("作词：某人\nI love you\n作曲：另一人\n君の声が聞こえる", "zh-CN", "I love you", "君の声が聞こえる")]
    [DataRow("I love you\n作曲：另一人\nkimi no na wa", "en-US", "kimi no na wa", null)]
    [DataRow("作词：某人\n我的世界充满阳光\nI need you\n作曲：另一人\n愛", "zh-CN", "I need you", null)]
    public void UntimedMixedDocumentKeepsDisplayBlockButAdmitsOnlyEligiblePhysicalSegments(
        string text, string target, string first, string? second)
    {
        var document = LyricsParser.Parse(text, LyricsProviderKind.NetEase);
        Assert.HasCount(1, document.Lines);
        var line = document.Lines[0];
        Assert.AreEqual(string.Join(Environment.NewLine, text.Split('\n')), line.Text);
        Assert.AreEqual(TimeSpan.Zero, line.Start);
        Assert.AreEqual(TimeSpan.FromHours(24), line.End);
        Assert.IsEmpty(line.Words);
        Assert.IsFalse(LyricsLanguagePolicy.IsCredit(line.Text));
        CollectionAssert.AreEqual(second is null ? new[] { first } : new[] { first, second },
            LyricsLanguagePolicy.EligibleSegments(line, target));
        CollectionAssert.AreEqual(new[] { 0 }, LyricsLanguagePolicy.EligibleIndices(document, target));
    }

    [TestMethod]
    public void AllCreditAndSameTargetPhysicalLinesDoNotProduceAnInferenceId()
    {
        foreach (var text in new[] { "作词：某人\n作曲：另一人", "作词：某人\nI love you\n作曲：另一人\nLet it be" })
        {
            var document = LyricsParser.Parse(text, LyricsProviderKind.NetEase);
            Assert.IsEmpty(LyricsLanguagePolicy.EligibleSegments(document.Lines[0], "en-US"));
            Assert.IsEmpty(LyricsLanguagePolicy.EligibleIndices(document, "en-US"));
        }
    }

    [TestMethod]
    public void UntimedProviderTranslationKeepsChineseEvidenceBesideAnUnknownRetainedName()
    {
        var source = LyricsParser.Parse("I miss your smile\nLondon", LyricsProviderKind.NetEase,
            "我的世界充满阳光\nLondon");
        Assert.HasCount(1, source.Lines);
        Assert.AreEqual("我的世界充满阳光" + Environment.NewLine + "London", source.Lines[0].Secondary);
        Assert.IsTrue(LyricsTranslationPolicy.HasMatchingProviderTranslation(source, "zh-CN"));
        Assert.IsFalse(LyricsTranslationPolicy.HasMatchingProviderTranslation(source, "en-US"),
            "A retained name does not supply positive English evidence.");
        var legacy = source with { Lines = [source.Lines[0] with { TranslationLanguage = null }] };
        var classified = LyricsLanguagePolicy.IdentifyProviderTranslations(legacy);
        Assert.IsTrue(LyricsTranslationPolicy.HasMatchingProviderTranslation(classified, "zh-CN"));
        Assert.AreEqual(source.Lines[0].Secondary, classified.Lines[0].Secondary);
    }

    [TestMethod]
    public void MultipleConfidentLanguagesInOneUntimedTranslationKeepSegmentMatchesWithoutASingleFalseTag()
    {
        var source = LyricsParser.Parse("First verse\nSecond verse\nThird verse", LyricsProviderKind.QqMusic,
            "我的世界充满阳光\nI love you\n君の声が聞こえる");
        Assert.HasCount(1, source.Lines);
        Assert.IsNull(source.Lines[0].TranslationLanguage);
        Assert.IsTrue(LyricsTranslationPolicy.HasMatchingProviderTranslation(source, "zh-CN"));
        Assert.IsTrue(LyricsTranslationPolicy.HasMatchingProviderTranslation(source, "en-US"));
        Assert.IsTrue(LyricsTranslationPolicy.HasMatchingProviderTranslation(source, "ja"));
        foreach (var target in new[] { "zh-CN", "en-US", "ja" })
            Assert.AreEqual(source.Lines[0].Secondary, LyricsDisplayPolicy.Secondary(source.Lines[0], target, true),
                "The source translation that bypassed AI must remain displayable.");
        var creditOnlyEvidence = LyricsParser.Parse("I miss your smile\nLondon", LyricsProviderKind.QqMusic,
            "作词：我的世界充满阳光\nLondon");
        Assert.IsFalse(LyricsTranslationPolicy.HasMatchingProviderTranslation(creditOnlyEvidence, "zh-CN"));
    }

    [TestMethod]
    [DataRow("我的世界充满阳光\n远方的天空\n你的温柔让我难忘")]
    [DataRow("作词：某人\n我的世界充满阳光\n编曲：某人\n远方的天空\n作曲：另一人\n你的温柔让我难忘")]
    public void UntimedChineseContextSkipsWeakPhysicalLineWithoutTranslatingCredits(string text)
    {
        var weak = LyricsLanguagePolicy.Identify("远方的天空");
        Assert.IsFalse(weak.IsConfident, "The fixture must require neighboring evidence.");
        Assert.AreEqual("zh-Hans", weak.Language);
        var document = LyricsParser.Parse(text, LyricsProviderKind.NetEase);
        Assert.HasCount(1, document.Lines);
        Assert.IsEmpty(LyricsLanguagePolicy.EligibleSegments(document.Lines[0], "zh-CN"));
        Assert.IsEmpty(LyricsLanguagePolicy.EligibleIndices(document, "zh-CN"));
    }

    [TestMethod]
    [DataRow("我的世界充满阳光\nI love you\n远方的天空\n你的温柔让我难忘", "I love you")]
    [DataRow("我的世界充满阳光\n春夏秋冬\n远方的天空\n你的温柔让我难忘", null)]
    [DataRow("我的世界充满阳光\n\n远方的天空\n你的温柔让我难忘", null)]
    [DataRow("我的世界充满阳光\n远方的天空\n\n你的温柔让我难忘", null)]
    [DataRow("我的世界充满阳光\n远方的天空", null)]
    [DataRow("我的世界充满阳光\n远方的天空\n夏天的风\n你的温柔让我难忘", null)]
    public void UntimedHanAbstentionPreservesBlankAndWeakBoundaries(string text, string? foreign)
    {
        var document = LyricsParser.Parse(text, LyricsProviderKind.NetEase);
        Assert.HasCount(1, document.Lines);
        Assert.AreEqual(string.Join(Environment.NewLine, text.Split('\n')), document.Lines[0].Text,
            "The parser must preserve a physical blank section boundary for admission.");
        var expected = foreign is null ? Array.Empty<string>() : new[] { foreign };
        CollectionAssert.AreEqual(expected, LyricsLanguagePolicy.EligibleSegments(document.Lines[0], "zh-CN"));
        CollectionAssert.AreEqual(expected.Length == 0 ? Array.Empty<int>() : new[] { 0 },
            LyricsLanguagePolicy.EligibleIndices(document, "zh-CN"));
    }
}
