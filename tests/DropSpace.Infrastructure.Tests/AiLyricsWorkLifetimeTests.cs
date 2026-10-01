using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class AiLyricsWorkLifetimeTests
{
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
