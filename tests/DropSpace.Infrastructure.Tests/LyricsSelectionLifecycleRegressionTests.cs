using System.Collections.Concurrent;
using System.Diagnostics;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class LyricsSelectionLifecycleRegressionTests
{
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(3);
    private static readonly LyricsQuery Query = new("Song", "Artist", "Album", TimeSpan.FromSeconds(180), "lifecycle:track")
    { PreferredTranslationLanguage = "zh" };

    [TestMethod]
    [DataRow(LyricsSelectionMode.AiAssisted)]
    [DataRow(LyricsSelectionMode.AiRanked)]
    public async Task WeakPrimaryStartsSupplementalCollectionWithoutPublishingItsOriginal(LyricsSelectionMode mode)
    {
        var weak = Document(Query, LyricsProviderKind.NetEase, "Unresolved credit", "weak");
        var trusted = Document(Query, LyricsProviderKind.QqMusic, "Artist", "trusted");
        Assert.AreEqual(0d, LyricsMatcher.Score(Query, "Song", "Unresolved credit", "Album", 180));
        Assert.AreEqual(4d, LyricsMatcher.CandidateScore(Query with { CollectSelectionCandidates = true },
            "Song", "Unresolved credit", "Album", 180));
        var backupEntered = Signal();
        var releasePrimary = Signal();
        var releaseBackup = Signal();
        var firstTrustedPreview = Signal();
        var previews = new ConcurrentQueue<LyricsDocument>();
        var service = new LyricsService(new([
            new Provider(LyricsProviderKind.NetEase, async (_, token, report) =>
            {
                report(weak);
                await releasePrimary.Task.WaitAsync(token);
                return weak;
            }),
            new Provider(LyricsProviderKind.QqMusic, async (_, token, _) =>
            {
                backupEntered.TrySetResult();
                await releaseBackup.Task.WaitAsync(token);
                return trusted;
            }),
        ]));
        var settings = new LyricsSettings { SelectionMode = mode, BackupProvider = LyricsProviderKind.QqMusic,
            SearchRemainingProviders = false };
        using var stop = new CancellationTokenSource();
        try
        {
            var pending = service.QueryDetailedAsync(Query, settings, stop.Token, reportOriginal: document =>
            {
                previews.Enqueue(document);
                firstTrustedPreview.TrySetResult();
            });
            await backupEntered.Task.WaitAsync(WaitBudget);
            Assert.IsTrue(previews.IsEmpty, "Collecting a weak identity must not show its original before confirmation.");
            Assert.IsFalse(pending.IsCompleted, "A weak primary must admit supplemental work while still running.");
            releaseBackup.SetResult();
            await firstTrustedPreview.Task.WaitAsync(WaitBudget);
            Assert.IsTrue(previews.All(document => document.Match!.CandidateId == "trusted"));
            releasePrimary.SetResult();
            var result = await pending.WaitAsync(WaitBudget);
            Assert.AreEqual("trusted", result.Document.Match!.CandidateId);
            Assert.AreEqual(2, result.SelectionCandidates.Candidates.Count);
        }
        finally { await stop.CancelAsync(); releasePrimary.TrySetResult(); releaseBackup.TrySetResult(); }
    }

    [TestMethod]
    [DataRow(LyricsSelectionMode.AiAssisted)]
    [DataRow(LyricsSelectionMode.AiRanked)]
    public async Task CancelledProgressiveTrackCannotAddLatePreviewOrOverwriteSourceCache(LyricsSelectionMode mode)
    {
        var oldQuery = Query with { TrackIdentity = "lifecycle:old", Title = "Old" };
        var oldPreview = Signal();
        var releaseOld = Signal();
        var oldReportedLate = Signal();
        var previews = new ConcurrentQueue<LyricsDocument>();
        var calls = 0;
        var provider = new Provider(LyricsProviderKind.NetEase, async (query, _, report) =>
        {
            Interlocked.Increment(ref calls);
            var document = Document(query, LyricsProviderKind.NetEase, "Artist", query.Title) with
            { Lines = [new(TimeSpan.Zero, TimeSpan.FromSeconds(10), "我们带着蓝色雨伞", null, [])] };
            if (query.Title == "Old")
            {
                report(document);
                await releaseOld.Task; // Deliberately ignore cancellation, like a retiring transport.
                report(document with { Lines = [document.Lines[0] with { Text = "过时的晚到响应" }] });
                oldReportedLate.TrySetResult();
            }
            return document;
        });
        var service = new LyricsService(new([provider]));
        var settings = new LyricsSettings { SelectionMode = mode, SearchRemainingProviders = false };
        using var stop = new CancellationTokenSource();
        try
        {
            var old = service.QueryDetailedAsync(oldQuery, settings, stop.Token, reportOriginal: document =>
            {
                previews.Enqueue(document);
                oldPreview.TrySetResult();
            });
            await oldPreview.Task.WaitAsync(WaitBudget);
            stop.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => old.WaitAsync(WaitBudget));
            var current = await service.QueryDetailedAsync(Query, settings, default).WaitAsync(WaitBudget);
            releaseOld.SetResult();
            await oldReportedLate.Task.WaitAsync(WaitBudget);
            Assert.AreEqual(1, previews.Count, "The retired provider callback must never produce another preview.");
            var cached = await service.QueryDetailedAsync(Query, settings with { SelectionMode = LyricsSelectionMode.Rules }, default);
            Assert.AreEqual(current.Document.Match!.CandidateId, cached.Document.Match!.CandidateId);
            Assert.AreEqual("我们带着蓝色雨伞", cached.Document.Lines[0].Text);
            Assert.AreEqual(2, calls, "The current source cache must survive the old provider's late completion.");
            await service.QueryDetailedAsync(oldQuery, settings with { SelectionMode = LyricsSelectionMode.Rules }, default);
            Assert.AreEqual(3, calls, "The cancelled track must not persist even its earlier valid original.");
        }
        finally { releaseOld.TrySetResult(); }
    }

    [TestMethod]
    [DataRow("clear")]
    [DataRow("owner")]
    [DataRow("cancel")]
    public async Task InferenceFinishingAfterRetirementCannotCacheSuccess(string retirement)
    {
        var document = Document(Query, LyricsProviderKind.NetEase, "Artist", "native");
        var snapshot = Snapshot(document);
        var runtime = new Runtime();
        var selector = new LyricsCandidateSelector(runtime);
        var settings = new LyricsSettings { SelectionMode = LyricsSelectionMode.AiRanked };
        var ownerCurrent = true;
        using var stop = new CancellationTokenSource();
        var pending = selector.SelectAsync(Query, settings, "zh", "model", snapshot, document, stop.Token,
            () => ownerCurrent);
        await runtime.Entered.Task.WaitAsync(WaitBudget);
        if (retirement == "clear") selector.Clear();
        else if (retirement == "owner") ownerCurrent = false;
        else stop.Cancel();
        runtime.Release.SetResult("{\"id\":\"c0\"}");
        if (retirement == "cancel")
            await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(WaitBudget));
        else
        {
            var rejected = await pending.WaitAsync(WaitBudget);
            Assert.AreEqual(LyricsSelectionOutcome.Unavailable, rejected.Outcome);
            Assert.AreSame(document, rejected.Document);
        }
        var fresh = await selector.SelectAsync(Query, settings, "zh", "model", Snapshot(document), document, default);
        Assert.AreEqual(LyricsSelectionOutcome.Selected, fresh.Outcome,
            "The retired result must not populate a decision which the next owner could reuse.");
        Assert.AreEqual(2, runtime.Calls);
        if (retirement == "cancel")
        {
            using var finalStop = new CancellationTokenSource();
            var reused = await selector.SelectAsync(Query, settings, "zh", "model", Snapshot(document), document, finalStop.Token);
            Assert.AreEqual(LyricsSelectionOutcome.Reused, reused.Outcome);
            finalStop.Cancel();
            Assert.IsFalse(reused.IsCurrent, "Cancellation after return must fence a queued success too.");
        }
    }

    [TestMethod]
    public async Task TimedOutUncooperativeInferenceCannotPublishOrBecomeReusableSuccess()
    {
        var document = Document(Query, LyricsProviderKind.NetEase, "Artist", "native");
        var runtime = new Runtime();
        var selector = new LyricsCandidateSelector(runtime);
        var settings = new LyricsSettings { SelectionMode = LyricsSelectionMode.AiRanked };
        var snapshot = Snapshot(document) with { DeadlineTimestamp = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 10 };
        var expired = await selector.SelectAsync(Query, settings, "zh", "model", snapshot, document, default).WaitAsync(WaitBudget);
        Assert.AreEqual(LyricsSelectionOutcome.TimedOut, expired.Outcome);
        Assert.AreSame(document, expired.Document);
        runtime.Release.SetResult("{\"id\":\"c0\"}");
        var fresh = await selector.SelectAsync(Query, settings, "zh", "model", Snapshot(document), document, default);
        Assert.AreEqual(LyricsSelectionOutcome.Selected, fresh.Outcome);
        Assert.AreEqual(2, runtime.Calls, "Late valid output after timeout must not be cached.");
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static LyricsDocument Document(LyricsQuery query, LyricsProviderKind provider, string artist, string id) =>
        LyricsParser.Parse("[00:01]The night is full of stars.", provider)
            .Bind(query, query.Title, artist, query.Album, query.Duration.TotalSeconds, 12, id);
    private static LyricsCandidateSnapshot Snapshot(LyricsDocument document) => new(
        [LyricsCandidateRules.Describe("c0", document, "zh")], Stopwatch.GetTimestamp() + 12 * Stopwatch.Frequency);

    private sealed class Runtime : ILyricsSelectionRuntime
    {
        internal int Calls;
        internal TaskCompletionSource Entered { get; } = Signal();
        internal TaskCompletionSource<string?> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CanPrepareSelection => true;
        public bool IsSelectionWarm(string hash) => true;
        public Task<bool> PrepareSelectionAsync(string path, string hash, CancellationToken token) => Task.FromResult(true);
        public Task<string?> TryRunSelectionAsync(string hash, string prompt, CancellationToken token)
        {
            Interlocked.Increment(ref Calls);
            Entered.TrySetResult();
            return Release.Task; // Retain a late valid response independently of cancellation.
        }
    }

    private sealed class Provider(LyricsProviderKind kind,
        Func<LyricsQuery, CancellationToken, Action<LyricsDocument>, Task<LyricsDocument>> query) : IProgressiveLyricsProvider
    {
        public LyricsProviderKind Kind => kind;
        public Task<LyricsDocument> QueryAsync(LyricsQuery request, CancellationToken token) => query(request, token, _ => { });
        public Task<LyricsDocument> QueryAsync(LyricsQuery request, CancellationToken token, Action<LyricsDocument> report) =>
            query(request, token, report);
    }
}
