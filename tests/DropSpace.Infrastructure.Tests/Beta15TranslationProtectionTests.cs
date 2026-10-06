using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Infrastructure.Tests;

/// <summary>Host policy/publication checks only. Controlled output is not model execution evidence.</summary>
[TestClass]
public sealed class Beta15TranslationProtectionTests
{
    private static LyricsDocument Song(params string[] text) => new(text.Select((value, id) =>
        new LyricsLine(TimeSpan.FromSeconds(id * 3), TimeSpan.FromSeconds(id * 3 + 3), value, null, [])).ToArray(),
        LyricsProviderKind.NetEase, new("Fixture", "Fixture artist", "Fixture album", text.Length * 3, 12, "fixture-recording", "fixture-track"));

    private static LyricsDocument ControlledOutput(LyricsDocument source, string target)
    {
        var segments = LyricsLanguagePolicy.EligibleSegments(source, target);
        return source with { Lines = source.Lines.Select((line, id) => segments[id].Length == 0 ? line : line with
        {
            Secondary = "受控宿主输出 " + id, TranslationOrigin = LyricsTranslationOrigin.LocalAi, TranslationLanguage = target,
            LocalAiAdmissionKey = LyricsLanguagePolicy.LocalAiAdmissionKey(line, target, segments[id]),
            TranslationState = LyricsLineTranslationState.Translated, TranslationReason = "local-ai-complete",
        }).ToArray() };
    }

    [TestMethod]
    public void LateNativeTranslationWinsOverCachedProgressAndFinalFrozenOutputs()
    {
        var source = Song("I will stay with you", "You are my home", "The night is full of stars");
        var output = ControlledOutput(source, "zh-Hans");
        var current = source with { Lines = source.Lines.Select((line, id) => id == 1 ? line with
        {
            Secondary = "你是我的归宿", TranslationOrigin = LyricsTranslationOrigin.Provider,
            TranslationLanguage = "zh-Hans", TranslationLanguageIsExplicit = true,
        } : line).ToArray() };
        Assert.AreEqual(LyricsTranslationOutput.SourceVersion(source), LyricsTranslationOutput.SourceVersion(current));
        // This checks the shared host boundary; real cache/progress/final UI
        // operation still requires the separate actual playback evidence.
        var result = LyricsTranslationOutput.Reconcile(current, source, output, "zh-Hans");
        Assert.AreEqual("你是我的归宿", result.Lines[1].Secondary);
        Assert.AreEqual(LyricsTranslationOrigin.Provider, result.Lines[1].TranslationOrigin);
        Assert.AreEqual("受控宿主输出 0", result.Lines[0].Secondary);
        Assert.AreEqual("受控宿主输出 2", result.Lines[2].Secondary);
        Assert.IsTrue(LyricsTranslationOutput.TryApply("""[{"id":1,"text":"不能覆盖来源译文"}]""", current, [1], "zh-Hans", out var protectedResult));
        Assert.AreEqual("你是我的归宿", protectedResult.Lines[1].Secondary);
        Assert.AreEqual(LyricsTranslationOrigin.Provider, protectedResult.Lines[1].TranslationOrigin);
    }

