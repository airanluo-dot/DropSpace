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
    public async Task EmptyPrimaryTriesAllFourOnlineAlternativesAndNeverLocalFiles()
    {
        var providers = Providers(kind => Task.FromResult(kind == LyricsProviderKind.Amll ? Document(kind) : LyricsDocument.Empty));
        var service = new LyricsService(new(providers));
        Assert.AreEqual(LyricsProviderKind.Amll, (await service.QueryAsync(Query, new(), default)).Provider);
        foreach (var provider in providers) Assert.AreEqual(provider.Kind == LyricsProviderKind.LocalLrc ? 0 : 1, provider.Calls);
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
        var result = await new LyricsService(new(providers)).QueryAsync(Query, new(), default);
        Assert.IsNotEmpty(result.Lines);
        Assert.AreNotEqual(LyricsProviderKind.NetEase, result.Provider);
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
        var result = await new LyricsService(new(providers)).QueryAsync(Query, new(), default).WaitAsync(TimeSpan.FromSeconds(2));
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
