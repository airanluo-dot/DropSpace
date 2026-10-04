using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class AiLyricsWorkLifetimeTests
{
    [TestMethod]
    public async Task MaintenanceDoesNotRunCancellationCallbacksOnItsCaller()
    {
        using var lifetime = new AiLyricsWorkLifetime();
        using var release = new ManualResetEventSlim();
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = lifetime.RunAsync(async token =>
        {
            using var registration = token.Register(() => { callbackEntered.TrySetResult(); release.Wait(); });
            ready.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return 1;
        }, 0, default);
        await ready.Task;
        var maintenance = Task.Run(async () =>
        {
            var pending = lifetime.MaintainAsync(_ => Task.CompletedTask, default);
            callReturned.TrySetResult();
            await pending;
        });
        try
        {
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await callReturned.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.IsFalse(maintenance.IsCompleted, "Maintenance must still retain cancellation cleanup ownership.");
        }
        finally
        {
            release.Set();
            await maintenance.WaitAsync(TimeSpan.FromSeconds(3));
            await Assert.ThrowsAsync<OperationCanceledException>(() => work);
        }
    }

    [TestMethod]
    public async Task MaintenanceDoesNotDeleteAfterPublicCancellationReturnsWithNativeCleanupPending()
    {
        using var runner = new LlamaCompletionRunner(TestInferenceMemory.Sufficient);
        using var lifetime = new AiLyricsWorkLifetime(token => runner.DrainCleanupAsync(token, TimeSpan.Zero));
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var model = Path.Combine(Path.GetTempPath(), "DropSpace-model-maintenance-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(model, "model fixture");
        try
        {
            var primary = new OperationCanceledException("Caller cancelled inference.");
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => lifetime.RunAsync<int>(async _ =>
            {
                runner.TrackCleanup(cleanup.Task);
                // Model the caller's expired 10-second grace without a wall-clock delay.
                await LlamaCompletionRunner.WaitForCleanupPreservingFailureAsync(cleanup.Task, 123, primary, TimeSpan.Zero);
                throw primary;
            }, 0, CancellationToken.None));
            var maintenanceStarted = false;
            Task Delete(CancellationToken _)
            {
                maintenanceStarted = true;
                File.Delete(model);
                return Task.CompletedTask;
            }
            var error = await Assert.ThrowsExactlyAsync<TimeoutException>(() => lifetime.MaintainAsync(Delete, CancellationToken.None));
            StringAssert.Contains(error.Message, "maintenance was not started");
            Assert.IsFalse(maintenanceStarted);
            Assert.IsTrue(File.Exists(model));
            Assert.IsFalse(cleanup.Task.IsCompleted);
            cleanup.SetResult();
            await lifetime.MaintainAsync(Delete, CancellationToken.None);
            Assert.IsTrue(maintenanceStarted);
            Assert.IsFalse(File.Exists(model));
        }
        finally
        {
            cleanup.TrySetResult();
            File.Delete(model);
        }
    }

    [TestMethod]
    public async Task MaintenanceRejectsNewWorkWhileAwaitingRetainedNativeCleanup()
    {
        using var runner = new LlamaCompletionRunner(TestInferenceMemory.Sufficient);
        using var lifetime = new AiLyricsWorkLifetime(runner.DrainCleanupAsync);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.TrackCleanup(cleanup.Task);
        var maintenanceStarted = false;
        var maintenance = lifetime.MaintainAsync(_ => { maintenanceStarted = true; return Task.CompletedTask; }, CancellationToken.None);
        try
        {
            Assert.IsFalse(maintenance.IsCompleted);
            Assert.IsFalse(maintenanceStarted);
            Assert.AreEqual(0, await lifetime.RunAsync(_ => Task.FromResult(1), 0, CancellationToken.None));
        }
        finally { cleanup.SetResult(); }
        await maintenance;
        Assert.IsTrue(maintenanceStarted);
    }

    [TestMethod]
    public async Task FailedNativeCleanupRemainsFailClosedForMaintenance()
    {
        using var runner = new LlamaCompletionRunner(TestInferenceMemory.Sufficient);
        using var lifetime = new AiLyricsWorkLifetime(runner.DrainCleanupAsync);
        runner.TrackCleanup(Task.FromException(new IOException("OS exit unconfirmed.")));
        runner.TrackCleanup(Task.CompletedTask);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var error = await Assert.ThrowsExactlyAsync<IOException>(() => lifetime.MaintainAsync(
                _ => throw new AssertFailedException("Must not delete with unresolved ownership."), CancellationToken.None));
            StringAssert.Contains(error.Message, "maintenance was not started");
        }
    }

    [TestMethod]
    public async Task CancellingMaintenanceDoesNotCancelRetainedNativeCleanup()
    {
        using var runner = new LlamaCompletionRunner(TestInferenceMemory.Sufficient);
        using var lifetime = new AiLyricsWorkLifetime(runner.DrainCleanupAsync);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.TrackCleanup(cleanup.Task);
        using var cancel = new CancellationTokenSource();
        var maintenance = lifetime.MaintainAsync(_ => throw new AssertFailedException("Cancelled maintenance must not run."), cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => maintenance);
        Assert.IsFalse(cleanup.Task.IsCompleted);
        cleanup.SetResult();
        await lifetime.MaintainAsync(_ => Task.CompletedTask, CancellationToken.None);
    }

    [TestMethod]
    public async Task MaintenanceStopsDrainsAndRejectsNewInferenceUntilDone()
    {
        using var lifetime = new AiLyricsWorkLifetime();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retired = false;
        var work = lifetime.RunAsync(async token =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, token); return 1; }
            finally { retired = true; }
        }, 0, CancellationToken.None);
        await started.Task;
        await lifetime.MaintainAsync(async token =>
        {
            Assert.IsTrue(retired);
            Assert.AreEqual(0, await lifetime.RunAsync(_ => throw new AssertFailedException("Must not restart during maintenance."), 0, token));
        }, CancellationToken.None);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await work);
        Assert.AreEqual(7, await lifetime.RunAsync(_ => Task.FromResult(7), 0, CancellationToken.None));
    }

    [TestMethod]
    public async Task FailedMaintenanceDoesNotPermanentlyBlockNewWork()
    {
        using var lifetime = new AiLyricsWorkLifetime();
        await Assert.ThrowsExactlyAsync<IOException>(() => lifetime.MaintainAsync(_ => throw new IOException("fixture"), CancellationToken.None));
        Assert.AreEqual(7, await lifetime.RunAsync(_ => Task.FromResult(7), 0, CancellationToken.None));
    }
}
