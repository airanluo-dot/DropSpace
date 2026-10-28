using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class LyricsCandidateRankingIntegrationTests
{
    private static readonly LyricsQuery Query = new("Song", "Artist", "Album", TimeSpan.FromSeconds(180));

    [TestMethod]
    public async Task RulesWithoutTranslationCloseThreeSecondsAfterFirstUsableBody()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var provider = new Provider(LyricsProviderKind.NetEase, async (query, report, token) =>
        {
            Interlocked.Increment(ref calls);
            report(Document(query, LyricsProviderKind.NetEase, 187, "first"));
            report(Document(query, LyricsProviderKind.NetEase, 180, "closer"));
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { cancelled.TrySetResult(); }
            return LyricsDocument.Empty;
        });
        var service = new LyricsService(new([provider]));
        var result = await service.QueryDetailedAsync(Query,
            new() { SearchRemainingProviders = false, BackupProvider = null }, default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("closer", result.Document.Match?.CandidateId);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var reused = await service.QueryDetailedAsync(Query,
            new() { SearchRemainingProviders = false, BackupProvider = null }, default).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual("closer", reused.Document.Match?.CandidateId);
        Assert.AreEqual(1, calls, "A completed bounded preferred decision must not repeat the provider scan on every play.");
    }

    [TestMethod]
    public async Task SixCandidatesPerSourceProduceBestFifteenWithoutInvalidatingAiSnapshot()
    {
        var kinds = new[] { LyricsProviderKind.NetEase, LyricsProviderKind.QqMusic, LyricsProviderKind.Kugou,
            LyricsProviderKind.Lrclib, LyricsProviderKind.Amll };
        var providers = kinds.Select(kind => new Provider(kind, (query, report, _) =>
        {
            for (var index = 0; index < 6; index++) report(Document(query, kind, 190 - index * 2, "recording-" + index));
            return Task.FromResult(Document(query, kind, 180, "recording-5"));
        }));
        var settings = new LyricsSettings { SelectionMode = LyricsSelectionMode.AiRanked, SearchRemainingProviders = true };
        var result = await new LyricsService(new(providers)).QueryDetailedAsync(Query, settings, default);
        Assert.AreEqual("recording-5", result.Document.Match?.CandidateId);
        Assert.AreEqual(15, result.SelectionCandidates.Candidates.Count);
        Assert.AreEqual(15, result.SelectionCandidates.PrunedCandidates);
        Assert.IsFalse(result.SelectionCandidates.Truncated);
        foreach (var group in result.SelectionCandidates.Candidates.GroupBy(candidate => candidate.Document.Provider))
            CollectionAssert.AreEquivalent(new[] { "recording-3", "recording-4", "recording-5" },
                group.Select(candidate => candidate.Document.Match!.CandidateId).ToArray());
        Assert.IsTrue(LyricsCandidateSelectionProtocol.TryBuild(Query, result.SelectionCandidates, "", settings, out _));
    }

    private static LyricsDocument Document(LyricsQuery query, LyricsProviderKind kind, double duration, string id) =>
        LyricsParser.Parse("[00:01]We sing together", kind).Bind(query, "Song", "Artist", "Album", duration, 12, id);

    private sealed class Provider(LyricsProviderKind kind,
        Func<LyricsQuery, Action<LyricsDocument>, CancellationToken, Task<LyricsDocument>> execute) : IProgressiveLyricsProvider
    {
        public LyricsProviderKind Kind => kind;
        public Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken token) => QueryAsync(query, token, _ => { });
        public Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken token, Action<LyricsDocument> reportCandidate) =>
            execute(query, reportCandidate, token);
    }
}