    [TestMethod]
    public void PublicationRejectsChangedSourceAndKeepsRepeatedOccurrencePositions()
    {
        var source = Song("I will stay with you", "I will stay with you");
        var output = ControlledOutput(source, "zh-Hans");
        Assert.AreNotEqual(LyricsTranslationOutput.LineIdentity(source, 0), LyricsTranslationOutput.LineIdentity(source, 1));
        var identicalTime = source with { Lines = [source.Lines[0], source.Lines[0]] };
        Assert.AreNotEqual(LyricsTranslationOutput.LineIdentity(identicalTime, 0), LyricsTranslationOutput.LineIdentity(identicalTime, 1));
        var latest = source with { Lines = [source.Lines[0] with
        {
            Secondary = "我会陪在你身边", TranslationOrigin = LyricsTranslationOrigin.Provider,
            TranslationLanguage = "zh-Hans", TranslationLanguageIsExplicit = true,
        }, source.Lines[1]] };
        var result = LyricsTranslationOutput.Reconcile(latest, source, output, "zh-Hans");
        Assert.AreEqual(LyricsTranslationOrigin.Provider, result.Lines[0].TranslationOrigin);
        Assert.AreEqual("受控宿主输出 1", result.Lines[1].Secondary);
        var reordered = output with { Lines = [output.Lines[1], output.Lines[0]] };
        Assert.IsTrue(LyricsTranslationOutput.Reconcile(source, source, reordered, "zh-Hans").Lines.All(line => line.Secondary is null));
        foreach (var changed in new[]
        {
            source with { Lines = [source.Lines[0] with { Text = "A changed original line" }, source.Lines[1]] },
            source with { Lines = [source.Lines[0] with { Start = TimeSpan.FromMilliseconds(100) }, source.Lines[1]] },
            source with { ProviderDataRevision = source.ProviderDataRevision + 1 },
            source with { Match = source.Match! with { CandidateId = "different-recording" } },
        }) Assert.AreSame(changed, LyricsTranslationOutput.Reconcile(changed, source, output, "zh-Hans"));
        Assert.AreSame(source, LyricsTranslationOutput.Reconcile(source, source, output with { Lines = [output.Lines[0]] }, "zh-Hans"));
    }

    [TestMethod]
    public void NativeShortHanTranslationsUseReliableCollectionContextAndKeepText()
    {
        var source = Song("I will stay with you", "You are my home", "The night is full of stars", "I can hear you", "You are my home", "I will stay with you");
        var translations = new[] { "我会陪在你身边", "花海", "我的世界充满阳光", "等", "寻觅", "星河" };
        var native = source with { Lines = source.Lines.Select((line, id) => line with
        { Secondary = translations[id], TranslationOrigin = LyricsTranslationOrigin.Provider }).ToArray() };
        var classified = LyricsLanguagePolicy.IdentifyProviderTranslations(native);
        Assert.IsTrue(classified.Lines.All(line => LyricsLanguagePolicy.HasTargetProviderTranslation(line, "zh-Hans")));
        Assert.IsEmpty(LyricsLanguagePolicy.EligibleIndices(classified, "zh-Hans"));
        var result = LyricsTranslationOutput.Reconcile(classified, source, ControlledOutput(source, "zh-Hans"), "zh-Hans");
        CollectionAssert.AreEqual(translations, result.Lines.Select(line => line.Secondary).ToArray());
        Assert.IsTrue(result.Lines.All(line => line.TranslationOrigin == LyricsTranslationOrigin.Provider));
        Assert.IsTrue(LyricsLanguagePolicy.MarkTranslationStates(classified, "zh-Hans").Lines.All(line =>
            line.TranslationState == LyricsLineTranslationState.Translated && line.TranslationReason == "provider-target-translation"));
    }

