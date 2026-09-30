using DropSpace.App.Views;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class ContentDialogLifetimeTests
{
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
