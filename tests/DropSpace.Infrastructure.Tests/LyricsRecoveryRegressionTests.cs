using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class LyricsRecoveryRegressionTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);
    private static readonly LyricsQuery Query = new("Track", "Artist", "", TimeSpan.Zero);
    private static readonly LyricsSettings Settings = new() { Enabled = true, SearchRemainingProviders = false };

    [TestMethod]
    public async Task RapidTrackChanges_CancelOldRequests_KeepProviderAndLatestCache()
    {
        var calls = 0;
        var entered = NewSignal();
        var provider = new Provider(async (query, token) =>
        {
            Interlocked.Increment(ref calls);
            if (query.Title != "Latest")
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return Document(query, query.Title);
        });
        var service = new LyricsService(new([provider]));
        for (var index = 0; index < 30; index++)
        {
            entered = NewSignal();
            using var stop = new CancellationTokenSource();
            var old = service.QueryDetailedAsync(Query with { Title = "Old " + index }, Settings, stop.Token);
            await entered.Task.WaitAsync(Budget);
            stop.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => old.WaitAsync(Budget));
        }
        var latest = Query with { Title = "Latest" };
        Assert.AreEqual("Latest", (await service.QueryDetailedAsync(latest, Settings, default)).Document.Lines[0].Text);
        await service.QueryDetailedAsync(latest, Settings, default);
        Assert.AreEqual(31, calls);
    }

    [TestMethod]
    public async Task CancellationDuringSwitch_DoesNotStartBackup_OrCacheLateResult()
    {
        var entered = NewSignal(); var release = NewSignal(); var exited = NewSignal(); var calls = 0; var backupCalls = 0;
        var provider = new Provider(async (query, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.SetResult(); await release.Task; exited.SetResult();
                return Document(query, "obsolete");
            }
            return Document(query, "current");
        });
        var backup = new Provider((query, _) => { backupCalls++; return Task.FromResult(Document(query, "backup")); }, LyricsProviderKind.QqMusic);
        var service = new LyricsService(new([provider, backup]));
        var settings = Settings with { BackupProvider = LyricsProviderKind.QqMusic, SearchRemainingProviders = true };
        using var stop = new CancellationTokenSource();
        try
        {
            var old = service.QueryDetailedAsync(Query, settings, stop.Token);
            await entered.Task.WaitAsync(Budget); stop.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => old.WaitAsync(Budget));
            Assert.AreEqual("current", (await service.QueryDetailedAsync(Query, settings, default)).Document.Lines[0].Text);
            release.SetResult(); await exited.Task.WaitAsync(Budget);
            Assert.AreEqual("current", (await service.QueryDetailedAsync(Query, settings, default)).Document.Lines[0].Text);
            Assert.AreEqual(0, backupCalls);
            Assert.AreEqual(2, calls);
        }
        finally { release.TrySetResult(); }
    }

    [TestMethod]
    public async Task TimedOutTransport_RetainsBoundedSlots_UntilActualCompletion_ThenRecovers()
    {
        var release = NewSignal(); var allExited = NewSignal(); var calls = 0; var exited = 0;
        var provider = new Provider(async (query, token) =>
        {
            Interlocked.Increment(ref calls);
            if (!release.Task.IsCompleted)
            {
                try { await release.Task; }
                finally { if (Interlocked.Increment(ref exited) == 2) allExited.TrySetResult(); }
                // Accessing the cancelled token after its waiter left must remain valid.
                using var registration = token.Register(() => { });
                token.ThrowIfCancellationRequested();
            }
            return Document(query, "recovered");
        });
        var service = new LyricsService(new([provider]), TimeSpan.FromMilliseconds(70));
        try
        {
            for (var index = 0; index < 8; index++)
                Assert.AreEqual(LyricsQueryStatus.Failed, (await service.QueryDetailedAsync(Query, Settings, default, refresh: true).WaitAsync(Budget)).Status);
            Assert.AreEqual(2, calls, "Expired callers must not abandon slots and spawn unlimited requests.");
            release.SetResult(); await allExited.Task.WaitAsync(Budget);
            Assert.AreEqual(LyricsQueryStatus.Found, (await service.QueryDetailedAsync(Query, Settings, default, refresh: true).WaitAsync(Budget)).Status);
            Assert.AreEqual(3, calls);
        }
        finally { release.TrySetResult(); }
    }

    [TestMethod]
    public async Task RefreshBypassesSuccessfulCache_RetainsUserStrategy_AndReplacesCacheOnlyOnSuccess()
    {
        var text = "cached"; var fail = false; var calls = 0; var unwantedCalls = 0;
        var provider = new Provider((query, _) =>
        {
            calls++;
            return fail ? Task.FromException<LyricsDocument>(new HttpRequestException("offline")) : Task.FromResult(Document(query, text));
        }, LyricsProviderKind.QqMusic);
        var unwanted = new Provider((query, _) => { unwantedCalls++; return Task.FromResult(Document(query, "unselected")); });
        var service = new LyricsService(new([provider, unwanted]));
        var settings = Settings with { Provider = LyricsProviderKind.QqMusic };
        Assert.AreEqual("cached", (await service.QueryDetailedAsync(Query, settings, default)).Document.Lines[0].Text);
        fail = true;
        Assert.AreEqual(LyricsQueryStatus.Failed, (await service.QueryDetailedAsync(Query, settings, default, refresh: true)).Status);
        Assert.AreEqual("cached", (await service.QueryDetailedAsync(Query, settings, default)).Document.Lines[0].Text);
        fail = false; text = "recovered";
        Assert.AreEqual("recovered", (await service.QueryDetailedAsync(Query, settings, default, refresh: true)).Document.Lines[0].Text);
        Assert.AreEqual("recovered", (await service.QueryDetailedAsync(Query, settings, default)).Document.Lines[0].Text);
        Assert.AreEqual(3, calls); Assert.AreEqual(0, unwantedCalls);
    }

    [TestMethod]
    public async Task NetworkFailureThenRecovery_DoesNotMemoizeFailure_OrRequireServiceRecreation()
    {
        var calls = 0;
        var provider = new Provider((query, _) => ++calls <= 2
            ? Task.FromException<LyricsDocument>(new HttpRequestException("connection reset"))
            : Task.FromResult(Document(query, "online again")));
        var service = new LyricsService(new([provider]));
        Assert.AreEqual(LyricsQueryStatus.Failed, (await service.QueryDetailedAsync(Query, Settings, default)).Status);
        Assert.AreEqual(LyricsQueryStatus.Failed, (await service.QueryDetailedAsync(Query, Settings, default, refresh: true)).Status);
        Assert.AreEqual(LyricsQueryStatus.Found, (await service.QueryDetailedAsync(Query, Settings, default, refresh: true)).Status);
        Assert.AreEqual(LyricsQueryStatus.Found, (await service.QueryDetailedAsync(Query, Settings, default)).Status);
        Assert.AreEqual(3, calls);
    }

    [TestMethod]
    public async Task SharedHttpTransport_CancelledAndFailedSongReads_DoNotPoisonRecovery()
    {
        var entered = NewSignal(); var calls = 0; var fail = true;
        using var handler = new HttpFixture(async (request, token) =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("search", StringComparison.Ordinal))
                return new(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"result":{"songs":[{"id":1,"name":"Track","artists":[{"name":"Artist"}]}]}}""")
                };
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return new(fail ? System.Net.HttpStatusCode.ServiceUnavailable : System.Net.HttpStatusCode.OK)
            { Content = new StringContent("""{"lrc":{"lyric":"[00:01]recovered through HTTP"}}""") };
        });
        using var client = new HttpClient(handler);
        var service = new LyricsService(new([new NetEaseLyricsProvider(new(client))]));
        using var stop = new CancellationTokenSource();
        var cancelled = service.QueryDetailedAsync(Query, Settings, stop.Token);
        await entered.Task.WaitAsync(Budget); stop.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => cancelled.WaitAsync(Budget));
        Assert.AreEqual(LyricsQueryStatus.Failed, (await service.QueryDetailedAsync(Query, Settings, default, refresh: true)).Status);
        fail = false;
        var recovered = await service.QueryDetailedAsync(Query, Settings, default, refresh: true);
        Assert.AreEqual(LyricsQueryStatus.Found, recovered.Status);
        Assert.AreEqual("recovered through HTTP", recovered.Document.Lines.Single().Text);
        Assert.AreEqual(3, calls);
    }

    [TestMethod]
    public async Task WinningFallback_DoesNotBlockOnRetiringTransportCancellationCallback()
    {
        using var releaseCallback = new ManualResetEventSlim();
        var callbackEntered = NewSignal(); var owner = NewSignal();
        var providers = new ILyricsProvider[]
        {
            new Provider((_, _) => Task.FromResult(LyricsDocument.Empty)),
            new Provider((query, _) => Task.FromResult(Document(query, "winner")), LyricsProviderKind.QqMusic),
            new Provider((_, token) =>
            {
                token.Register(() => { callbackEntered.TrySetResult(); releaseCallback.Wait(); });
                return owner.Task.ContinueWith(_ => LyricsDocument.Empty, TaskScheduler.Default);
            }, LyricsProviderKind.Kugou),
            new Provider((_, _) => Task.FromResult(LyricsDocument.Empty), LyricsProviderKind.Lrclib),
            new Provider((_, _) => Task.FromResult(LyricsDocument.Empty), LyricsProviderKind.Amll),
        };
        try
        {
            var result = await new LyricsService(new(providers)).QueryDetailedAsync(Query,
                Settings with { SearchRemainingProviders = true }, default).WaitAsync(Budget);
            Assert.AreEqual("winner", result.Document.Lines[0].Text);
            await callbackEntered.Task.WaitAsync(Budget);
        }
        finally { releaseCallback.Set(); owner.TrySetResult(); }
    }

    [TestMethod]
    public async Task CancelCallbacks_RetainProviderBudgetAfterInvocationReturns_ThenReleaseForRecovery()
    {
        using var releaseCallbacks = new ManualResetEventSlim();
        var owners = NewSignal(); var allOwnersExited = NewSignal(); var allCallbacksEntered = NewSignal();
        var entered = NewSignal(); var calls = 0; var exited = 0; var callbacks = 0;
        var provider = new Provider(async (query, token) =>
        {
            Interlocked.Increment(ref calls);
            if (!owners.Task.IsCompleted)
            {
                token.Register(() =>
                {
                    if (Interlocked.Increment(ref callbacks) == 2) allCallbacksEntered.TrySetResult();
                    releaseCallbacks.Wait();
                });
                entered.TrySetResult();
                await owners.Task;
                if (Interlocked.Increment(ref exited) == 2) allOwnersExited.TrySetResult();
            }
            return Document(query, "recovered");
        });
        var service = new LyricsService(new([provider]), TimeSpan.FromMilliseconds(100));
        try
        {
            for (var index = 0; index < 2; index++)
            {
                entered = NewSignal();
                using var stop = new CancellationTokenSource();
                var pending = service.QueryDetailedAsync(Query, Settings, stop.Token);
                await entered.Task.WaitAsync(Budget);
                await Task.Run(stop.Cancel).WaitAsync(Budget);
                await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(Budget));
            }
            await allCallbacksEntered.Task.WaitAsync(Budget);
            owners.SetResult(); await allOwnersExited.Task.WaitAsync(Budget);
            Assert.AreEqual(LyricsQueryStatus.Failed, (await service.QueryDetailedAsync(Query, Settings, default, refresh: true)).Status);
            Assert.AreEqual(2, calls, "Completing the provider task must not discard its live cancellation callbacks.");
            releaseCallbacks.Set();
            Assert.AreEqual(LyricsQueryStatus.Found, (await service.QueryDetailedAsync(Query, Settings, default, refresh: true)).Status);
            Assert.AreEqual(3, calls);
        }
        finally { releaseCallbacks.Set(); owners.TrySetResult(); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LateHttpCancellationCallback_CannotBlockProviderDeadlineOrTrackCancellation(bool cancelTrack)
    {
        using var handler = new LateHttpCancellationFixture();
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var service = new LyricsService(new([new NetEaseLyricsProvider(new(client))]),
            cancelTrack ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(400));
        using var stop = new CancellationTokenSource();
        try
        {
            var pending = service.QueryDetailedAsync(Query, Settings, stop.Token);
            await handler.SearchEntered.Task.WaitAsync(Budget);
            // NetEase's second HTTP request starts after the service's cancellation
            // await is installed. Its linked token is newer in the provider's LIFO
            // chain; a blocked transport callback used to hide the outer deadline.
            handler.ContinueSearch.SetResult();
            await handler.LyricsEntered[0].Task.WaitAsync(Budget);
            if (cancelTrack) await stop.CancelAsync().WaitAsync(Budget);
            await handler.CallbacksEntered[0].Task.WaitAsync(Budget);
            Assert.IsFalse(handler.ReleaseCallbacks.IsSet);
            if (cancelTrack)
                await Assert.ThrowsAsync<OperationCanceledException>(() => pending.WaitAsync(Budget));
            else
                Assert.AreEqual(LyricsQueryStatus.Failed, (await pending.WaitAsync(Budget)).Status);
            Assert.IsFalse(handler.ReleaseResponses.Task.IsCompleted,
                "The caller must retire while the real HTTP operation remains owned.");
        }
        finally { handler.ReleaseAll(); }
    }

    [TestMethod]
    public async Task TwoLateHttpCancellationCallbacks_LeaveRefreshBounded_AndRecoverWithoutLateCacheWrites()
    {
        using var handler = new LateHttpCancellationFixture();
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var service = new LyricsService(new([new NetEaseLyricsProvider(new(client))]), TimeSpan.FromMilliseconds(400));
        try
        {
            for (var index = 0; index < 2; index++)
            {
                var pending = service.QueryDetailedAsync(Query, Settings, default, refresh: true);
                await handler.SearchEntered.Task.WaitAsync(Budget);
                handler.ContinueSearch.TrySetResult();
                await handler.LyricsEntered[index].Task.WaitAsync(Budget);
                await handler.CallbacksEntered[index].Task.WaitAsync(Budget);
                Assert.AreEqual(LyricsQueryStatus.Failed, (await pending.WaitAsync(Budget)).Status);
            }
            Assert.AreEqual(2, handler.LyricsCalls);
            for (var retry = 0; retry < 3; retry++)
                Assert.AreEqual(LyricsQueryStatus.Failed,
                    (await service.QueryDetailedAsync(Query, Settings, default, refresh: true).WaitAsync(Budget)).Status);
            Assert.AreEqual(2, handler.LyricsCalls,
                "Retiring callbacks retain the actual transport budget; refresh cannot spawn replacements without bound.");
            handler.ReleaseAll();
            var recovered = await service.QueryDetailedAsync(Query, Settings, default, refresh: true).WaitAsync(Budget);
            Assert.AreEqual(LyricsQueryStatus.Found, recovered.Status);
            Assert.AreEqual("fresh after cleanup", recovered.Document.Lines.Single().Text);
            Assert.AreEqual("fresh after cleanup",
                (await service.QueryDetailedAsync(Query, Settings, default).WaitAsync(Budget)).Document.Lines.Single().Text);
            Assert.AreEqual(3, handler.LyricsCalls);
        }
        finally { handler.ReleaseAll(); }
    }

    private sealed class LateHttpCancellationFixture : HttpMessageHandler
    {
        internal readonly TaskCompletionSource SearchEntered = NewSignal(), ContinueSearch = NewSignal(), ReleaseResponses = NewSignal();
        internal readonly TaskCompletionSource[] LyricsEntered = [NewSignal(), NewSignal()];
        internal readonly TaskCompletionSource[] CallbacksEntered = [NewSignal(), NewSignal()];
        internal readonly ManualResetEventSlim ReleaseCallbacks = new();
        private int _lyricsCalls;
        internal int LyricsCalls => Volatile.Read(ref _lyricsCalls);
        internal void ReleaseAll() { ReleaseCallbacks.Set(); ReleaseResponses.TrySetResult(); ContinueSearch.TrySetResult(); }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath.Contains("search", StringComparison.Ordinal))
            {
                SearchEntered.TrySetResult();
                await ContinueSearch.Task;
                return Response(request, """{"result":{"songs":[{"id":1,"name":"Track","artists":[{"name":"Artist"}]}]}}""");
            }
            var index = Interlocked.Increment(ref _lyricsCalls) - 1;
            if (index < 2)
            {
                using var registration = token.Register(() =>
                {
                    CallbacksEntered[index].TrySetResult();
                    ReleaseCallbacks.Wait();
                });
                LyricsEntered[index].TrySetResult();
                await ReleaseResponses.Task;
                return Response(request, """{"lrc":{"lyric":"[00:01]obsolete late response"}}""");
            }
            return Response(request, """{"lrc":{"lyric":"[00:01]fresh after cleanup"}}""");
        }

        private static HttpResponseMessage Response(HttpRequestMessage request, string json) =>
            new(System.Net.HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(json) };
    }

    private sealed class HttpFixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = await send(request, token);
            response.RequestMessage = request;
            return response;
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static LyricsDocument Document(LyricsQuery query, string text) =>
        new LyricsDocument([new(TimeSpan.Zero, TimeSpan.FromSeconds(10), text, null, [])], LyricsProviderKind.NetEase)
            .Bind(query, query.Title, query.Artist, query.Album, 0, 10, "matched-track");
    private sealed class Provider(Func<LyricsQuery, CancellationToken, Task<LyricsDocument>> query,
        LyricsProviderKind kind = LyricsProviderKind.NetEase) : ILyricsProvider
    {
        public LyricsProviderKind Kind => kind;
        public Task<LyricsDocument> QueryAsync(LyricsQuery request, CancellationToken token) => query(request, token);
    }
}
