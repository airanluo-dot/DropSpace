using DropSpace.Core.Policies;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class ClipboardCaptureCoordinatorShutdownTests
{
    [TestMethod]
    public async Task DisposalDoesNotTurnAnAlreadyCommittedCaptureIntoAFailure()
    {
        var coordinator = new ConsecutiveClipboardCaptureCoordinator();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = coordinator.ExecuteAsync("A", async _ =>
        {
            started.SetResult();
            await release.Task;
            return 7;
        }, value => value == 7);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        coordinator.Dispose();
        release.SetResult();

        var result = await capture.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(result.Suppressed);
        Assert.AreEqual(7, result.Value);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() =>
            coordinator.ExecuteAsync("B", _ => Task.FromResult(1), _ => true));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WaitingCaptureOrResetObservesDisposalWithoutRunning(bool reset)
    {
        var coordinator = new ConsecutiveClipboardCaptureCoordinator();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = coordinator.ExecuteAsync("A", async _ =>
        {
            started.SetResult();
            await release.Task;
            return true;
        }, value => value);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var invoked = false;
        Task waiting = reset ? coordinator.ResetAsync() : coordinator.ExecuteAsync("B", _ =>
        {
            invoked = true;
            return Task.FromResult(true);
        }, value => value);

        coordinator.Dispose();
        release.SetResult();

        // Observe both tasks even on the old implementation, whose active Release throws.
        try { await active.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (ObjectDisposedException) { }
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() =>
            waiting.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.IsFalse(invoked);
    }
}
