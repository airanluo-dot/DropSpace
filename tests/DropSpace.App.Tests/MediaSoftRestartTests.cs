using DropSpace.App.Services.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class MediaSoftRestartTests
{
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(3);

    [TestMethod]
    public async Task RepeatedClicks_ShareOneActualRestart()
    {
        var restart = new MediaSoftRestartOperation(TestDeadline);
        var started = NewSignal(); var release = NewSignal(); var calls = 0;
        async Task Work(CancellationToken token) { Interlocked.Increment(ref calls); started.SetResult(); await release.Task.WaitAsync(token); }
        var first = restart.RunAsync(Work, CancellationToken.None);
        await started.Task.WaitAsync(TestDeadline);
        var repeated = Enumerable.Range(0, 40).Select(_ => restart.RunAsync(Work, CancellationToken.None)).ToArray();
        Assert.IsTrue(repeated.All(task => ReferenceEquals(task, first)));
        release.SetResult();
        await Task.WhenAll(repeated).WaitAsync(TestDeadline);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task FailedAttempt_AllowsSuccessfulRetry()
    {
        var restart = new MediaSoftRestartOperation(TestDeadline);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => restart.RunAsync(
            _ => Task.FromException(new InvalidOperationException("Reconnect unavailable")), CancellationToken.None));
        var calls = 0;
        await restart.RunAsync(_ => { calls++; return Task.CompletedTask; }, CancellationToken.None);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task RetiredPage_CancelsOnlyItsWaiter_AndNewPageJoinsSameRestart()
    {
        var restart = new MediaSoftRestartOperation(TestDeadline);
        using var page = new CancellationTokenSource();
        var started = NewSignal(); var release = NewSignal(); var calls = 0;
        async Task Work(CancellationToken token) { Interlocked.Increment(ref calls); started.SetResult(); await release.Task.WaitAsync(token); }
        var firstPage = restart.RunAsync(Work, CancellationToken.None, page.Token);
        await started.Task.WaitAsync(TestDeadline);
        page.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => firstPage);
        var nextPage = restart.RunAsync(Work, CancellationToken.None);
        Assert.IsFalse(nextPage.IsCompleted);
        release.SetResult();
        await nextPage.WaitAsync(TestDeadline);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task AlreadyRetiredPage_DoesNotStartRestart()
    {
        var restart = new MediaSoftRestartOperation(TestDeadline);
        using var page = new CancellationTokenSource(); page.Cancel();
        var calls = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => restart.RunAsync(
            _ => { calls++; return Task.CompletedTask; }, CancellationToken.None, page.Token));
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task Timeout_ReturnsPromptly_RetainsHungOwner_ThenPermitsRetryAfterActualCompletion()
    {
        var restart = new MediaSoftRestartOperation(TimeSpan.FromMilliseconds(60));
        var hung = NewSignal(); var calls = 0;
        Task Work(CancellationToken _) { Interlocked.Increment(ref calls); return hung.Task; }
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => restart.RunAsync(Work, CancellationToken.None).WaitAsync(TestDeadline));
        for (var index = 0; index < 20; index++)
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => restart.RunAsync(Work, CancellationToken.None));
        Assert.AreEqual(1, calls);
        hung.SetResult();
        // Task.Run unwrap completion may trail the source completion by a scheduler turn.
        await EventuallyAsync(async () =>
        {
            try { await restart.RunAsync(_ => Task.CompletedTask, CancellationToken.None); return true; }
            catch (InvalidOperationException) { return false; }
        });
    }

    [TestMethod]
    public async Task AppLifetimeCancellation_DoesNotReportTimeout()
    {
        var restart = new MediaSoftRestartOperation(TestDeadline);
        using var lifetime = new CancellationTokenSource();
        var started = NewSignal();
        var pending = restart.RunAsync(async token => { started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }, lifetime.Token);
        await started.Task.WaitAsync(TestDeadline); lifetime.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
    }

    [TestMethod]
    public async Task RetiredLyrics_DoNotBlockFreshWork_ButRemainOwnedForCacheDrain()
    {
        var jobs = new RetirableMediaWork(); var oldStarted = NewSignal(); var oldRelease = NewSignal();
        jobs.Start(_ => { oldStarted.SetResult(); return oldRelease.Task; }, CancellationToken.None);
        await oldStarted.Task.WaitAsync(TestDeadline); jobs.RetireCurrent();
        var freshFinished = NewSignal();
        jobs.Start(_ => { freshFinished.SetResult(); return Task.CompletedTask; }, CancellationToken.None);
        await freshFinished.Task.WaitAsync(TestDeadline);
        await jobs.WaitForCurrentAsync(CancellationToken.None).WaitAsync(TestDeadline);
        var drain = jobs.DrainAsync(CancellationToken.None);
        Assert.IsFalse(drain.IsCompleted, "Cache deletion must not run before retired cache writers drain.");
        oldRelease.SetResult(); await drain.WaitAsync(TestDeadline);
    }

    [TestMethod]
    public async Task RepeatedRecovery_CannotAccumulateUnboundedLyricsOrArtworkWorkers()
    {
        var jobs = new RetirableMediaWork(); var release = NewSignal(); var firstStarted = NewSignal(); var started = NewSignal(); var count = 0;
        Task Work(CancellationToken _) { if (Interlocked.Increment(ref count) == 2) started.SetResult(); else firstStarted.SetResult(); return release.Task; }
        jobs.Start(Work, CancellationToken.None); await firstStarted.Task.WaitAsync(TestDeadline); jobs.RetireCurrent();
        jobs.Start(Work, CancellationToken.None); await started.Task.WaitAsync(TestDeadline); jobs.RetireCurrent();
        for (var index = 0; index < 20; index++)
            Assert.ThrowsExactly<InvalidOperationException>(() => jobs.Start(Work, CancellationToken.None));
        Assert.AreEqual(2, count);
        release.SetResult(); await jobs.DrainAsync(CancellationToken.None).WaitAsync(TestDeadline);
        jobs.Start(_ => Task.CompletedTask, CancellationToken.None);
        await jobs.WaitForCurrentAsync(CancellationToken.None).WaitAsync(TestDeadline);
        await jobs.DrainAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task CancellingRuntime_BoundsWaitForNonCooperativeOldWork()
    {
        var jobs = new RetirableMediaWork(); var release = NewSignal();
        jobs.Start(_ => release.Task, CancellationToken.None);
        using var runtime = new CancellationTokenSource();
        var wait = jobs.WaitForCurrentAsync(runtime.Token); runtime.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => wait);
        jobs.RetireCurrent(); release.SetResult();
        await jobs.DrainAsync(CancellationToken.None).WaitAsync(TestDeadline);
    }

    [TestMethod]
    public async Task BlockingCancellationCallback_DoesNotBlockRetirementOrFreshPresentation()
    {
        var jobs = new RetirableMediaWork(); var started = NewSignal(); var callbackEntered = NewSignal();
        var owner = NewSignal(); using var releaseCallback = new ManualResetEventSlim();
        jobs.Start(token =>
        {
            token.Register(() => { callbackEntered.TrySetResult(); releaseCallback.Wait(); });
            started.SetResult(); return owner.Task;
        }, CancellationToken.None);
        try
        {
            await started.Task.WaitAsync(TestDeadline);
            await Task.Run(jobs.RetireCurrent).WaitAsync(TestDeadline);
            await callbackEntered.Task.WaitAsync(TestDeadline);
            owner.SetResult();
            Assert.IsTrue(jobs.TryStart(_ => Task.CompletedTask, CancellationToken.None), "A hung old cancel callback must leave room for one recovery.");
            var drain = jobs.DrainAsync(CancellationToken.None);
            Assert.IsFalse(drain.IsCompleted, "Actual cancellation callbacks retain source ownership.");
            releaseCallback.Set(); await drain.WaitAsync(TestDeadline);
        }
        finally { owner.TrySetResult(); releaseCallback.Set(); }
    }

    [TestMethod]
    public async Task ParentCancellation_DoesNotSynchronouslyInvokeNativeCallbacks()
    {
        using var parent = new CancellationTokenSource(); using var releaseCallback = new ManualResetEventSlim();
        var cancellation = new MediaWorkCancellation(parent.Token);
        var entered = NewSignal(); var owner = NewSignal();
        cancellation.Token.Register(() => { entered.TrySetResult(); releaseCallback.Wait(); });
        var retirement = cancellation.CompleteWhenAsync(owner.Task);
        try
        {
            await Task.Run(parent.Cancel).WaitAsync(TestDeadline);
            await entered.Task.WaitAsync(TestDeadline);
            owner.SetResult(); Assert.IsFalse(retirement.IsCompleted);
            releaseCallback.Set(); await retirement.WaitAsync(TestDeadline);
        }
        finally { owner.TrySetResult(); releaseCallback.Set(); }
    }

    [TestMethod]
    public async Task CancelCallbacks_RetainBothSlotsEvenAfterWorkReturns()
    {
        var jobs = new RetirableMediaWork(); using var releaseCallbacks = new ManualResetEventSlim();
        var entered = NewSignal(); var firstStarted = NewSignal(); var started = NewSignal(); var owners = NewSignal(); var count = 0; var callbacks = 0;
        Task Work(CancellationToken token)
        {
            token.Register(() => { if (Interlocked.Increment(ref callbacks) == 2) entered.TrySetResult(); releaseCallbacks.Wait(); });
            if (Interlocked.Increment(ref count) == 1) firstStarted.TrySetResult(); else started.TrySetResult();
            return owners.Task;
        }
        try
        {
            jobs.Start(Work, CancellationToken.None); await firstStarted.Task.WaitAsync(TestDeadline); jobs.RetireCurrent();
            jobs.Start(Work, CancellationToken.None); await started.Task.WaitAsync(TestDeadline); jobs.RetireCurrent();
            await entered.Task.WaitAsync(TestDeadline); owners.SetResult();
            Assert.IsFalse(jobs.TryStart(_ => Task.CompletedTask, CancellationToken.None));
            releaseCallbacks.Set(); await jobs.DrainAsync(CancellationToken.None).WaitAsync(TestDeadline);
            Assert.IsTrue(jobs.TryStart(_ => Task.CompletedTask, CancellationToken.None));
            await jobs.DrainAsync(CancellationToken.None).WaitAsync(TestDeadline);
        }
        finally { owners.TrySetResult(); releaseCallbacks.Set(); }
    }

    [TestMethod]
    public async Task BusyCacheMaintenance_SkipsOptionalLyricsWithoutWaiting()
    {
        using var maintenance = new SemaphoreSlim(0, 1);
        var jobs = new RetirableMediaWork(); var calls = 0;
        var accepted = await Task.Run(() => jobs.TryStartWhileIdle(maintenance,
            _ => { calls++; return Task.CompletedTask; }, CancellationToken.None)).WaitAsync(TestDeadline);
        Assert.IsFalse(accepted); Assert.AreEqual(0, calls);
        maintenance.Release();
        Assert.IsTrue(jobs.TryStartWhileIdle(maintenance, _ => { calls++; return Task.CompletedTask; }, CancellationToken.None));
        await jobs.WaitForCurrentAsync(CancellationToken.None).WaitAsync(TestDeadline);
        await jobs.DrainAsync(CancellationToken.None).WaitAsync(TestDeadline);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task RestartTimeoutAndShutdown_RetainBlockingCallbacksWithoutHangingCaller()
    {
        var restart = new MediaSoftRestartOperation(TimeSpan.FromMilliseconds(80));
        using var release = new ManualResetEventSlim(); var callbackEntered = NewSignal(); var owner = NewSignal();
        try
        {
            var attempt = restart.RunAsync(token =>
            {
                token.Register(() => { callbackEntered.TrySetResult(); release.Wait(); });
                return owner.Task;
            }, CancellationToken.None);
            await Assert.ThrowsExactlyAsync<TimeoutException>(() => attempt.WaitAsync(TestDeadline));
            await callbackEntered.Task.WaitAsync(TestDeadline);
            var shutdown = restart.StopAsync();
            Assert.IsFalse(shutdown.IsCompleted);
            Assert.ThrowsExactly<ObjectDisposedException>(() => restart.RunAsync(_ => Task.CompletedTask, CancellationToken.None));
            owner.SetResult(); Assert.IsFalse(shutdown.IsCompleted);
            release.Set(); await shutdown.WaitAsync(TestDeadline);
        }
        finally { owner.TrySetResult(); release.Set(); }
    }

    [TestMethod]
    public async Task AlreadyCancelledLifetime_DoesNotInvokeRestartOrPresentationDelegate()
    {
        using var lifetime = new CancellationTokenSource(); lifetime.Cancel();
        var calls = 0; var restart = new MediaSoftRestartOperation(TestDeadline);
        await Assert.ThrowsAsync<OperationCanceledException>(() => restart.RunAsync(
            _ => { calls++; return Task.CompletedTask; }, lifetime.Token));
        var jobs = new RetirableMediaWork();
        Assert.Throws<OperationCanceledException>(() => jobs.TryStart(
            _ => { calls++; return Task.CompletedTask; }, lifetime.Token));
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task SourceRefresh_SurvivesCapacityAndMaintenanceRejection_UntilAdmitted()
    {
        var refresh = new MediaLyricsRefreshRequest();
        var jobs = new RetirableMediaWork();
        var release = NewSignal();
        using var maintenance = new SemaphoreSlim(1, 1);
        Assert.IsFalse(refresh.IsPending(refresh.Capture()));
        var firstStarted = NewSignal(); var secondStarted = NewSignal();
        jobs.Start(_ => { firstStarted.SetResult(); return release.Task; }, CancellationToken.None);
        await firstStarted.Task.WaitAsync(TestDeadline); jobs.RetireCurrent();
        jobs.Start(_ => { secondStarted.SetResult(); return release.Task; }, CancellationToken.None);
        await secondStarted.Task.WaitAsync(TestDeadline); jobs.RetireCurrent();
        refresh.Request();
        var observedRefresh = false;
        Task Load(CancellationToken _) { observedRefresh = true; return Task.CompletedTask; }
        for (var index = 0; index < 20; index++)
        {
            var ticket = refresh.Capture();
            Assert.IsTrue(refresh.IsPending(ticket));
            Assert.IsFalse(jobs.TryStartWhileIdle(maintenance, Load, CancellationToken.None));
            // No start means no acknowledgement; later capacity notification retains refresh.
        }
        release.SetResult(); await jobs.DrainAsync(CancellationToken.None).WaitAsync(TestDeadline);
        await maintenance.WaitAsync();
        Assert.IsFalse(jobs.TryStartWhileIdle(maintenance, Load, CancellationToken.None));
        Assert.IsTrue(refresh.IsPending(refresh.Capture()));
        maintenance.Release();
        var accepted = refresh.Capture();
        Assert.IsTrue(jobs.TryStartWhileIdle(maintenance, Load, CancellationToken.None));
        refresh.MarkStarted(accepted);
        await jobs.WaitForCurrentAsync(CancellationToken.None).WaitAsync(TestDeadline);
        await jobs.DrainAsync(CancellationToken.None).WaitAsync(TestDeadline);
        Assert.IsTrue(observedRefresh);
        Assert.IsFalse(refresh.IsPending(refresh.Capture()));
    }

    [TestMethod]
    public void SourceRefresh_NewerClickCannotBeAcknowledgedByAnOlderAdmission()
    {
        var refresh = new MediaLyricsRefreshRequest();
        refresh.Request(); var older = refresh.Capture();
        refresh.Request(); var newer = refresh.Capture();
        refresh.MarkStarted(older);
        Assert.IsTrue(refresh.IsPending(newer));
        refresh.MarkStarted(newer);
        refresh.MarkStarted(older);
        Assert.IsFalse(refresh.IsPending(newer));
    }

    [TestMethod]
    public async Task RetiredAiWait_FreesSourceFetchCapacity_WhileAiCleanupRemainsOwned()
    {
        var jobs = new RetirableMediaWork();
        var aiRelease = NewSignal(); var started = NewSignal();
        Task aiRetirement = Task.CompletedTask;
        CancellationToken aiToken = default;
        jobs.Start(async token =>
        {
            var cancellation = new MediaWorkCancellation(token);
            aiToken = cancellation.Token;
            aiRetirement = cancellation.CompleteWhenAsync(aiRelease.Task);
            started.SetResult();
            try { await aiRelease.Task.WaitAsync(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }, CancellationToken.None);
        try
        {
            await started.Task.WaitAsync(TestDeadline);
            jobs.RetireCurrent();
            await jobs.DrainAsync(CancellationToken.None).WaitAsync(TestDeadline);
            Assert.IsTrue(aiToken.IsCancellationRequested);
            Assert.IsFalse(aiRetirement.IsCompleted, "The native owner still holds AI cleanup after source presentation retires.");
            var calls = 0;
            for (var index = 0; index < 30; index++)
            {
                Assert.IsTrue(jobs.TryStart(_ => { Interlocked.Increment(ref calls); return Task.CompletedTask; }, CancellationToken.None));
                await jobs.WaitForCurrentAsync(CancellationToken.None).WaitAsync(TestDeadline);
                await jobs.DrainAsync(CancellationToken.None).WaitAsync(TestDeadline);
            }
            Assert.AreEqual(30, calls);
            aiRelease.SetResult(); await aiRetirement.WaitAsync(TestDeadline);
        }
        finally { aiRelease.TrySetResult(); }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        using var deadline = new CancellationTokenSource(TestDeadline);
        while (!await condition()) await Task.Delay(10, deadline.Token);
    }
}
