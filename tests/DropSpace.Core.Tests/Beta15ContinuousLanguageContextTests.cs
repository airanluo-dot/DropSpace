using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;

namespace DropSpace.Core.Tests;

/// <summary>Host admission checks; they do not execute or validate model output.</summary>
[TestClass]
public sealed class Beta15ContinuousLanguageContextTests
{
    private static LyricsDocument Song(params string[] text) => new(text.Select((value, id) =>
        new LyricsLine(TimeSpan.FromSeconds(id * 3), TimeSpan.FromSeconds(id * 3 + 3), value, null, [])).ToArray(), LyricsProviderKind.QqMusic);

    [TestMethod]
    public void IndependentChineseAnchorsSupportAWholeContinuousVerseBeyondFourRows()
    {
        string[] ordinary = ["春夏秋冬", "青石古巷", "薄雾笼城", "千山万水", "一叶扁舟", "暮色四合",
            "孤帆一片", "寂寂长空", "长街寂寂", "漫步小巷", "静候钟声", "天穹苍茫"];
        Assert.IsTrue(ordinary.All(text => !LyricsLanguagePolicy.Identify(text).IsConfident));
        var source = Song(new[] { "我在等你回来" }.Concat(ordinary).Append("我们继续向前").ToArray());
        var evidence = LyricsLanguagePolicy.SourceEvidence(source);
        Assert.IsTrue(evidence.All(item => item.IsConfident && item.Language == "zh-Hans"));
        Assert.IsTrue(evidence.Skip(1).Take(ordinary.Length).All(item => item.Kind == LyricsLanguageEvidenceKind.Context));
        CollectionAssert.AreEqual(Enumerable.Range(0, source.Lines.Count).ToArray(), LyricsLanguagePolicy.EligibleIndices(source, "en"));
        Assert.IsEmpty(LyricsLanguagePolicy.EligibleIndices(source, "zh-Hans"));
        Assert.IsTrue(LyricsLanguagePolicy.MarkTranslationStates(source, "en").Lines.All(line =>
            line.TranslationState == LyricsLineTranslationState.Pending && line.TranslationReason == "awaiting-translation"));
        var untimed = LyricsParser.Parse(string.Join('\n', source.Lines.Select(line => line.Text)), LyricsProviderKind.QqMusic);
        Assert.AreEqual(source.Lines.Count, LyricsLanguagePolicy.EligibleSegments(untimed, "en")[0].Length);
    }

    [TestMethod]
    public void ForeignScriptTagsBlanksAndTimeGapsDoNotBridgeChineseVerseEvidence()
    {
        foreach (var boundary in new[] { "I will wait for you", "君の声が聞こえる", "wo ai ni", string.Empty, "世界" })
        {
            var source = Song("我在等你回来", "春夏秋冬", boundary, "千山万水", "我们继续向前");
            if (boundary == "世界") source = source with { Lines = source.Lines.Select((line, id) =>
                id == 2 ? line with { SourceLanguage = "ja" } : line).ToArray() };
            var evidence = LyricsLanguagePolicy.SourceEvidence(source);
            Assert.IsFalse(evidence[1].IsConfident, boundary);
            Assert.IsFalse(evidence[3].IsConfident, boundary);
            if (boundary == "世界") Assert.AreEqual("ja", evidence[2].Language);
            var admitted = LyricsLanguagePolicy.EligibleIndices(source, "en");
            Assert.IsFalse(admitted.Contains(1), boundary);
            Assert.IsFalse(admitted.Contains(3), boundary);
        }
        var gap = Song("我在等你回来", "春夏秋冬", "我们继续向前");
        // LRC infers End from the next timestamp, so start separation must also
        // preserve an instrumental section boundary instead of reporting a zero gap.
        gap = gap with { Lines = [gap.Lines[0], gap.Lines[1] with { End = TimeSpan.FromSeconds(30) },
            gap.Lines[2] with { Start = TimeSpan.FromSeconds(30), End = TimeSpan.FromSeconds(33) }] };
        Assert.IsFalse(LyricsLanguagePolicy.SourceEvidence(gap)[1].IsConfident);
        CollectionAssert.AreEqual(new[] { 0, 2 }, LyricsLanguagePolicy.EligibleIndices(gap, "en"));
    }

    [TestMethod]
    public void ARepeatedAnchorOrAnUnknownRepeatInAnotherSectionDoesNotInventEvidence()
    {
        var repeatedAnchor = Song("我在等你回来", "春夏秋冬", "我在等你回来");
        Assert.IsFalse(LyricsLanguagePolicy.SourceEvidence(repeatedAnchor)[1].IsConfident);
        var separated = Song("我在等你回来", "春夏秋冬", "我们继续向前", string.Empty, "春夏秋冬");
        var evidence = LyricsLanguagePolicy.SourceEvidence(separated);
        Assert.IsTrue(evidence[1].IsConfident);
        Assert.IsFalse(evidence[4].IsConfident);
        Assert.IsFalse(LyricsLanguagePolicy.EligibleIndices(separated, "en").Contains(4));
        var untimed = LyricsParser.Parse(string.Join('\n', separated.Lines.Select(line => line.Text)), LyricsProviderKind.QqMusic);
        CollectionAssert.AreEqual(new[] { "我在等你回来", "春夏秋冬", "我们继续向前" },
            LyricsLanguagePolicy.EligibleSegments(untimed, "en")[0]);
        Assert.IsEmpty(LyricsLanguagePolicy.EligibleIndices(Song("春夏秋冬", "千山万水", "一叶扁舟"), "en"));
    }

