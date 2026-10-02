using DropSpace.App.Services.Media;
using DropSpace.Core.Media;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class MediaPublisherTimeoutTests
{
    [TestMethod]
    public async Task OptionalArtworkIgnoringCancellationCannotFreezeMetadataConsumer()
    {
        await using var service = new WindowsMediaSessionService(NullLogger<WindowsMediaSessionService>.Instance);
        var native = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Assert.IsNull(await service.ReadOptionalArtworkAsync(_ => native.Task, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
            CollectionAssert.AreEqual(new byte[] { 2 }, await service.ReadOptionalArtworkAsync(_ => Task.FromResult<byte[]?>([2]), CancellationToken.None));
        }
        finally { native.TrySetResult([1]); }
    }

    [TestMethod]
    public async Task MetadataThatIgnoresCancellationCannotHoldTheSessionGateForever()
    {
        var native = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        var read = WindowsMediaSessionService.ReadStableMetadataAsync(_ => native.Task, () => 0, stop.Token);
        await stop.CancelAsync();
        try { await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(5))); }
        finally { native.TrySetResult("Old title"); }
    }

    [TestMethod]
    public async Task UnresponsiveSelectionProbeCannotBlockPreferredRendererMetadata()
    {
        var preferred = new object();
        var sibling = new object();
        var native = new TaskCompletionSource<WindowsMediaSessionService.SessionSelection?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            Assert.AreSame(preferred, await WindowsMediaSessionService.SelectRicherSessionAsync(
                new[] { preferred, sibling }, _ => "player", (_, _) => native.Task,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { native.TrySetResult(null); }
    }
    [TestMethod]
    public async Task NewTitlePublishesBeforeUnresponsiveOptionalArtworkFinishes()
    {
        await using var service = new WindowsMediaSessionService(NullLogger<WindowsMediaSessionService>.Instance);
        var artwork = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new TaskCompletionSource<MediaSessionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += (_, value) => published.TrySetResult(value);
        var snapshot = MediaSessionSnapshot.Empty with { SessionId = "session", TrackTitle = "New title" };
        var read = service.CompleteArtworkAsync(snapshot, 0, _ => artwork.Task, new object(), CancellationToken.None);
        try
        {
            Assert.AreEqual("New title", (await published.Task.WaitAsync(TimeSpan.FromSeconds(1))).TrackTitle);
            Assert.IsFalse(read.IsCompleted, "Title must be observable before thumbnail completion.");
            Assert.AreEqual("New title", (await read.WaitAsync(TimeSpan.FromSeconds(5))).TrackTitle);
        }
        finally { artwork.TrySetResult([1]); }
    }

    [TestMethod]
    public async Task LatePreviousTrackArtworkCannotRestorePreviousTitleOrCover()
    {
        await using var service = new WindowsMediaSessionService(NullLogger<WindowsMediaSessionService>.Instance);
        var oldArtwork = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = MediaSessionSnapshot.Empty with { SessionId = "old-session", TrackTitle = "Old" };
        var first = service.CompleteArtworkAsync(old, 0, _ => oldArtwork.Task, new object(), CancellationToken.None);
        service.InvalidateMetadata();
        var current = old with { SessionId = "replacement-session", TrackTitle = "New", Artwork = [2] };
        var second = await service.CompleteArtworkAsync(current, 1, _ => Task.FromResult<byte[]?>([2]), new object(), CancellationToken.None);
        oldArtwork.SetResult([1]);
        var late = await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("New", late.TrackTitle);
        CollectionAssert.AreEqual(new byte[] { 2 }, late.Artwork);
        Assert.AreEqual(second, service.Current);
    }

    [TestMethod]
    public async Task RepeatedEquivalentMetadataNotificationsDoNotStarveTrackPublication()
    {
        long revision = 0;
        var reads = 0;
        var result = await WindowsMediaSessionService.ReadStableMetadataAsync(_ =>
        {
            revision++;
            reads++;
            return Task.FromResult("New track");
        }, () => revision, CancellationToken.None, static (left, right) => left == right);
        Assert.AreEqual("New track", result.Value);
        Assert.AreEqual(2, reads);
        Assert.AreEqual(2L, result.Revision);
    }

    [TestMethod]
    public async Task EquivalentMetadataStillPublishesWhenAnotherNotificationArrivesBeforePublication()
    {
        await using var service = new WindowsMediaSessionService(NullLogger<WindowsMediaSessionService>.Instance);
        long revision = 0;
        for (var transition = 1; transition <= 32; transition++)
        {
            var title = $"Track {transition}";
            var result = await WindowsMediaSessionService.ReadStableMetadataAsync(_ =>
            {
                revision++;
                service.InvalidateMetadata();
                return Task.FromResult(MediaSessionSnapshot.Empty with { SessionId = "session", TrackTitle = title });
            }, () => revision, CancellationToken.None, static (left, right) => left.IsSameTrack(right));
            Assert.IsTrue(result.EquivalentDespiteRevisionChange);
            // This is the previously untested window after stable metadata returned.
            revision++;
            service.InvalidateMetadata();
            var artworkReads = 0;
            var completed = await service.CompleteArtworkAsync(result.Value!, result.Revision,
                _ => { artworkReads++; return Task.FromResult<byte[]?>([1]); }, new object(),
                CancellationToken.None, result.EquivalentDespiteRevisionChange);
            Assert.AreEqual(title, completed.TrackTitle);
            Assert.AreEqual(title, service.Current.TrackTitle);
            Assert.IsNull(completed.Artwork);
            Assert.AreEqual(0, artworkReads, "Equivalent identity permission must not admit stale artwork.");
        }
    }

    [TestMethod]
    public async Task UnconfirmedMetadataCannotPublishAfterALaterTrackNotification()
    {
        await using var service = new WindowsMediaSessionService(NullLogger<WindowsMediaSessionService>.Instance);
        var stable = await WindowsMediaSessionService.ReadStableMetadataAsync(
            _ => Task.FromResult(MediaSessionSnapshot.Empty with { SessionId = "session", TrackTitle = "Old" }),
            () => 0, CancellationToken.None);
        Assert.IsFalse(stable.EquivalentDespiteRevisionChange);
        service.InvalidateMetadata();
        var result = await service.CompleteArtworkAsync(stable.Value!, stable.Revision,
            _ => Task.FromResult<byte[]?>([1]), new object(), CancellationToken.None, stable.EquivalentDespiteRevisionChange);
        Assert.AreEqual(MediaSessionSnapshot.Empty, result);
        Assert.AreEqual(MediaSessionSnapshot.Empty, service.Current);
    }

    [TestMethod]
    public async Task TrulyDifferentConsecutiveMetadataIsStillRejected()
    {
        long revision = 0;
        var result = await WindowsMediaSessionService.ReadStableMetadataAsync(_ =>
        {
            revision++;
            return Task.FromResult($"Track {revision}");
        }, () => revision, CancellationToken.None, static (left, right) => left == right);
        Assert.IsNull(result.Value);
    }
    [TestMethod]
    public async Task ExactWinRtBridgeKeepsNativeOwnershipAfterManagedWaitTimesOut()
    {
        var operations = new BoundedMediaOperation(4, 2);
        var owner = new object();
        var first = new FakeNativeOperation();
        var second = new FakeNativeOperation();
        var calls = 0;
        Task<string> Read(FakeNativeOperation native) => operations.RunAsync(owner, token =>
        {
            calls++;
            return WindowsMediaSessionService.AwaitNativeAsync(native, token);
        }, TimeSpan.FromMilliseconds(50), CancellationToken.None);
        try
        {
            await Assert.ThrowsExactlyAsync<TimeoutException>(() => Read(first));
            await first.CancelRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(Windows.Foundation.AsyncStatus.Started, first.Status);
            Assert.AreEqual(1, operations.Outstanding);
            await Assert.ThrowsExactlyAsync<TimeoutException>(() => Read(second));
            await second.CancelRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => Read(new FakeNativeOperation()));
            Assert.AreEqual(2, calls);
            Assert.AreEqual(2, operations.Outstanding);
        }
        finally { first.Complete("Late old title"); second.Complete("Late old title"); }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (operations.Outstanding != 0) await Task.Delay(1, timeout.Token);
    }

    [TestMethod]
    public async Task OptionalDiscoveryTimeoutPreservesAlreadyReadHealthyCurrent()
    {
        var current = new object();
        var broken = new object();
        var native = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var result = await WindowsMediaSessionService.ReadCandidatesAsync(new[] { current, broken },
                (candidate, _) => ReferenceEquals(candidate, current) ? Task.FromResult("Healthy current") : native.Task,
                TimeSpan.FromMilliseconds(50), CancellationToken.None);
            Assert.AreEqual(1, result.Count);
            Assert.AreSame(current, result[0].Candidate);
            Assert.AreEqual("Healthy current", result[0].Value);
        }
        finally { native.SetResult("Too late"); }
    }

    [TestMethod]
    public async Task DiscoveryHasNoFirst512SliceThatHidesRicherRenderer()
    {
        var sessions = Enumerable.Range(0, 600).Select(_ => new object()).ToArray();
        var result = await WindowsMediaSessionService.ReadCandidatesAsync(sessions,
            (candidate, _) => Task.FromResult(ReferenceEquals(candidate, sessions[^1]) ? "Rich renderer" : "Other"),
            TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.AreEqual("Rich renderer", result[^1].Value);
    }

    [TestMethod]
    public async Task ThrowingNativeCancelCannotReleaseAnUncompletedOperation()
    {
        var native = new FakeNativeOperation { ThrowOnCancel = true };
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();
        var task = WindowsMediaSessionService.AwaitNativeAsync(native, stop.Token);
        Assert.IsFalse(task.IsCompleted);
        native.Complete("Still owned until completion");
        Assert.AreEqual("Still owned until completion", await task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task SdkCancellationBridgeIsNotProofOfNativeCompletion()
    {
        var native = new FakeNativeOperation();
        using var stop = new CancellationTokenSource();
        var bridge = native.AsTask(stop.Token);
        await stop.CancelAsync();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => bridge);
        Assert.AreEqual(Windows.Foundation.AsyncStatus.Started, native.Status);
        native.Complete("Actual completion arrives later");
    }

    [TestMethod]
    public async Task CanceledNativeImageActionKeepsItsStreamUntilActualCompletion()
    {
        var action = new FakeNativeAction();
        using var stop = new CancellationTokenSource();
        var disposed = false;
        async Task DecodeAsync()
        {
            try
            {
                await WindowsMediaSessionService.AwaitNativeAsync(action, stop.Token);
                stop.Token.ThrowIfCancellationRequested();
            }
            finally { disposed = true; }
        }
        var decode = DecodeAsync();
        await stop.CancelAsync();
        await action.CancelRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(decode.IsCompleted);
        Assert.IsFalse(disposed, "Native image input must remain alive while the action is Started.");
        action.Complete();
        await Assert.ThrowsAsync<OperationCanceledException>(() => decode.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsTrue(disposed);
    }

    [TestMethod]
    public async Task AlreadyCanceledImageWaitNeverRunsSlowNativeCancelInline()
    {
        using var release = new ManualResetEventSlim();
        var action = new FakeNativeAction { OnCancel = () => release.Wait(TimeSpan.FromSeconds(5)) };
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();
        // This is the same synchronous entry path used inside the image UI dispatcher.
        var wait = WindowsMediaSessionService.AwaitNativeAsync(action, stop.Token);
        try
        {
            await action.CancelRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            action.Complete();
            Assert.IsFalse(wait.IsCompleted, "Cancellation owns the native object until the callback exits.");
        }
        finally { release.Set(); }
        await wait.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task RestartReadinessWaitsForARealSnapshotAndAcceptsNoPlayer()
    {
        await using var service = new WindowsMediaSessionService(NullLogger<WindowsMediaSessionService>.Instance);
        using var lifetime = new CancellationTokenSource();
        var changes = System.Threading.Channels.Channel.CreateBounded<bool>(1);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshot = new TaskCompletionSource<MediaSessionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = service.ConsumeAsync(changes.Reader, lifetime.Token, _ =>
        {
            entered.TrySetResult();
            return snapshot.Task;
        }, ready);
        changes.Writer.TryWrite(true);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsFalse(ready.Task.IsCompleted, "Manager creation alone must not declare reconnection successful.");
            snapshot.SetResult(MediaSessionSnapshot.Empty);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(service.IsAvailable);
            Assert.AreEqual(MediaSessionSnapshot.Empty, service.Current);
        }
        finally
        {
            snapshot.TrySetResult(MediaSessionSnapshot.Empty);
            await lifetime.CancelAsync();
            await consumer.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    public async Task RestartReadinessReportsFirstSnapshotFailureInsteadOfFalseSuccess()
    {
        await using var service = new WindowsMediaSessionService(NullLogger<WindowsMediaSessionService>.Instance);
        using var lifetime = new CancellationTokenSource();
        var changes = System.Threading.Channels.Channel.CreateBounded<bool>(1);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = service.ConsumeAsync(changes.Reader, lifetime.Token,
            _ => Task.FromException<MediaSessionSnapshot>(new TimeoutException("Publisher is not responding")), ready);
        changes.Writer.TryWrite(true);
        try
        {
            await Assert.ThrowsExactlyAsync<TimeoutException>(() => ready.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsFalse(service.IsAvailable);
            Assert.AreEqual(nameof(TimeoutException), service.AvailabilityReason);
        }
        finally
        {
            await lifetime.CancelAsync();
            await consumer.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class FakeNativeAction : Windows.Foundation.IAsyncAction
    {
        private readonly object _gate = new();
        private Windows.Foundation.AsyncActionCompletedHandler? _completed;
        private Windows.Foundation.AsyncStatus _status = Windows.Foundation.AsyncStatus.Started;
        public Action? OnCancel { get; init; }
        public TaskCompletionSource CancelRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public uint Id => 2;
        public Exception ErrorCode => null!;
        public Windows.Foundation.AsyncStatus Status { get { lock (_gate) return _status; } }
        public Windows.Foundation.AsyncActionCompletedHandler Completed
        {
            get { lock (_gate) return _completed!; }
            set
            {
                bool complete;
                lock (_gate) { _completed = value; complete = _status == Windows.Foundation.AsyncStatus.Completed; }
                if (complete) value(this, Windows.Foundation.AsyncStatus.Completed);
            }
        }
        public void GetResults() { }
        public void Cancel() { CancelRequested.TrySetResult(); OnCancel?.Invoke(); }
        public void Close() { }
        public void Complete()
        {
            Windows.Foundation.AsyncActionCompletedHandler? callback;
            lock (_gate) { _status = Windows.Foundation.AsyncStatus.Completed; callback = _completed; }
            callback?.Invoke(this, Windows.Foundation.AsyncStatus.Completed);
        }
    }

    private sealed class FakeNativeOperation : Windows.Foundation.IAsyncOperation<string>
    {
        private readonly object _gate = new();
        private Windows.Foundation.AsyncOperationCompletedHandler<string>? _completed;
        private Windows.Foundation.AsyncStatus _status = Windows.Foundation.AsyncStatus.Started;
        private string _result = string.Empty;
        public TaskCompletionSource CancelRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public uint Id => 1;
        public Exception ErrorCode => null!;
        public Windows.Foundation.AsyncStatus Status { get { lock (_gate) return _status; } }
        public Windows.Foundation.AsyncOperationCompletedHandler<string> Completed
        {
            get { lock (_gate) return _completed!; }
            set
            {
                bool complete;
                lock (_gate) { _completed = value; complete = _status == Windows.Foundation.AsyncStatus.Completed; }
                if (complete) value(this, Windows.Foundation.AsyncStatus.Completed);
            }
        }
        public string GetResults() { lock (_gate) return _result; }
        public bool ThrowOnCancel { get; init; }
        public void Cancel()
        {
            CancelRequested.TrySetResult();
            if (ThrowOnCancel) throw new System.Runtime.InteropServices.COMException("Native cancellation failed");
        }
        public void Close() { }
        public void Complete(string result)
        {
            Windows.Foundation.AsyncOperationCompletedHandler<string>? callback;
            lock (_gate) { _result = result; _status = Windows.Foundation.AsyncStatus.Completed; callback = _completed; }
            callback?.Invoke(this, Windows.Foundation.AsyncStatus.Completed);
        }
    }
}
