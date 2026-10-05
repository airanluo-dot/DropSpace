using System.Diagnostics;
using DropSpace.App.Services.Media;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;
using DropSpace.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class AiLyricsSelectionLifecycleTests
{
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(3);
    private static readonly LyricsQuery Query = new("Song", "Artist", "Album", TimeSpan.FromSeconds(180), "service:track")
    { PreferredTranslationLanguage = "zh" };
    private static readonly LyricsSettings Settings = new() { SelectionMode = LyricsSelectionMode.AiRanked,
        BackupProvider = LyricsProviderKind.QqMusic, SearchRemainingProviders = false, AiTranslationEnabled = true };

    [TestMethod]
    [DataRow("invalidate")]
    [DataRow("clear")]
    [DataRow("model")]
    [DataRow("cancel")]
    public async Task SelectedAndReusedServiceDecisionsRetainRetirementFenceUntilUiCommit(string retirement)
    {
        using var fixture = new Fixture();
        using var stop = new CancellationTokenSource();
        var source = Source();
        var selected = await fixture.Service.SelectCandidateAsync(Query, source, Settings, "zh", stop.Token);
        var reused = await fixture.Service.SelectCandidateAsync(Query, source, Settings, "zh", stop.Token);
        Assert.AreEqual(LyricsSelectionOutcome.Selected, selected.Outcome);
        Assert.AreEqual(LyricsSelectionOutcome.Reused, reused.Outcome);
        Assert.IsTrue(selected.IsCurrent && reused.IsCurrent);
        Assert.AreEqual(1, fixture.Backend.SelectionCalls);
        switch (retirement)
        {
            case "invalidate": fixture.Service.InvalidateSelectionDecisions(); break;
            case "clear": await fixture.Service.ClearCacheAsync(default); break;
            case "model": await fixture.Service.DeleteModelAsync(AiLyricsSelectionModelCatalog.Default.Id, default); break;
            case "cancel": stop.Cancel(); break;
        }
        Assert.IsFalse(selected.IsCurrent, "A selected document waiting in the UI queue must recheck retirement.");
        Assert.IsFalse(reused.IsCurrent, "A reused document needs the same retirement checks as fresh inference.");
        var fresh = await fixture.Service.SelectCandidateAsync(Query, Source(), Settings, "zh", default);
        Assert.AreEqual(retirement == "cancel" ? LyricsSelectionOutcome.Reused : LyricsSelectionOutcome.Selected, fresh.Outcome);
        Assert.IsTrue(fresh.IsCurrent);
        Assert.AreEqual(retirement == "cancel" ? 1 : 2, fixture.Backend.SelectionCalls,
            "Cancellation after success retires publication; maintenance also retires the decision cache.");
    }

    [TestMethod]
    public async Task ServiceRetirementRejectsInferenceStillRunningAndDoesNotRepopulateDecisionCache()
    {
        using var fixture = new Fixture();
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Backend.Infer = _ => { entered.TrySetResult(); return release.Task; };
        var source = Source();
        var pending = fixture.Service.SelectCandidateAsync(Query, source, Settings, "zh", default);
        await entered.Task.WaitAsync(WaitBudget);
        fixture.Service.InvalidateSelectionDecisions();
        release.SetResult("{\"id\":\"c0\"}");
        var stale = await pending.WaitAsync(WaitBudget);
        Assert.AreEqual(LyricsSelectionOutcome.Unavailable, stale.Outcome);
        Assert.AreSame(source.Document, stale.Document);
        Assert.IsFalse(stale.IsCurrent);
        var fresh = await fixture.Service.SelectCandidateAsync(Query, Source(), Settings, "zh", default);
        Assert.AreEqual(LyricsSelectionOutcome.Selected, fresh.Outcome);
        Assert.AreEqual(2, fixture.Backend.SelectionCalls);
    }

    [TestMethod]
    [DataRow(LyricsSelectionMode.AiAssisted)]
    [DataRow(LyricsSelectionMode.AiRanked)]
    public async Task PersistentTranslatedPreviewContinuesWeakCollectionAndBypassesAllTranslationWork(LyricsSelectionMode mode)
    {
        using var fixture = new Fixture();
        var translated = Source().Document with { Lines = [Source().Document.Lines[0] with
        {
            Secondary = "夜里满布繁星", TranslationOrigin = LyricsTranslationOrigin.Provider,
            TranslationLanguage = "zh", TranslationLanguageIsExplicit = true,
        }] };
        var weak = translated with { Provider = LyricsProviderKind.QqMusic,
            Match = translated.Match! with { Artist = "Unresolved credit", CandidateId = "weak" } };
        var primary = new Provider(LyricsProviderKind.NetEase, translated);
        var backup = new Provider(LyricsProviderKind.QqMusic, weak);
        var registry = new LyricsProviderRegistry([primary, backup]);
        await new LyricsService(registry, fixture.Cache).QueryDetailedAsync(Query,
            Settings with { SelectionMode = LyricsSelectionMode.Rules }, default);
        Assert.AreEqual(1, primary.Calls);
        Assert.AreEqual(0, backup.Calls, "A rules-mode native translation requires no supplemental lookup.");
        var previews = new List<LyricsDocument>();
        primary.OnQuery = () => Assert.AreEqual(1, previews.Count,
            "The trusted disk-cache preview must be shown before the fresh provider call starts.");
        // A new instance ensures this preview really comes from the disk cache.
        var result = await new LyricsService(registry, new LyricsCache(fixture.CacheDirectory)).QueryDetailedAsync(
            Query, Settings with { SelectionMode = mode }, default, reportOriginal: previews.Add);
        Assert.AreEqual(2, primary.Calls);
        Assert.AreEqual(1, backup.Calls);
        Assert.AreEqual(2, result.SelectionCandidates.Candidates.Count);
        Assert.IsTrue(previews.Count > 0 && previews.All(document => document.Match!.CandidateId == "native"));
        Assert.AreEqual("夜里满布繁星", result.Document.Lines[0].Secondary);
        var publication = await fixture.Service.TranslateForPublicationAsync(Query, result.Document,
            Settings with { SelectionMode = mode }, "zh", default);
        Assert.IsTrue(publication.IsCurrent);
        Assert.AreEqual("The night is full of stars.", publication.Document.Lines[0].Text);
        Assert.AreEqual("夜里满布繁星", publication.Document.Lines[0].Secondary);
        Assert.AreEqual(0, fixture.Resolver.Calls);
        Assert.AreEqual(0, fixture.Backend.CacheCalls);
        Assert.AreEqual(0, fixture.Backend.TranslationCalls);
        Assert.AreEqual(0, fixture.Backend.SelectionCalls);
        Assert.AreEqual(0, fixture.Backend.Drains);
    }

    private static LyricsQueryResult Source()
    {
        var document = LyricsParser.Parse("[00:01]The night is full of stars.", LyricsProviderKind.NetEase)
            .Bind(Query, Query.Title, Query.Artist, Query.Album, 180, 12, "native");
        return new(document, LyricsQueryStatus.Found)
        {
            SelectionCandidates = new([LyricsCandidateRules.Describe("c0", document, "zh")],
                Stopwatch.GetTimestamp() + 12 * Stopwatch.Frequency),
        };
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "DropSpace-selection-service-" + Guid.NewGuid().ToString("N"));
        internal Backend Backend { get; } = new();
        internal Resolver Resolver { get; } = new();
        internal LyricsCache Cache { get; }
        internal string CacheDirectory { get; }
        internal AiLyricsService Service { get; }
        internal Fixture()
        {
            var paths = new AppStoragePaths(_root);
            CacheDirectory = paths.Lyrics;
            Cache = new(paths.Lyrics);
            Service = new(paths, Cache, NullLogger<AiLyricsService>.Instance, null, Resolver, Backend);
        }
        public void Dispose()
        {
            Service.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }

    private sealed class Backend : IAiLyricsBackend, ILyricsSelectionRuntime
    {
        internal int SelectionCalls, TranslationCalls, CacheCalls, Drains;
        internal Func<CancellationToken, Task<string?>> Infer { get; set; } = _ => Task.FromResult<string?>("{\"id\":\"c0\"}");
        public string Id => "selection-lifecycle-fixture";
        public bool CanPrepareSelection => true;
        public bool IsSelectionWarm(string hash) => true;
        public Task<bool> PrepareSelectionAsync(string path, string hash, CancellationToken token) => Task.FromResult(true);
        public Task<string?> TryRunSelectionAsync(string hash, string prompt, CancellationToken token)
        { SelectionCalls++; return Infer(token); }
        public Task<LyricsTranslationResult?> TryGetCachedResultAsync(string id, LyricsQuery query,
            LyricsDocument source, string target, CancellationToken token)
        { CacheCalls++; throw new AssertFailedException("Native translations must bypass translation cache preflight."); }
        public Task<LyricsTranslationResult> TranslateAsync(AiLyricsResolvedPackage package, LyricsQuery query,
            LyricsDocument source, string target, CancellationToken token)
        { TranslationCalls++; throw new AssertFailedException("Native translations must never invoke the translator."); }
        public Task DrainCleanupAsync(CancellationToken token) { Drains++; return Task.CompletedTask; }
        public void Dispose() { }
    }

    private sealed class Resolver : IAiLyricsPackageResolver
    {
        internal int Calls;
        public Task<AiLyricsResolvedPackage?> ResolveAsync(string id, LyricsQuery query, LyricsDocument source,
            string target, CancellationToken token)
        { Calls++; throw new AssertFailedException("Native translations must not resolve a translation model."); }
    }

    private sealed class Provider(LyricsProviderKind kind, LyricsDocument document) : ILyricsProvider
    {
        internal int Calls;
        internal Action? OnQuery { get; set; }
        public LyricsProviderKind Kind => kind;
        public Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken token)
        { Calls++; OnQuery?.Invoke(); return Task.FromResult(document); }
    }
}