    [TestMethod]
    public void ChineseAndEnglishMinoritySegmentsRemainIndependentOfSongMajority()
    {
        var chinese = Song(Enumerable.Repeat("我在等你回来", 99).Append("We carry seven seeds across the bridge.").ToArray());
        CollectionAssert.AreEqual(new[] { 99 }, LyricsLanguagePolicy.EligibleIndices(chinese, "zh-Hans"));
        var english = Song(Enumerable.Repeat("We carry seven seeds across the bridge.", 99).Append("我在等你回来").ToArray());
        CollectionAssert.AreEqual(Enumerable.Range(0, 99).ToArray(), LyricsLanguagePolicy.EligibleIndices(english, "zh-Hans"));
        var mixed = Song("我在等你回来 We carry seven seeds across the bridge.");
        CollectionAssert.AreEqual(new[] { "We carry seven seeds across the bridge." }, LyricsLanguagePolicy.EligibleSegments(mixed, "zh-Hans")[0]);
    }

    [TestMethod]
    public void StructuredBilingualProductionCreditsAreExcludedWithoutEatingOrdinaryLyrics()
    {
        string[] roles = ["作词 Lyricist", "作曲 Composer", "编曲 Arranger", "制谱 Music Copyist", "音乐誊谱 Music Copyist",
            "乐队 Orchestra", "管弦乐队 Orchestra", "演唱 Artist", "电吉他 Electric Guitar", "民谣吉他 Acoustic Guitar",
            "录音棚 Recording Studio", "录音师 Recording Engineer", "混音师 Mixing Engineer", "母带制作 Mastering Engineer", "出品 Produced by"];
        foreach (var role in roles)
        {
            var text = role + "：张三 Example Musician";
            Assert.IsTrue(LyricsLanguagePolicy.IsCredit(text), text);
            Assert.IsFalse(LyricsLanguagePolicy.Identify(text).IsConfident, text);
            foreach (var target in new[] { "en", "zh-Hans" })
            {
                Assert.IsEmpty(LyricsLanguagePolicy.EligibleIndices(Song(text), target), text);
                Assert.AreEqual("credit", LyricsLanguagePolicy.MarkTranslationStates(Song(text), target).Lines[0].TranslationReason, text);
            }
        }
        Assert.IsFalse(LyricsLanguagePolicy.IsCredit("The orchestra carries my voice across the hall."));
        Assert.IsFalse(LyricsLanguagePolicy.IsCredit("录音棚 Recording Studio 是这段歌词里的字样"));
        Assert.IsFalse(LyricsLanguagePolicy.IsCredit("我在录音棚里等你"));
        var body = LyricsParser.Parse(roles[0] + "：Example Musician\n我在等你回来\n春夏秋冬\n我们继续向前", LyricsProviderKind.QqMusic);
        Assert.AreEqual(3, LyricsLanguagePolicy.EligibleSegments(body, "en")[0].Length);
    }

    [TestMethod]
    public void ExactAcceptedBilingualHeaderIsRemovedBeforeScriptSplitting()
    {
        var source = Song("山海 The Road To Our Home - Example Artist", "我在等你回来", "我们继续向前") with
        { Match = new("山海 The Road To Our Home", "Example Artist", "", 9, 12) };
        Assert.IsEmpty(LyricsLanguagePolicy.EligibleSegments(source, "en")[0]);
        Assert.IsEmpty(LyricsLanguagePolicy.EligibleSegments(source, "zh-Hans")[0]);
        var otherRecording = source with { Match = source.Match! with { Artist = "Different Artist" } };
        Assert.IsNotEmpty(LyricsLanguagePolicy.EligibleSegments(otherRecording, "zh-Hans")[0], "A mismatching header is not silently removed.");
    }

    [TestMethod]
    public void CapturedLocalQqResponseAdmitsOrdinaryRowsAndExcludesCreditsAndHeader()
    {
        var path = Environment.GetEnvironmentVariable("DROPSPACE_BETA15_LANGUAGE_SAMPLE");
        if (string.IsNullOrWhiteSpace(path)) Assert.Inconclusive("Local response capture is supplied only for the affected-song host check.");
        var text = File.ReadAllText(path);
        static string Metadata(string payload, string key) => payload.Split('\n').Select(line => line.Trim())
            .First(line => line.StartsWith("[" + key + ":", StringComparison.Ordinal))[(key.Length + 2)..^1].Trim();
        var source = LyricsParser.Parse(text, LyricsProviderKind.QqMusic) with
        { Match = new(Metadata(text, "ti"), Metadata(text, "ar"), "", 185, 12) };
        Assert.AreEqual(52, source.Lines.Count, "This check uses the original captured response, not invented model output.");
        var ordinary = Enumerable.Range(1, source.Lines.Count - 1).Where(id => !LyricsLanguagePolicy.IsCredit(source.Lines[id].Text)).ToArray();
        Assert.AreEqual(38, ordinary.Length);
        CollectionAssert.AreEqual(ordinary, LyricsLanguagePolicy.EligibleIndices(source, "en"));
        Assert.IsEmpty(LyricsLanguagePolicy.EligibleIndices(source, "zh-Hans"));
        var state = LyricsLanguagePolicy.MarkTranslationStates(source, "en");
        Assert.IsTrue(ordinary.All(id => state.Lines[id].TranslationState == LyricsLineTranslationState.Pending));
    }
}
