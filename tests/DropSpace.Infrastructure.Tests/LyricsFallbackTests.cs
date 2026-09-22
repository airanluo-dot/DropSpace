using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class LyricsFallbackTests
{
    private static readonly LyricsQuery Query = new("Track", "Artist", "", TimeSpan.Zero);
    private static LyricsDocument Document(LyricsProviderKind kind) =>
        new LyricsDocument([new(TimeSpan.Zero, TimeSpan.FromSeconds(10), "Test line", null, [])], kind)
            .Bind(Query, Query.Title, Query.Artist, Query.Album, 0, LyricsMatcher.Score(Query, Query.Title, Query.Artist, Query.Album, 0), kind.ToString());

    [TestMethod]
    public async Task PrimaryHitSkipsOtherProvidersAndCachesTheResult()
    {
        var providers = Providers(kind => Task.FromResult(Document(kind)));
        var service = new LyricsService(new(providers));
        Assert.AreEqual(LyricsProviderKind.NetEase, (await service.QueryAsync(Query, new(), default)).Provider);
        await service.QueryAsync(Query, new(), default);
        Assert.AreEqual(1, providers.Sum(provider => provider.Calls));
    }

    [TestMethod]
    public async Task RemainingProvidersCanRunWithoutABackupAndNeverUseLocalFiles()
    {
        var providers = Providers(kind => Task.FromResult(kind == LyricsProviderKind.Amll ? Document(kind) : LyricsDocument.Empty));
        var service = new LyricsService(new(providers));
        Assert.AreEqual(LyricsProviderKind.Amll, (await service.QueryAsync(Query, new() { SearchRemainingProviders = true }, default)).Provider);
        foreach (var provider in providers) Assert.AreEqual(provider.Kind == LyricsProviderKind.LocalLrc ? 0 : 1, provider.Calls);
    }

    [TestMethod]
    public async Task NoBackupAndRemainingDisabledQueriesOnlyThePreferredProvider()
    {
        var providers = Providers(kind => Task.FromResult(kind == LyricsProviderKind.NetEase ? LyricsDocument.Empty : Document(kind)));
        var result = await new LyricsService(new(providers)).QueryAsync(Query, new(), default);

        Assert.IsEmpty(result.Lines);
        Assert.AreEqual(1, providers.Single(provider => provider.Kind == LyricsProviderKind.NetEase).Calls);
        Assert.IsTrue(providers.Where(provider => provider.Kind != LyricsProviderKind.NetEase).All(provider => provider.Calls == 0));
    }

    [TestMethod]
    public async Task ConfiguredBackupIsTheOnlyFallbackWhenRemainingSearchIsDisabled()
    {
        var order = new List<LyricsProviderKind>();
        var providers = Providers(kind =>
        {
            order.Add(kind);
            return Task.FromResult(kind == LyricsProviderKind.QqMusic ? Document(kind) : LyricsDocument.Empty);
        });
        var result = await new LyricsService(new(providers)).QueryAsync(Query,
            new() { BackupProvider = LyricsProviderKind.QqMusic }, default);

        Assert.AreEqual(LyricsProviderKind.QqMusic, result.Provider);
        CollectionAssert.AreEqual(new[] { LyricsProviderKind.NetEase, LyricsProviderKind.QqMusic }, order);
    }

    [TestMethod]
    public async Task RemainingProvidersStartOnlyAfterPreferredAndBackupMiss()
    {
        var order = new List<LyricsProviderKind>();
        var providers = Providers(kind =>
        {
            order.Add(kind);
            return Task.FromResult(kind == LyricsProviderKind.Kugou ? Document(kind) : LyricsDocument.Empty);
        });
        var result = await new LyricsService(new(providers)).QueryAsync(Query,
            new() { BackupProvider = LyricsProviderKind.QqMusic, SearchRemainingProviders = true }, default);

        Assert.AreEqual(LyricsProviderKind.Kugou, result.Provider);
        CollectionAssert.AreEqual(
            new[] { LyricsProviderKind.NetEase, LyricsProviderKind.QqMusic },
            order.Take(2).ToArray());
        Assert.AreEqual(1, providers.Single(provider => provider.Kind == LyricsProviderKind.Kugou).Calls);
        Assert.AreEqual(0, providers.Single(provider => provider.Kind == LyricsProviderKind.LocalLrc).Calls);
    }

    [TestMethod]
    public async Task CachedBackupCannotLeakIntoPreferredOnlyStrategy()
    {
        var providers = Providers(kind => Task.FromResult(kind == LyricsProviderKind.QqMusic ? Document(kind) : LyricsDocument.Empty));
        var service = new LyricsService(new(providers));

        Assert.AreEqual(LyricsProviderKind.QqMusic, (await service.QueryAsync(Query,
            new() { BackupProvider = LyricsProviderKind.QqMusic }, default)).Provider);
        Assert.IsEmpty((await service.QueryAsync(Query, new(), default)).Lines);
        Assert.AreEqual(2, providers.Single(provider => provider.Kind == LyricsProviderKind.NetEase).Calls);
    }

    [TestMethod]
    public async Task MissingLyricsAreNotCachedAndLocalModeNeverFallsBackOnline()
    {
        var providers = Providers(_ => Task.FromResult(LyricsDocument.Empty));
        var service = new LyricsService(new(providers));
        await service.QueryAsync(Query, new(), default); await service.QueryAsync(Query, new(), default);
        Assert.AreEqual(2, providers[0].Calls);
        var counts = providers.Select(provider => provider.Calls).ToArray();
        await service.QueryAsync(Query, new() { Mode = LyricsMode.LocalLrc }, default);
        foreach (var provider in providers)
            Assert.AreEqual(counts[(int)provider.Kind] + (provider.Kind == LyricsProviderKind.LocalLrc ? 1 : 0), provider.Calls);
    }

    [TestMethod]
    public async Task FailedPrimaryStillFallsBack()
    {
        var providers = Providers(kind => kind == LyricsProviderKind.NetEase ? Task.FromException<LyricsDocument>(new HttpRequestException()) : Task.FromResult(Document(kind)));
        var result = await new LyricsService(new(providers)).QueryAsync(Query, new() { SearchRemainingProviders = true }, default);
        Assert.IsNotEmpty(result.Lines);
        Assert.AreNotEqual(LyricsProviderKind.NetEase, result.Provider);
    }

    [TestMethod]
    public async Task FailedResponsesAreNotCachedAndNextAttemptCanRecover()
    {
        var failing = true;
        var providers = Providers(kind => failing ? Task.FromException<LyricsDocument>(new HttpRequestException()) : Task.FromResult(Document(kind)));
        var service = new LyricsService(new(providers));
        Assert.AreEqual(LyricsQueryStatus.Failed, (await service.QueryDetailedAsync(Query, new(), default)).Status);
        failing = false;
        Assert.AreEqual(LyricsQueryStatus.Found, (await service.QueryDetailedAsync(Query, new(), default)).Status);
        Assert.AreEqual(2, providers[0].Calls);
    }

    [TestMethod]
    public async Task StaleProviderIdentityCannotEnterFallbackOrCache()
    {
        var query = Query with { TrackIdentity = "current" };
        var providers = Providers(kind => Task.FromResult(kind == LyricsProviderKind.NetEase
            ? Document(kind) with { Match = Document(kind).Match! with { TrackIdentity = "previous" } }
            : kind == LyricsProviderKind.Kugou ? Document(kind) : LyricsDocument.Empty));
        var service = new LyricsService(new(providers));
        var result = await service.QueryDetailedAsync(query, new() { SearchRemainingProviders = true }, default);
        Assert.AreEqual(LyricsProviderKind.Kugou, result.Document.Provider);
        Assert.AreEqual("current", result.Document.Match!.TrackIdentity);
        Assert.AreEqual(LyricsProviderKind.Kugou, (await service.QueryDetailedAsync(query, new() { SearchRemainingProviders = true }, default)).Document.Provider);
    }

    [TestMethod]
    public async Task ClearingCacheWhileLookupIsPendingPreventsRepopulation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var provider = new CancellableProvider(LyricsProviderKind.NetEase, async token =>
        {
            if (Interlocked.Increment(ref calls) == 1) { entered.SetResult(); await release.Task.WaitAsync(token); }
            return Document(LyricsProviderKind.NetEase);
        });
        var service = new LyricsService(new([provider]));
        var pending = service.QueryAsync(Query, new(), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        service.ClearCache();
        release.SetResult();
        await pending;
        await service.QueryAsync(Query, new(), default);
        Assert.AreEqual(2, calls);
    }

    private static Provider[] Providers(Func<LyricsProviderKind, Task<LyricsDocument>> query) =>
        Enum.GetValues<LyricsProviderKind>().Select(kind => new Provider(kind, query)).ToArray();

    [TestMethod]
    public async Task WinningFallbackCancelsAndDrainsOtherRequests()
    {
        var active = 0;
        var providers = Enum.GetValues<LyricsProviderKind>().Select(kind => new CancellableProvider(kind, async token =>
        {
            if (kind == LyricsProviderKind.NetEase) return LyricsDocument.Empty;
            if (kind == LyricsProviderKind.Kugou) return Document(kind);
            Interlocked.Increment(ref active);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return LyricsDocument.Empty; }
            finally { Interlocked.Decrement(ref active); }
        }));
        var result = await new LyricsService(new(providers)).QueryAsync(Query, new() { SearchRemainingProviders = true }, default).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(LyricsProviderKind.Kugou, result.Provider);
        Assert.AreEqual(0, active);
    }

    [TestMethod]
    public async Task TrackCancellationPropagatesAndCannotPopulateCache()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var provider = new CancellableProvider(LyricsProviderKind.NetEase, async token =>
        {
            if (Interlocked.Increment(ref calls) == 1) { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            return Document(LyricsProviderKind.NetEase);
        });
        var service = new LyricsService(new([provider]));
        using var stop = new CancellationTokenSource();
        var pending = service.QueryAsync(Query, new(), stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); stop.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        await service.QueryAsync(Query, new(), default);
        Assert.AreEqual(2, calls);
    }

    private sealed class CancellableProvider(LyricsProviderKind kind, Func<CancellationToken, Task<LyricsDocument>> query) : ILyricsProvider
    {
        public LyricsProviderKind Kind => kind;
        public Task<LyricsDocument> QueryAsync(LyricsQuery request, CancellationToken token) => query(token);
    }
    private sealed class Provider(LyricsProviderKind kind, Func<LyricsProviderKind, Task<LyricsDocument>> query) : ILyricsProvider
    {
        public LyricsProviderKind Kind => kind;
        public int Calls { get; private set; }
        public Task<LyricsDocument> QueryAsync(LyricsQuery request, CancellationToken token) { token.ThrowIfCancellationRequested(); Calls++; return query(kind); }
    }
}
