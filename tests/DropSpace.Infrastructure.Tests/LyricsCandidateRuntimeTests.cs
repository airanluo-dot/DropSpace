using System.Diagnostics;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class LyricsCandidateRuntimeTests
{
    private static readonly LyricsQuery Query = new("Song", "Artist", "Album", TimeSpan.FromSeconds(180), "track");
    private static LyricsDocument Doc(LyricsProviderKind kind, string artist, string id) =>
        LyricsParser.Parse("[00:01]The night is full of stars.", kind).Bind(Query, "Song", artist, "Album", 180, 12, id);
    private static LyricsCandidateSnapshot Snapshot(LyricsDocument rules, LyricsDocument uncertain) => new(
        [LyricsCandidateRules.Describe("c0", rules, "zh"), LyricsCandidateRules.Describe("c1", uncertain, "zh")],
        Stopwatch.GetTimestamp() + 3 * Stopwatch.Frequency);

    [TestMethod]
    public async Task SuccessfulAllowlistedSelectionIsRealAndOnlySuccessIsCached()
    {
        var rules = Doc(LyricsProviderKind.NetEase, "Artist", "rule");
        var uncertain = Doc(LyricsProviderKind.QqMusic, "Unresolved credit", "uncertain");
        uncertain = uncertain with { Lines = uncertain.Lines.Select(line => line with { Secondary = "夜里满布繁星", TranslationOrigin = LyricsTranslationOrigin.Provider,
            TranslationLanguage = "zh", TranslationLanguageIsExplicit = true }).ToArray() };
        var runtime = new Runtime();
        var selector = new LyricsCandidateSelector(runtime);
        var settings = new LyricsSettings { Enabled = true, SelectionMode = LyricsSelectionMode.AiRanked };
        var snapshot = Snapshot(rules, uncertain);
        var selected = await selector.SelectAsync(Query, settings, "zh", "model", snapshot, rules, default);
        Assert.AreEqual(LyricsSelectionOutcome.Selected, selected.Outcome);
        Assert.AreEqual("uncertain", selected.Document.Match!.CandidateId);
        Assert.AreEqual(LyricsSelectionOutcome.Reused, (await selector.SelectAsync(Query, settings, "zh", "model", snapshot, rules, default)).Outcome);
        Assert.AreEqual(1, runtime.Calls);
        selector.Clear(); runtime.Output = "{\"id\":\"invented\"}";
        Assert.AreEqual(LyricsSelectionOutcome.Invalid, (await selector.SelectAsync(Query, settings, "zh", "model", snapshot, rules, default)).Outcome);
        runtime.Output = "{\"id\":\"c1\"}";
        Assert.AreEqual(LyricsSelectionOutcome.Selected, (await selector.SelectAsync(Query, settings, "zh", "model", snapshot, rules, default)).Outcome);
        Assert.AreEqual(3, runtime.Calls);
        runtime.Block = true;
        var shortBudget = snapshot with { DeadlineTimestamp = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 20 };
        var timedOut = await selector.SelectAsync(Query, settings, "zh", "different-model", shortBudget, rules, default);
        Assert.AreEqual(LyricsSelectionOutcome.TimedOut, timedOut.Outcome);
        Assert.AreSame(rules, timedOut.Document);
    }

    [TestMethod]
    public async Task FullCollectionRetainsUncertainArtistWithoutLeakingItIntoRulesOrPreview()
    {
        var rules = Doc(LyricsProviderKind.NetEase, "Artist", "rule");
        var uncertain = Doc(LyricsProviderKind.QqMusic, "Unresolved credit", "uncertain");
        var previews = new List<LyricsDocument>();
        var service = new LyricsService(new([new Provider(LyricsProviderKind.NetEase, rules), new Provider(LyricsProviderKind.QqMusic, uncertain)]));
        var settings = new LyricsSettings { Enabled = true, SelectionMode = LyricsSelectionMode.AiRanked,
            BackupProvider = LyricsProviderKind.QqMusic, SearchRemainingProviders = false };
        var full = await service.QueryDetailedAsync(Query, settings, default, reportOriginal: previews.Add);
        Assert.AreEqual(2, full.SelectionCandidates.Candidates.Count);
        Assert.AreEqual("rule", full.Document.Match!.CandidateId);
        Assert.IsTrue(previews.All(document => document.Match!.CandidateId == "rule"));
        var assisted = await service.QueryDetailedAsync(Query, settings with { SelectionMode = LyricsSelectionMode.AiAssisted }, default);
        Assert.IsTrue(assisted.SelectionCandidates.Candidates.Any(candidate => candidate.Document.Match!.CandidateId == "uncertain"));
        Assert.AreEqual(0d, LyricsMatcher.CandidateScore(Query with { CollectSelectionCandidates = true }, "Song (Live)", "Unresolved credit", "Album", 180));
        Assert.AreEqual(0d, LyricsMatcher.CandidateScore(Query with { CollectSelectionCandidates = true }, "Song", "Unresolved credit", "Album", 300));
    }

    [TestMethod]
    public async Task ColdSelectionNeverResolvesStartsOrQueuesWorker()
    {
        var resolves = 0;
        using var runner = new PersistentPlainLyricsRunner((_, _) => { resolves++; return Task.FromResult("unused"); },
            new(), TimeSpan.FromSeconds(60));
        Assert.IsFalse(runner.IsSelectionWarm(AiLyricsModelCatalog.ExperimentalPlain.Sha256));
        Assert.IsNull(await runner.TryRunSelectionAsync(AiLyricsModelCatalog.ExperimentalPlain.Sha256, "metadata", default));
        Assert.AreEqual(0, resolves);
        await runner.DrainCleanupAsync(default);
    }

    private sealed class Runtime : ILyricsSelectionRuntime
    {
        public bool CanPrepareSelection => true;
        public int Calls;
        public string Output = "{\"id\":\"c1\"}";
        public bool Block;
        public bool IsSelectionWarm(string modelHash) => true;
        public Task<bool> PrepareSelectionAsync(string verifiedModelPath, string modelHash, CancellationToken token) => Task.FromResult(true);
        public async Task<string?> TryRunSelectionAsync(string modelHash, string prompt, CancellationToken token)
        { Calls++; if (Block) await Task.Delay(Timeout.Infinite, token); return Output; }
    }
    private sealed class Provider(LyricsProviderKind kind, LyricsDocument document) : IProgressiveLyricsProvider
    {
        public LyricsProviderKind Kind => kind;
        public Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken token) => Task.FromResult(document);
        public Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken token, Action<LyricsDocument> report)
        { report(document); return Task.FromResult(document); }
    }
}
