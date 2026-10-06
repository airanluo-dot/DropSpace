using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DropSpace.Core.Island;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Core.Overlay;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class Beta14FocusedTests
{
    private static LyricsDocument Song(params string[] text) => new(text.Select((value, i) =>
        new LyricsLine(TimeSpan.FromSeconds(i * 3), TimeSpan.FromSeconds(i * 3 + 3), value, null, [])).ToArray(), LyricsProviderKind.NetEase);

    [TestMethod]
    public void ManualEmptyOpenRetiresPauseDeadlineAndStaleDismissal()
    {
        var time = new Clock();
        var coordinator = new IslandExperienceCoordinator(time);
        coordinator.UpdateSettings(new() { HideDelayMilliseconds = 2000 }, true);
        coordinator.UpdateMedia(true, true, 2000, contentIdentity: "song");
        coordinator.UpdateMedia(false, true, 2000);
        var oldDeadline = coordinator.Current.NextDeadline!.Value;
        var generation = coordinator.HideGeneration;
        time.Now += TimeSpan.FromSeconds(1);
        coordinator.UpdateMedia(false, true, 2000);
        Assert.AreEqual(oldDeadline, coordinator.Current.NextDeadline);
        coordinator.UpdateSettings(new() { HideDelayMilliseconds = 2000 }, false);
        coordinator.Open(IslandPage.Files);
        var machine = new OverlayStateMachine();
        machine.OpenQuickPanel(); machine.SetTemporaryItemCount(0);
        coordinator.UpdateFiles(machine.Snapshot);
        coordinator.UpdateFiles(new(OverlayState.Hidden, 0, false, 8,
            new(OverlayState.Dismissing, OverlayState.Hidden, OverlayTransitionCause.Dismissed, OverlayMotionPreference.System)));
        time.Now += TimeSpan.FromSeconds(2);
        coordinator.OnDeadline(generation, oldDeadline);
        Assert.AreEqual(OverlayState.Expanded, coordinator.Current.State);
        Assert.IsNull(coordinator.Current.NextDeadline);
        Assert.AreEqual(OverlayState.Expanded, machine.Snapshot.State);
        coordinator.Collapse();
        Assert.IsTrue(coordinator.Current.PendingHide);
        coordinator.DismissNow();
        Assert.AreEqual(OverlayState.Hidden, coordinator.Current.State);
    }

    [TestMethod]
    public async Task DisabledDiskCachePreservesFilesAndFencesBothStores()
    {
        using var fixture = new CacheFixture();
        var query = new LyricsQuery("song", "artist", "album", TimeSpan.FromSeconds(3));
        var source = Song("I will stay with you").Bind(query, "song", "artist", "album", 3, 12, "candidate");
        await fixture.Store.WriteDocumentAsync("old", source, fixture.Store.Generation, default);
        var originalFiles = Directory.GetFiles(fixture.Root);
        var generation = fixture.Store.Generation;
        await fixture.Store.SetMaximumBytesAsync(0);
        Assert.IsNull(await fixture.Store.ReadDocumentAsync("old", default));
        await fixture.Store.WriteDocumentAsync("late", source, generation, default);
        var aiKey = new string('b', 64);
        await fixture.Ai.WriteAsync(aiKey, "[]", generation, default);
        CollectionAssert.AreEquivalent(originalFiles, Directory.GetFiles(fixture.Root));
        await fixture.Store.SetMaximumBytesAsync(1_000_000_000);
        Assert.IsNotNull(await fixture.Store.ReadDocumentAsync("old", default));
        Assert.IsNull(await fixture.Ai.ReadAsync(aiKey, default));
    }

    [TestMethod]
    public void RecordingIdentityRejectsOtherVersionsAndCredits()
    {
        var golden = new LyricsQuery("golden hour", "JVKE", "this is what ____ feels like", TimeSpan.FromSeconds(209));
        Assert.AreEqual(0d, LyricsMatcher.Score(golden, "golden hour 初光", "JVKE; HENRY", "golden hour 初光", 209));
        Assert.AreEqual(0d, LyricsMatcher.CandidateScore(golden with { CollectSelectionCandidates = true }, "golden hour", "JVKE; HENRY", "golden hour 初光", 209));
        Assert.IsTrue(LyricsMatcher.Score(golden, "golden hour", "JVKE", golden.Album, 209) >= 4);
        var chinese = new LyricsQuery("如果可以", "韦礼安", "如果可以", TimeSpan.FromSeconds(274));
        Assert.AreEqual(0d, LyricsMatcher.Score(chinese, "赤い糸", "韦礼安", "赤い糸", 274));
        Assert.AreEqual(0d, LyricsMatcher.Score(chinese, "如果可以 (日语版)", "韦礼安", "赤い糸", 274));
        Assert.IsTrue(LyricsMatcher.Score(chinese, "如果可以", "韦礼安", "如果可以", 274) >= 4);
    }

    [TestMethod]
    public void ContextAdmitsShortRepeatsAndRetainsMixedLanguageBoundaries()
    {
        var english = Song("I will stay with you", "You are my home", "distant echoes", "Forever", "distant echoes", "我的世界充满阳光", "ooh oh", "Luo Airan");
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4 }, LyricsLanguagePolicy.EligibleIndices(english, "zh-Hans"));
        var chinese = Song("我的世界充满阳光", "我们还在这里", "等", "星河", "等", "I will stay with you");
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4 }, LyricsLanguagePolicy.EligibleIndices(chinese, "en"));
        CollectionAssert.AreEqual(new[] { 5 }, LyricsLanguagePolicy.EligibleIndices(chinese, "zh-Hans"));
        var mixed = Song("我的世界充满阳光 I will stay with you", "我们还在这里 You are my home");
        Assert.IsTrue(LyricsLanguagePolicy.EligibleSegments(mixed, "zh-Hans").SelectMany(x => x).All(x => !x.Contains('我')));
        var ninetyNine = Song(Enumerable.Repeat("我的世界充满阳光", 99).Append("I will stay with you").ToArray());
        CollectionAssert.AreEqual(new[] { 99 }, LyricsLanguagePolicy.EligibleIndices(ninetyNine, "zh-Hans"));
        var opposite = Song(Enumerable.Repeat("I will stay with you", 99).Append("我的世界充满阳光").ToArray());
        Assert.AreEqual(99, LyricsLanguagePolicy.EligibleIndices(opposite, "zh-Hans").Length);
    }

    [TestMethod]
    public async Task NativePartialTranslationDoesNotBlockAiAndReplayDoesNotInfer()
    {
        using var fixture = new CacheFixture();
        var source = Song("I will stay with you", "You are my home", "distant echoes", "You are my home");
        source = source with { Lines = source.Lines.Select((line, id) => id == 0 ? line with
        { Secondary = "我会陪在你身边", TranslationOrigin = LyricsTranslationOrigin.Provider,
            TranslationLanguage = "zh-Hans", TranslationLanguageIsExplicit = true } : line).ToArray() };
        var coordinator = new PlainHyLyricsCoordinator(fixture.Ai);
        var calls = 0;
        Task<string> Infer(string prompt, CancellationToken token)
        { calls++; return Task.FromResult(prompt.Contains("echoes", StringComparison.Ordinal) ? "遥远的回声" : "你是我的归宿"); }
        var identity = PlainHyLyricsProtocol.InferenceIdentity(new string('a', 64));
        var query = new LyricsQuery("song", "artist", "album", TimeSpan.FromSeconds(12));
        var result = await coordinator.TranslateAsync(query, source, "zh-Hans", identity, fixture.Ai.Generation, Infer, default);
        Assert.AreEqual(LyricsTranslationOutcome.Translated, result.Outcome);
        Assert.AreEqual(2, calls); // one repeated literal reuses its validated request-local result
        Assert.IsTrue(result.Document.Lines.All(l => l.TranslationState == LyricsLineTranslationState.Translated));
        Assert.AreEqual(LyricsTranslationOrigin.Provider, result.Document.Lines[0].TranslationOrigin);
        var cached = await coordinator.TryGetCachedAsync(query, source, "zh-Hans", identity, default);
        Assert.IsNotNull(cached);
        Assert.IsTrue(cached.FromCache);
        var replay = await coordinator.TranslateAsync(query, result.Document, "zh-Hans", identity, fixture.Ai.Generation, Infer, default);
        Assert.AreEqual(2, calls);
        Assert.AreEqual(LyricsTranslationOutcome.Translated, replay.Outcome);
    }

    [TestMethod]
    public async Task FailureRetainsUsefulProgressAndAccurateRowStates()
    {
        using var fixture = new CacheFixture();
        var coordinator = new PlainHyLyricsCoordinator(fixture.Ai);
        var calls = 0;
        var result = await coordinator.TranslateAsync(new("song", "artist", "album", TimeSpan.FromSeconds(6)),
            Song("I will stay with you", "You are my home"), "zh-Hans", PlainHyLyricsProtocol.InferenceIdentity(new string('a', 64)),
            fixture.Ai.Generation, (_, _) => Task.FromResult(++calls == 1 ? "我会陪在你身边" : ""), default);
        Assert.AreEqual(LyricsTranslationOutcome.Failed, result.Outcome);
        Assert.AreEqual(LyricsLineTranslationState.Translated, result.Document.Lines[0].TranslationState);
        Assert.AreEqual(LyricsLineTranslationState.Failed, result.Document.Lines[1].TranslationState);
        Assert.IsNotNull(result.Document.Lines[0].Secondary);
        Assert.AreEqual("invalid-worker-output", result.Document.Lines[1].TranslationReason);
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
        var old = JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName, "scripts/plain-hy-production-evidence/source48-admission-v9.json")));
        var snapshot = new { schemaVersion = 1, recordKind = "host-fixture-admission-v10", fixtureSha256 = Hash(File.ReadAllBytes(fixturePath)),
            policyVersion = LyricsLanguagePolicy.Version,
            policySourceSha256 = Hash(Encoding.UTF8.GetBytes(File.ReadAllText(Path.Combine(root.FullName, "src/DropSpace.Core/Lyrics/LyricsLanguagePolicy.cs")).Replace("\r\n", "\n", StringComparison.Ordinal))),
            policySourceHashNormalization = "UTF-8 with CRLF normalized to LF", modelInferenceExecuted = false, semanticApproved = false,
            computedWith = "Beta14FocusedTests.FrozenAdmissionSnapshotBindsCurrentHostPolicyWithoutInference",
            reviewNotes = "Current host computation only; unchanged independent fixture language annotations retained. No model execution or semantic approval.",
            semanticLanguages = old.RootElement.GetProperty("semanticLanguages"), targets };
        File.WriteAllText(Path.Combine(root.FullName, "scripts/plain-hy-production-evidence/source48-admission-v10.json"), JsonSerializer.Serialize(snapshot, json) + "\n");
    }

    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now = new(2026, 10, 6, 0, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class CacheFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "DropSpace-Beta14-" + Guid.NewGuid().ToString("N"));
        public LyricsCache Store { get; }
        public AiLyricsCache Ai { get; }
        public CacheFixture() { Directory.CreateDirectory(Root); Store = new(Root); Ai = new(Store); }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
