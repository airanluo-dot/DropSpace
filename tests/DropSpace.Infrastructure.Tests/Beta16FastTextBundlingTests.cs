using System.Text.Json;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class Beta16FastTextBundlingTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task PinnedWindowsBinUsesSingleInstanceWholeTrackSamplesAndProtectsTwoExistingReports()
    {
        var root = FindRepository();
        var model = Path.Combine(root, "artifacts", "lyrics-language", "lid.176.bin");
        if (!OperatingSystem.IsWindows() || !File.Exists(model))
            Assert.Inconclusive("The staged Windows x64 bin is required for this compatibility check.");
        using var identifier = new FastTextLanguageIdentifier(model);
        var english = Document("[00:01.00]I will carry the sunlight across the open water.",
            "Baby, Yeah, Frank Ocean, I remember all the nights we spent together.",
            "And every morning I will wait for you to come back home.");
        Assert.AreEqual("I will carry the sunlight across the open water. Baby, Yeah, Frank Ocean, I remember all the nights we spent together. And every morning I will wait for you to come back home.",
            LyricsLanguagePolicy.BuildSample(english, LyricsLanguageRole.Original));
        var englishUi = await identifier.PrepareAsync(english, "en-US", default);
        Assert.AreEqual(LyricsWholeTrackDecision.OriginalTarget, englishUi.TranslationAdmission!.Decision,
            englishUi.TranslationAdmission.OriginalPrediction.Diagnostic);
        Assert.AreEqual(1, identifier.PredictionCount);
        var chineseUi = await identifier.PrepareAsync(englishUi, "zh-CN", default);
        Assert.AreEqual(LyricsWholeTrackDecision.AllowAi, chineseUi.TranslationAdmission!.Decision);
        Assert.IsTrue(chineseUi.TranslationAdmission.OriginalPrediction.CacheHit);
        Assert.AreEqual(1, identifier.PredictionCount, "A UI change remaps the same true model prediction.");
        var chinese = await identifier.PrepareAsync(Document("我们一起走过每一个孤独的夜晚", "我会陪着你直到明天的太阳升起", "无论你在哪里，我都不会忘记你的微笑"), "zh", default);
        Assert.AreEqual(LyricsWholeTrackDecision.OriginalTarget, chinese.TranslationAdmission!.Decision);
        var japanese = await identifier.PrepareAsync(Document("君と歩いた夜の街を今も覚えている", "明日の光を探して僕らは旅を続ける"), "zh", default);
        Assert.AreEqual(LyricsWholeTrackDecision.AllowAi, japanese.TranslationAdmission!.Decision);
        TestContext.WriteLine("Single-instance synthetic compatibility: en label={0}, score={1}; zh label={2}, score={3}; non-target label={4}, score={5}.",
            englishUi.TranslationAdmission.OriginalPrediction.Label, englishUi.TranslationAdmission.OriginalPrediction.Score,
            chinese.TranslationAdmission.OriginalPrediction.Label, chinese.TranslationAdmission.OriginalPrediction.Score,
            japanese.TranslationAdmission.OriginalPrediction.Label, japanese.TranslationAdmission.OriginalPrediction.Score);

        foreach (var relative in new[] { "artifacts/lyrics-followup-20261006/die-for-you-source-cache.json", ".codex/beta15/evidence/her-source-beta14.json" })
        {
            var path = Path.Combine(root, relative);
            if (!File.Exists(path)) continue; // Local report fixtures are intentionally not release source.
            var source = JsonSerializer.Deserialize<LyricsDocument>(await File.ReadAllTextAsync(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            var prepared = await identifier.PrepareAsync(source, "zh", default);
            Assert.AreEqual(LyricsWholeTrackDecision.ProviderTarget, prepared.TranslationAdmission!.Decision, relative);
            Assert.IsEmpty(LyricsLanguagePolicy.EligibleIndices(prepared, "zh"));
            CollectionAssert.AreEqual(source.Lines.Select(line => line.Secondary).ToArray(), prepared.Lines.Select(line => line.Secondary).ToArray());
            Assert.AreEqual(source.Lines.Count(line => string.IsNullOrWhiteSpace(line.Secondary)),
                prepared.Lines.Count(line => string.IsNullOrWhiteSpace(line.Secondary)), "Native gaps stay empty.");
            TestContext.WriteLine("Existing report {0}: native={1}, blank={2}, AI eligible=0, original label={3}, score={4}, provider label={5}, score={6}.",
                source.Match?.Title, source.Lines.Count(line => !string.IsNullOrWhiteSpace(line.Secondary)),
                source.Lines.Count(line => string.IsNullOrWhiteSpace(line.Secondary)),
                prepared.TranslationAdmission.OriginalPrediction.Label, prepared.TranslationAdmission.OriginalPrediction.Score,
                prepared.TranslationAdmission.TranslationPrediction?.Label, prepared.TranslationAdmission.TranslationPrediction?.Score);
        }
        Assert.AreEqual(1, identifier.ModelLoadCount);
        TestContext.WriteLine("ModelLoadCount={0}; PredictionCount={1}. This is compatibility and admission confirmation, not a quality or performance benchmark.",
            identifier.ModelLoadCount, identifier.PredictionCount);
    }

    private static LyricsDocument Document(params string[] lines) => new(lines.Select((text, index) =>
        new LyricsLine(TimeSpan.FromSeconds(index), TimeSpan.FromSeconds(index + 1), text, null, [])).ToArray(), LyricsProviderKind.LocalLrc);
    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "DropSpace.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Repository root is unavailable.");
    }
}
