using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class LyricsTranslationPreferenceTests
{
    private static readonly LyricsQuery Query = new("Song", "Artist", "", TimeSpan.FromSeconds(180)) { PreferredTranslationLanguage = "zh-Hans" };
    private static LyricsDocument Doc(LyricsProviderKind kind, bool translated = false) =>
        new LyricsDocument([new(TimeSpan.Zero, TimeSpan.FromSeconds(10), "The night is full of stars.", translated ? "我们一起走向明天。" : null, [])
        { TranslationOrigin = translated ? LyricsTranslationOrigin.Provider : LyricsTranslationOrigin.None }], kind)
        .Bind(Query, Query.Title, Query.Artist, "", 180, 12, kind.ToString());

    [TestMethod]
    public async Task ConfiguredBackupTranslationBeatsPrimaryOriginalWithoutRemainingSearch()
    {
        var a = new Provider(LyricsProviderKind.NetEase, () => Task.FromResult(Doc(LyricsProviderKind.NetEase)));
        var b = new Provider(LyricsProviderKind.QqMusic, () => Task.FromResult(Doc(LyricsProviderKind.QqMusic, true)));
        var s = new LyricsService(new([a,b]));
        var result = await s.QueryDetailedAsync(Query, new() { Enabled=true, BackupProvider=LyricsProviderKind.QqMusic }, default);
        Assert.AreEqual(LyricsProviderKind.QqMusic, result.Document.Provider);
        Assert.IsTrue(LyricsTranslationPolicy.HasMatchingProviderTranslation(result.Document, "zh-Hans"));
        Assert.AreEqual(1,b.Calls);
    }

    [TestMethod]
    public async Task ExpandedSearchWaitsForTranslatedCandidateInsteadOfOriginalQualityWindow()
    {
        var providers = All(kind => kind == LyricsProviderKind.Amll ? DelayedTranslation() : Task.FromResult(Doc(kind)));
        var result=await new LyricsService(new(providers)).QueryDetailedAsync(Query,new(){Enabled=true,SearchRemainingProviders=true},default);
        Assert.AreEqual(LyricsProviderKind.Amll,result.Document.Provider);
        Assert.IsFalse(result.TranslationLookupIncomplete);
    }
    private static async Task<LyricsDocument> DelayedTranslation() { await Task.Delay(450); return Doc(LyricsProviderKind.Amll,true); }

    [TestMethod]
    public async Task NoTranslationRetainsOriginalAndDoesNotContactUnselectedSources()
    {
        var providers=All(kind=>Task.FromResult(Doc(kind)));
        var result=await new LyricsService(new(providers)).QueryDetailedAsync(Query,new(){Enabled=true},default);
        Assert.AreEqual(LyricsProviderKind.NetEase,result.Document.Provider);
        Assert.IsFalse(result.TranslationLookupIncomplete);
        Assert.AreEqual(1,providers.Sum(p=>p.Calls));
    }

    [TestMethod]
    public async Task FailedTranslationSearchRetainsOriginalButIsNotProofOfNoTranslation()
    {
        var a=new Provider(LyricsProviderKind.NetEase,()=>Task.FromResult(Doc(LyricsProviderKind.NetEase)));
        var b=new Provider(LyricsProviderKind.QqMusic,()=>throw new HttpRequestException("Unavailable"));
        var result=await new LyricsService(new([a,b])).QueryDetailedAsync(Query,new(){Enabled=true,BackupProvider=LyricsProviderKind.QqMusic},default);
        Assert.AreEqual(LyricsQueryStatus.Found,result.Status);
        Assert.IsTrue(result.TranslationLookupIncomplete);
        Assert.IsNotEmpty(result.Document.Lines);
    }

    [TestMethod]
    public async Task ExistingMatchingTranslationSkipsFurtherRequests()
    {
        var providers=All(kind=>Task.FromResult(Doc(kind,true)));
        var result=await new LyricsService(new(providers)).QueryDetailedAsync(Query,new(){Enabled=true,SearchRemainingProviders=true},default);
        Assert.AreEqual(LyricsProviderKind.NetEase,result.Document.Provider);
        Assert.AreEqual(1,providers.Sum(p=>p.Calls));
    }

    [TestMethod]
    public async Task TranslationCannotSelectConflictingSong()
    {
        var providers=All(kind=>Task.FromResult(kind==LyricsProviderKind.NetEase?Doc(kind):Doc(kind,true).Bind(Query,"Different song","Other artist","",180,12,"wrong")));
        var result=await new LyricsService(new(providers)).QueryDetailedAsync(Query,new(){Enabled=true,SearchRemainingProviders=true},default);
        Assert.AreEqual(LyricsProviderKind.NetEase,result.Document.Provider);
    }

    [TestMethod]
    public async Task TranslationTimeoutReturnsOriginalWithoutWaitingForUncooperativeProvider()
    {
        var late = new TaskCompletionSource<LyricsDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = new Provider(LyricsProviderKind.NetEase, () => Task.FromResult(Doc(LyricsProviderKind.NetEase)));
        var b = new Provider(LyricsProviderKind.QqMusic, () => late.Task);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await new LyricsService(new([a,b])).QueryDetailedAsync(Query,
            new() { Enabled=true, BackupProvider=LyricsProviderKind.QqMusic }, default).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsTrue(watch.Elapsed.TotalSeconds < 2.2, watch.Elapsed.ToString());
        Assert.AreEqual(LyricsProviderKind.NetEase, result.Document.Provider);
        Assert.IsTrue(result.TranslationLookupIncomplete);
        late.SetResult(Doc(LyricsProviderKind.QqMusic, true));
    }

    [TestMethod]
    public async Task SharedBudgetDoesNotRestartAfterBackupFinishes()
    {
        var late = new TaskCompletionSource<LyricsDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        var providers = All(kind => kind switch
        {
            LyricsProviderKind.NetEase => Task.FromResult(Doc(kind)),
            LyricsProviderKind.QqMusic => SlowOriginal(kind),
            _ => late.Task,
        });
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await new LyricsService(new(providers)).QueryDetailedAsync(Query,
            new() { Enabled=true, BackupProvider=LyricsProviderKind.QqMusic, SearchRemainingProviders=true }, default)
            .WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsTrue(watch.Elapsed.TotalSeconds < 2.2, watch.Elapsed.ToString());
        Assert.IsNotEmpty(result.Document.Lines);
        late.SetResult(LyricsDocument.Empty);
    }
    private static async Task<LyricsDocument> SlowOriginal(LyricsProviderKind kind)
    { await Task.Delay(1000); return Doc(kind); }

    [TestMethod]
    public async Task OriginalDiscoveredInsideProviderSurvivesItsTranslationTimeout()
    {
        var late = new TaskCompletionSource<LyricsDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new Progressive(late.Task);
        var result = await new LyricsService(new([provider])).QueryDetailedAsync(Query,
            new() { Enabled=true }, default).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.IsNotEmpty(result.Document.Lines);
        Assert.AreEqual(LyricsQueryStatus.Found, result.Status);
        Assert.IsTrue(result.TranslationLookupIncomplete);
        late.SetResult(LyricsDocument.Empty);
    }
    [TestMethod]
    public async Task OriginalDiscoveredInsideProviderSurvivesImmediateLaterFailure()
    {
        var provider = new Progressive(Task.FromException<LyricsDocument>(new HttpRequestException("Second candidate failed")));
        var result = await new LyricsService(new([provider])).QueryDetailedAsync(Query, new() { Enabled=true }, default);
        Assert.AreEqual(LyricsQueryStatus.Found, result.Status);
        Assert.IsNotEmpty(result.Document.Lines);
        Assert.IsTrue(result.TranslationLookupIncomplete);
    }

    private sealed class Progressive(Task<LyricsDocument> pending) : IProgressiveLyricsProvider
    {
        public LyricsProviderKind Kind => LyricsProviderKind.NetEase;
        public Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken token) => pending;
        public Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken token, Action<LyricsDocument> report)
        { report(Doc(Kind)); return pending; }
    }

    private static Provider[] All(Func<LyricsProviderKind,Task<LyricsDocument>> f)=>new[]{LyricsProviderKind.NetEase,LyricsProviderKind.QqMusic,LyricsProviderKind.Kugou,LyricsProviderKind.Lrclib,LyricsProviderKind.Amll}.Select(kind=>new Provider(kind,()=>f(kind))).ToArray();
    private sealed class Provider(LyricsProviderKind kind,Func<Task<LyricsDocument>> get):ILyricsProvider
    {
        public LyricsProviderKind Kind=>kind;
        public int Calls;
        public Task<LyricsDocument> QueryAsync(LyricsQuery query,CancellationToken token){Calls++;return get();}
    }
}
