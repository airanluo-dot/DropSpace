using DropSpace.App.Views;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class ContentDialogLifetimeTests
{
    [TestMethod]
    public async Task CancellationDoesNotReleaseRootUntilNativeDialogActuallyCloses()
    {
        using var gate = new SemaphoreSlim(1, 1);
        using var cancel = new CancellationTokenSource();
        var native = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondShown = false;
        var first = ContentDialogLifetime.RunSerializedAsync(gate, () => native.Task, () => { }, cancel.Token);
        var second = ContentDialogLifetime.RunSerializedAsync(gate, () => { secondShown = true; return Task.FromResult(true); }, () => { }, CancellationToken.None);
        await cancel.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => first);
        Assert.IsFalse(secondShown);
        native.SetResult(false);
        Assert.IsTrue(await second.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(1, gate.CurrentCount);
    }

    [TestMethod]
    public async Task FailedPresentationReleasesRootForNextDialog()
    {
        using var gate = new SemaphoreSlim(1, 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ContentDialogLifetime.RunSerializedAsync<bool>(gate,
            () => throw new InvalidOperationException("Native presentation failed"), () => { }, CancellationToken.None));
        Assert.IsTrue(await ContentDialogLifetime.RunSerializedAsync(gate, () => Task.FromResult(true), () => { }, CancellationToken.None));
    }

    [TestMethod]
    public async Task CanceledQueuedDialogNeverShows()
    {
        using var gate = new SemaphoreSlim(0, 1);
        using var cancel = new CancellationTokenSource();
        var queued = ContentDialogLifetime.RunSerializedAsync<bool>(gate,
            () => throw new AssertFailedException("A canceled queued dialog must not open"), () => { }, cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => queued);
        Assert.AreEqual(0, gate.CurrentCount);
    }

    [TestMethod]
    public async Task AlreadyCanceledOwnershipDoesNotShowDialog()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => ContentDialogLifetime.RunAsync<bool>(
            () => throw new AssertFailedException("Canceled ownership must not display a dialog."),
            () => throw new AssertFailedException("No dialog needs dismissal."), cancellation.Token));
    }

    [TestMethod]
    public async Task ShutdownDismissesOwnedDialogAndUnblocksEvenWhenUiDoesNotComplete()
    {
        using var cancellation = new CancellationTokenSource();
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dismissals = 0;
        var ownership = ContentDialogLifetime.RunAsync(() => pending.Task, () => dismissals++, cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => ownership.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual(1, dismissals);
        Assert.IsFalse(pending.Task.IsCompleted, "A retiring dispatcher can leave native completion pending.");
        pending.SetResult(false);
    }

    [TestMethod]
    public async Task CancellationDuringPresentationCannotApproveEvenIfHideCompletesDialog()
    {
        using var cancellation = new CancellationTokenSource();
        var pending = new TaskCompletionSource<bool>();
        var ownership = ContentDialogLifetime.RunAsync(() => pending.Task, () => pending.TrySetResult(true), cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => ownership);
    }

    [TestMethod]
    public async Task CompletedDialogDoesNotDismissAfterLaterCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var dismissals = 0;
        Assert.IsTrue(await ContentDialogLifetime.RunAsync(() => Task.FromResult(true), () => dismissals++, cancellation.Token));
        await cancellation.CancelAsync();
        Assert.AreEqual(0, dismissals);
    }
}
