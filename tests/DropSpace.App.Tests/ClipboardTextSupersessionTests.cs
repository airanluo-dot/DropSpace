using DropSpace.App.Services;
using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Foundation;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class ClipboardTextSupersessionTests
{
    [TestMethod]
    public async Task ANewSequenceReleasesTheWaiterWithoutCompletingItsNativeTask()
    {
        var coordinator = new ClipboardCaptureService.TextReadCoordinator();
        var original = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var read = coordinator.Register(28, () => 28, _ => { Assert.Fail("The current sequence must not be queued again."); return false; });
        try
        {
            var waiting = original.Task.WaitAsync(read.SupersededToken);
            coordinator.Notify(38, 38, _ => { Assert.Fail("The notification already admitted this sequence."); return false; });
            await Assert.ThrowsAsync<OperationCanceledException>(() => waiting);
            Assert.IsFalse(original.Task.IsCompleted, "Releasing the single reader must not pretend native data completed.");
        }
        finally { coordinator.Release(read); }
    }

    [TestMethod]
    public void RegistrationRechecksNativeStateAndAdmitsItBeforeCancellation()
    {
        var coordinator = new ClipboardCaptureService.TextReadCoordinator();
        var queued = new List<uint>();
        var read = coordinator.Register(28, () => 38, sequence => { queued.Add(sequence); return true; });
        try
        {
            Assert.IsTrue(read.IsSuperseded, "A replacement published before registration must release the obsolete read.");
            CollectionAssert.AreEqual(new uint[] { 38 }, queued);
        }
        finally { coordinator.Release(read); }
    }

    [TestMethod]
    public void ANotificationDuringRegistrationCannotBeLostBeforeTheWaiterStarts()
    {
        var coordinator = new ClipboardCaptureService.TextReadCoordinator();
        var read = coordinator.Register(28, () =>
        {
            coordinator.Notify(38, 38, _ => false);
            return 28;
        }, _ => false);
        try { Assert.IsTrue(read.IsSuperseded, "The active owner must be published before native state is reread."); }
        finally { coordinator.Release(read); }
    }

    [TestMethod]
    public void AStaleNotificationUsesActualNativeSequenceAndCannotCancelTheCurrentRead()
    {
        var coordinator = new ClipboardCaptureService.TextReadCoordinator();
        var read = coordinator.Register(38, () => 38, _ => false);
        try
        {
            coordinator.Notify(38, 28, _ => { Assert.Fail("An old sample must not create new work for the active current read."); return false; });
            Assert.IsFalse(read.IsSuperseded);
        }
        finally { coordinator.Release(read); }
    }

    [TestMethod]
    public void ProvenAdvancementIsAdmittedBeforeSupersedingAnOlderRead()
    {
        var coordinator = new ClipboardCaptureService.TextReadCoordinator();
        var read = coordinator.Register(28, () => 28, _ => false);
        try
        {
            coordinator.Notify(38, 28, sequence =>
            {
                Assert.AreEqual(38u, sequence);
                Assert.IsFalse(read.SupersededToken.IsCancellationRequested, "Latest content must be admitted first.");
                return true;
            });
            Assert.IsTrue(read.IsSuperseded);
        }
        finally { coordinator.Release(read); }
    }

    [TestMethod]
    public void FailedAdmissionSameSequenceAndUnknownSequencePreserveTheRead()
    {
        var coordinator = new ClipboardCaptureService.TextReadCoordinator();
        var read = coordinator.Register(28, () => 28, _ => false);
        try
        {
            coordinator.Notify(38, 28, _ => false);
            coordinator.Notify(28, 28, _ => { Assert.Fail("Same-sequence duplicates must not be queued again."); return false; });
            coordinator.Notify(0, 0, _ => { Assert.Fail("Unknown native state does not establish supersession."); return false; });
            Assert.IsFalse(read.IsSuperseded);
        }
        finally { coordinator.Release(read); }
        var unknown = coordinator.Register(0, () => 38, _ => { Assert.Fail("An unknown original sequence must retain its existing behavior."); return false; });
        try { Assert.IsFalse(unknown.IsSuperseded); }
        finally { coordinator.Release(unknown); }
    }

    [TestMethod]
    public void SequenceWraparoundStillSupersedesAndRetiringAnOldLeaseKeepsTheNewOwner()
    {
        var coordinator = new ClipboardCaptureService.TextReadCoordinator();
        var old = coordinator.Register(uint.MaxValue, () => uint.MaxValue, _ => false);
        coordinator.Notify(1, 1, _ => false);
        var current = coordinator.Register(1, () => 1, _ => false);
        try
        {
            Assert.IsTrue(old.IsSuperseded);
            coordinator.Release(old);
            coordinator.Notify(2, 2, _ => false);
            Assert.IsTrue(current.IsSuperseded, "Releasing an old lease must not erase the newer active owner.");
            old.Supersede(); // A notification racing disposal must remain safe.
        }
        finally { coordinator.Release(current); }
    }

    [TestMethod]
    public async Task BlockingNativeCancellationDoesNotBlockTheWaiterAndCloseWaitsForBothOwners()
    {
        var original = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancelRelease = new ManualResetEventSlim();
        var closed = false;
        var released = false;
        var cleanup = ClipboardCaptureService.ObserveTextReadCompletionAsync(original.Task,
            () => { cancelEntered.SetResult(); cancelRelease.Wait(); },
            () => closed = true, requestCancellation: true, onClosed: () => released = true);
        try
        {
            await cancelEntered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            original.SetResult("synthetic");
            Assert.IsFalse(cleanup.IsCompleted);
            Assert.IsFalse(closed);
            Assert.IsFalse(released, "A native slot must stay owned until cancellation and Close actually finish.");
            cancelRelease.Set();
            await cleanup.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.IsTrue(closed);
            Assert.IsTrue(released);
        }
        finally { cancelRelease.Set(); await cleanup.WaitAsync(TimeSpan.FromSeconds(4)); }
    }

    [TestMethod]
    public async Task NativeCompletionAndCleanupFailuresAreObservedWithoutStrandingTheSlot()
    {
        var failures = new List<Exception>();
        var closed = false;
        var released = false;
        await ClipboardCaptureService.ObserveTextReadCompletionAsync(Task.FromException<string>(new IOException()),
            () => throw new InvalidOperationException(), () => closed = true, requestCancellation: true,
            onFailure: exception => { lock (failures) failures.Add(exception); }, onClosed: () => released = true);
        Assert.AreEqual(2, failures.Count);
        Assert.IsTrue(closed);
        Assert.IsTrue(released);
    }

    [TestMethod]
    public async Task ShutdownCancellationReleasesOnlyTheManagedWaiter()
    {
        var coordinator = new ClipboardCaptureService.TextReadCoordinator();
        var read = coordinator.Register(28, () => 28, _ => false);
        var native = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var shutdown = new CancellationTokenSource();
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token, read.SupersededToken);
            var waiting = native.Task.WaitAsync(linked.Token);
            shutdown.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => waiting);
            Assert.IsFalse(read.IsSuperseded);
            Assert.IsFalse(native.Task.IsCompleted);
        }
        finally { coordinator.Release(read); }
    }

    [TestMethod]
    public async Task EightRetiredOperationsKeepTheirSlotsUntilNativeTeardownCompletes()
    {
        await using var capture = new ClipboardCaptureService(null!, null!, null!, null!, null!, null!, null!, null!,
            NullLogger<ClipboardCaptureService>.Instance);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var slots = (SemaphoreSlim)typeof(ClipboardCaptureService).GetField("_textReadSlots", flags)!.GetValue(capture)!;
        var retired = (ConcurrentDictionary<long, Task>)typeof(ClipboardCaptureService).GetField("_retiredTextReads", flags)!.GetValue(capture)!;
        var retire = typeof(ClipboardCaptureService).GetMethod("RetireTextRead", flags)!;
        var nativeReads = Enumerable.Range(0, 8).Select(_ => new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        Task[] cleanup = [];
        try
        {
            foreach (var original in nativeReads)
            {
                Assert.IsTrue(await slots.WaitAsync(TimeSpan.FromSeconds(4)));
                retire.Invoke(capture, [original.Task, (Action)(() => { }), (Action)(() => { }), true]);
            }
            cleanup = retired.Values.ToArray();
            Assert.AreEqual(8, cleanup.Length);
            Assert.AreEqual(0, slots.CurrentCount, "Cancelled waiters do not release still-owned native operations.");
            using var stop = new CancellationTokenSource();
            var saturated = slots.WaitAsync(stop.Token);
            Assert.IsFalse(saturated.IsCompleted);
            stop.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => saturated);
            nativeReads[0].SetResult("synthetic");
            Assert.IsTrue(await slots.WaitAsync(TimeSpan.FromSeconds(4)), "Only actual native teardown frees the ninth admission.");
            slots.Release();
        }
        finally
        {
            foreach (var original in nativeReads) original.TrySetResult("synthetic");
            await Task.WhenAll(cleanup).WaitAsync(TimeSpan.FromSeconds(4));
        }
    }

    [TestMethod]
    public async Task FailedBridgeObservationRetainsTheStartedOperationUntilTerminalState()
    {
        var statusRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminal = 0;
        var results = 0;
        var observation = ClipboardCaptureService.ObserveUnbridgedTextReadAsync(
            () => { statusRead.TrySetResult(); return Volatile.Read(ref terminal) == 0 ? AsyncStatus.Started : AsyncStatus.Completed; },
            () => { Interlocked.Increment(ref results); return "synthetic"; });
        try
        {
            await statusRead.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.IsFalse(observation.IsCompleted);
            Assert.AreEqual(0, results);
        }
        finally { Volatile.Write(ref terminal, 1); }
        await observation.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.AreEqual(1, results);
    }

    [TestMethod]
    public async Task ACloseFailureKeepsOwnershipUntilCloseActuallySucceeds()
    {
        var firstClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowClose = 0;
        var closed = false;
        var released = false;
        var cleanup = ClipboardCaptureService.ObserveTextReadCompletionAsync(Task.FromResult("synthetic"),
            () => Assert.Fail("No cancellation is needed for a completed read."), () =>
            {
                firstClose.TrySetResult();
                if (Volatile.Read(ref allowClose) == 0) throw new IOException();
                closed = true;
            }, requestCancellation: false, onClosed: () => released = true);
        try
        {
            await firstClose.Task.WaitAsync(TimeSpan.FromSeconds(4));
            Assert.IsFalse(cleanup.IsCompleted);
            Assert.IsFalse(released);
        }
        finally { Volatile.Write(ref allowClose, 1); }
        await cleanup.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.IsTrue(closed);
        Assert.IsTrue(released);
    }
}
