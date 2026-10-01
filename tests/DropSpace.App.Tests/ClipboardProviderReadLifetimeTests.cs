using DropSpace.App.Services;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class ClipboardProviderReadLifetimeTests
{
    [TestMethod]
    public async Task SupersessionReleasesWaiterButRetainsLateStreamUntilNativeCompletion()
    {
        using var stop = new CancellationTokenSource();
        var native = new TaskCompletionSource<TrackedStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = false;
        var stream = new TrackedStream();
        var chain = ReadChainAsync();
        var waiter = chain.WaitAsync(stop.Token);
        stop.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => waiter);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.IsFalse(chain.IsCompleted);
        Assert.IsFalse(closed);
        Assert.IsFalse(stream.Disposed);
        native.SetResult(stream);
        await Assert.ThrowsAsync<OperationCanceledException>(() => chain);
        Assert.IsTrue(closed);
        Assert.IsTrue(stream.Disposed, "The late stream must be reclaimed by its original chain.");

        async Task ReadChainAsync()
        {
            using var result = await ClipboardProviderReadLifetime.CompleteAsync(native.Task,
                () => canceled.TrySetResult(), () => closed = true, stop.Token);
            stop.Token.ThrowIfCancellationRequested();
        }
    }

    [TestMethod]
    public async Task BlockingNativeCancelNeverBlocksReplacementAndCloseWaitsForCancel()
    {
        using var stop = new CancellationTokenSource();
        using var releaseCancel = new ManualResetEventSlim();
        var native = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = false;
        var owned = ClipboardProviderReadLifetime.CompleteAsync(native.Task,
            () => { cancelEntered.TrySetResult(); releaseCancel.Wait(); }, () => closed = true, stop.Token);
        var waiter = owned.WaitAsync(stop.Token);
        try
        {
            stop.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => waiter);
            await cancelEntered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            native.SetResult(8);
            Assert.IsFalse(owned.IsCompleted);
            Assert.IsFalse(closed);
        }
        finally { native.TrySetResult(8); releaseCancel.Set(); }
        Assert.AreEqual(8u, await owned.WaitAsync(TimeSpan.FromSeconds(4)));
        Assert.IsTrue(closed);
    }

    [TestMethod]
    public async Task SuccessfulReadClosesOperationWithoutCancelingOrDisposingItsResult()
    {
        using var stop = new CancellationTokenSource();
        var stream = new TrackedStream();
        var closed = false;
        using var result = await ClipboardProviderReadLifetime.CompleteAsync(Task.FromResult(stream),
            () => Assert.Fail("A completed read does not require cancellation."), () => closed = true, stop.Token);
        Assert.AreSame(stream, result);
        Assert.IsTrue(closed);
        Assert.IsFalse(stream.Disposed);
    }

    [TestMethod]
    public async Task NativeFailureStillClosesItsOperation()
    {
        var closed = false;
        await Assert.ThrowsAsync<IOException>(() => ClipboardProviderReadLifetime.CompleteAsync(
            Task.FromException<uint>(new IOException()), () => { }, () => closed = true, CancellationToken.None));
        Assert.IsTrue(closed);
    }

    [TestMethod]
    public async Task CloseFailureKeepsTheReadOwnedUntilTeardownSucceeds()
    {
        var firstClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canClose = 0;
        var owned = ClipboardProviderReadLifetime.CompleteAsync(Task.FromResult(12u), () => { }, () =>
        {
            firstClose.TrySetResult();
            if (Volatile.Read(ref canClose) == 0) throw new IOException();
        }, CancellationToken.None);
        try
        {
            await firstClose.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.IsFalse(owned.IsCompleted);
        }
        finally { Volatile.Write(ref canClose, 1); }
        Assert.AreEqual(12u, await owned.WaitAsync(TimeSpan.FromSeconds(4)));
    }

    private sealed class TrackedStream : MemoryStream
    {
        internal bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