    [TestMethod]
    public void OpenVocabularyAndLocalMixedContextKeepSameLanguageAndUnknownProtection()
    {
        var ordinary = Song("We carry seven seeds across the bridge.", "I sculpted copper lanterns beneath the overpass.",
            "我把晨光折进一只纸船。", "你把潮声藏进凌晨的车站。");
        CollectionAssert.AreEqual(new[] { 0, 1 }, LyricsLanguagePolicy.EligibleIndices(ordinary, "zh-Hans"));
        CollectionAssert.AreEqual(new[] { 2, 3 }, LyricsLanguagePolicy.EligibleIndices(ordinary, "en"));
        var local = Song("我的世界充满阳光", "I sculpted copper lanterns beneath the overpass.", "lanterns",
            "I will stay with you", "君を忘れない", "我的世界充满阳光", "我把晨光折进一只纸船。");
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, LyricsLanguagePolicy.EligibleIndices(local, "zh-Hans"));
        var mixed = Song("我的世界充满阳光 I sculpted copper lanterns beneath the overpass.");
        CollectionAssert.AreEqual(new[] { "I sculpted copper lanterns beneath the overpass." }, LyricsLanguagePolicy.EligibleSegments(mixed, "zh-Hans")[0]);
        var uncertain = Song("Azur velorum", "星河", "Luo Airan", "kimi no na wa", "ooh oh");
        Assert.IsEmpty(LyricsLanguagePolicy.EligibleIndices(uncertain, "en"));
        Assert.IsEmpty(LyricsLanguagePolicy.EligibleIndices(uncertain, "zh-Hans"));
        var chineseMajority = Song(Enumerable.Repeat("我的世界充满阳光", 99).Append("We carry seven seeds across the bridge.").ToArray());
        CollectionAssert.AreEqual(new[] { 99 }, LyricsLanguagePolicy.EligibleIndices(chineseMajority, "zh-Hans"));
        var englishMajority = Song(Enumerable.Repeat("We carry seven seeds across the bridge.", 99).Append("我把晨光折进一只纸船。").ToArray());
        Assert.AreEqual(99, LyricsLanguagePolicy.EligibleIndices(englishMajority, "zh-Hans").Length);
    }

    [TestMethod]
    public void FrozenAdmissionSnapshotBindsCurrentHostPolicyWithoutInference()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "RELEASE_VERSION"))) root = root.Parent;
        Assert.IsNotNull(root);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var fixturePath = Path.Combine(root.FullName, "scripts/ai-model-qa/inputs/source48.json");
        var document = JsonSerializer.Deserialize<LyricsDocument>(File.ReadAllText(fixturePath), json)!;
        var evidence = LyricsLanguagePolicy.SourceEvidence(document);
        static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
        var targets = new Dictionary<string, object>();
        foreach (var target in new[] { "en", "zh-Hans" })
        {
            var ids = LyricsLanguagePolicy.EligibleIndices(document, target).ToHashSet();
            var segments = LyricsLanguagePolicy.EligibleSegments(document, target);
            var rows = document.Lines.Select((line, id) => new { lineId = id, sourceText = line.Text,
                detectedLanguage = evidence[id].Language, confidence = evidence[id].Confidence,
                evidenceKind = evidence[id].Kind.ToString(), eligible = ids.Contains(id),
                reason = ids.Contains(id) ? "identified-foreign-language" : evidence[id].IsConfident &&
                    LyricsLanguagePolicy.SameSourceLanguage(evidence[id].Language, target) ? "same-target-language" : "no-eligible-segments",
                segments = segments[id].Select((text, index) => new { segmentIndex = index, text, sha256 = Hash(Encoding.UTF8.GetBytes(text)) }).ToArray() }).ToArray();
            for (var id = 0; id < rows.Length; id++)
                if (evidence[id].IsConfident) Assert.AreEqual(id < 12 ? "en" : id < 24 ? "ja" : id < 36 ? "ko" : "zh-Hans", evidence[id].Language);
            targets[target] = rows;
        }
        using var prior = JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName, "scripts/plain-hy-production-evidence/source48-admission-v10.json")));
        var snapshot = new { schemaVersion = 1, recordKind = "host-fixture-admission-v12", fixtureSha256 = Hash(File.ReadAllBytes(fixturePath)),
            policyVersion = LyricsLanguagePolicy.Version,
            policySourceSha256 = Hash(Encoding.UTF8.GetBytes(File.ReadAllText(Path.Combine(root.FullName, "src/DropSpace.Core/Lyrics/LyricsLanguagePolicy.cs")).Replace("\r\n", "\n", StringComparison.Ordinal))),
            policySourceHashNormalization = "UTF-8 with CRLF normalized to LF", modelInferenceExecuted = false, semanticApproved = false,
            computedWith = "Beta15TranslationProtectionTests.FrozenAdmissionSnapshotBindsCurrentHostPolicyWithoutInference",
            reviewNotes = "Current host computation only; unchanged independent fixture language annotations retained. No model execution or semantic approval.",
            semanticLanguages = prior.RootElement.GetProperty("semanticLanguages"), targets };
        File.WriteAllText(Path.Combine(root.FullName, "scripts/plain-hy-production-evidence/source48-admission-v12.json"), JsonSerializer.Serialize(snapshot, json) + "\n");
    }
}
