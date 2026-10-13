using System.Collections.Concurrent;
using DropSpace.App.Services;
using DropSpace.Core.Transfer;
using DropSpace.Infrastructure.Network;
using Microsoft.Extensions.Logging.Abstractions;

internal static class TrackedScenario
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    internal static async Task RunAsync()
    {
        var lifetime = new DeviceHandoffSendLifetime(NullLogger.Instance);
        var queued = new ConcurrentQueue<TrackedSend>();
        var attempts = new List<TrackedSend>();
        var tracker = new DeviceHandoffSendTracker((peer, endpoint, paths, progress, retired, token) =>
        {
            Check(queued.TryDequeue(out var next), "Tracker unexpectedly invoked a send delegate.");
            return next!.SendAsync(lifetime, progress, retired, token);
        }, NullLogger.Instance);
        var peer = new PeerDevice(Guid.NewGuid(), "Controlled peer", DevicePlatform.Windows,
            "fixture-fingerprint", PeerCapability.HandoffFiles, PeerTrustState.Trusted,
            DateTimeOffset.UtcNow, null);
        var stages = new ConcurrentQueue<HandoffSendStage>();
        Guid successfulId = default;
        EventHandler observer = (_, _) =>
        {
            var snapshot = tracker.GetSnapshots().FirstOrDefault(item => item.Id == successfulId);
            if (snapshot is not null) stages.Enqueue(snapshot.Stage);
        };
        tracker.Changed += observer;
        try
        {
            var rejected = new TrackedSend();
            var rejectedId = Start(rejected, "Disabled transfer");
            await WaitForAsync(tracker, snapshots => snapshots.Any(item => item.Id == rejectedId && item.IsSettled));
            Check(Snapshot(rejectedId) is { Stage: HandoffSendStage.Failed, FailureReason: HandoffSendFailureReason.Disabled } &&
                rejected.RetiredCount == 1 && !rejected.Started.Task.IsCompleted,
                "Disabled preadmission failed to retire exactly once or started actual work.");
            tracker.ClearCompleted();
            Check(tracker.GetSnapshots().Count == 0, "Rejected preadmission left a tracked operation behind.");
            lifetime.Enable();
            var successful = new TrackedSend(holdCaller: true);
            successfulId = Start(successful, "Successful transfer");
            var cancelled = new TrackedSend();
            var cancelledId = Start(cancelled, "Cancelled transfer");
            await Task.WhenAll(successful.Started.Task, cancelled.Started.Task).WaitAsync(Deadline);
            var initial = Snapshot(successfulId);
            Check(initial.Stage == HandoffSendStage.Preparing && initial.TotalBytes is null &&
                initial.CanCancel && !initial.IsSettled && initial.PeerDisplayName == peer.DisplayName,
                "Initial snapshot did not preserve preparing state and display metadata.");
            try
            {
                tracker.Start(peer, new Uri("https://unused.invalid"), ["unused-source"], "Excess transfer");
                throw new InvalidOperationException("Tracker admitted a third active operation.");
            }
            catch (HandoffSendUnavailableException error) when (error.Reason == HandoffSendUnavailableReason.Busy) { }

            successful.Report(TransferProgressStage.Preparing, 0, 100);
            successful.Report(TransferProgressStage.WaitingForApproval, 0, 100);
            successful.Report(TransferProgressStage.Sending, 25, 100);
            var sending = Snapshot(successfulId);
            Check(sending.Stage == HandoffSendStage.Sending && sending.TransferredBytes == 25 &&
                sending.TotalBytes == 100 && sending.Revision > initial.Revision,
                "Stage or acknowledged-byte progress did not advance.");

            // Stop observing without changing ownership, as when a view is rebuilt.
            tracker.Changed -= observer;
            successful.ReportAndCommit();
            Check(!tracker.Cancel(successfulId) && successful.CommitMarkers == 1,
                "Finalizing allowed cancellation or failed to cross the controlled commit boundary.");
            tracker.Changed += observer;
            var restored = Snapshot(successfulId);
            Check(restored.Stage == HandoffSendStage.Finalizing && !restored.CanCancel &&
                !restored.IsSettled && restored.Revision > sending.Revision,
                "A replacement observer could not reread the live finalizing operation.");

            cancelled.Report(TransferProgressStage.Sending, 10, 100);
            var beforeCancel = Snapshot(cancelledId);
            cancelled.BeforeFinalizing = () => Check(tracker.Cancel(cancelledId),
                "Cancellation did not win the controlled pre-finalizing boundary.");
            try
            {
                cancelled.ReportAndCommit();
                throw new InvalidOperationException("Cancel-first progress returned into the commit marker.");
            }
            catch (OperationCanceledException) { }
            await cancelled.CallerCompleted.Task.WaitAsync(Deadline);
            // No other operation changes here: Cancel publishes one revision,
            // and observing the canceled caller publishes the next revision.
            await WaitForAsync(tracker, snapshots => snapshots.Any(item =>
                item.Id == cancelledId && item.Revision >= beforeCancel.Revision + 2));
            Check(cancelled.CommitMarkers == 0 && !cancelled.Retired.Task.IsCompleted &&
                Snapshot(cancelledId) is { Stage: HandoffSendStage.Cancelling, IsSettled: false },
                "Canceled caller settled or crossed commit while actual ownership was pending.");
            var cancelling = Snapshot(cancelledId);
            cancelled.ReportDirect(TransferProgressStage.Sending, 99, 100);
            Check(Snapshot(cancelledId) == cancelling, "Retired progress changed a cancelling operation.");
            cancelled.Complete();
            await WaitForAsync(tracker, snapshots => snapshots.Any(item => item.Id == cancelledId && item.IsSettled));
            Check(Snapshot(cancelledId) is { Stage: HandoffSendStage.CancelledOutcomeUnknown, MayHaveReachedPeer: true },
                "Cancellation after network-stage admission claimed a definite remote outcome.");

            successful.Complete();
            await successful.Retired.Task.WaitAsync(Deadline);
            Check(!successful.CallerCompleted.Task.IsCompleted &&
                Snapshot(successfulId) is { Stage: HandoffSendStage.Finalizing, IsSettled: false, CanCancel: false },
                "Owner-first retirement settled before caller outcome arrived.");
            successful.ReleaseCaller.TrySetResult();
            await WaitForAsync(tracker, snapshots => snapshots.Any(item => item.Id == successfulId && item.IsSettled));
            var completed = Snapshot(successfulId);
            successful.ReportDirect(TransferProgressStage.Sending, 1, 100);
            Check(completed.Stage == HandoffSendStage.Completed && Snapshot(successfulId) == completed,
                "Late tracker progress overwrote a completed snapshot.");
            Check(stages.Contains(HandoffSendStage.Preparing) && stages.Contains(HandoffSendStage.WaitingForApproval) &&
                stages.Contains(HandoffSendStage.Sending) && stages.Contains(HandoffSendStage.Completed),
                "Changed observers did not receive the actual operation phases.");

            // Keep one real owner active while overflowing the small terminal history.
            var held = new TrackedSend();
            var heldId = Start(held, "Still active");
            await held.Started.Task.WaitAsync(Deadline);
            for (var index = 0; index < 11; index++)
            {
                var quick = new TrackedSend();
                quick.Complete();
                var id = Start(quick, $"History {index}");
                await WaitForAsync(tracker, snapshots => snapshots.Any(item => item.Id == id && item.IsSettled));
            }
            var bounded = tracker.GetSnapshots();
            Check(bounded.Count == 11 && bounded.Count(item => item.IsSettled) == 10 &&
                bounded.Single(item => !item.IsSettled).Id == heldId &&
                bounded.All(item => item.Id != successfulId && item.Id != cancelledId),
                "Terminal history exceeded ten records or evicted an active owner.");
            tracker.ClearCompleted();
            Check(tracker.GetSnapshots() is { Count: 1 } remaining && remaining[0].Id == heldId,
                "ClearCompleted removed live ownership or retained terminal history.");
            Check(tracker.Cancel(heldId), "Active preparing send was no longer cancelable.");
            held.Complete();
            await WaitForAsync(tracker, snapshots => snapshots.Any(item => item.Id == heldId && item.IsSettled));
            Check(Snapshot(heldId).Stage == HandoffSendStage.Cancelled,
                "Pre-offer cancellation incorrectly claimed an uncertain remote outcome.");
            tracker.ClearCompleted();
            Check(tracker.GetSnapshots().Count == 0 && attempts.Count == 15 &&
                attempts.Count(item => item.Started.Task.IsCompleted) == 14 &&
                attempts.All(item => item.RetiredCount == 1),
                "A callback was duplicated or completed owners remained after clearing history.");
            Console.WriteLine("PASS one tracked handoff scenario: one disabled preadmission retires once without starting work; fourteen actual sends; real phases and reread snapshots; two-active/ten-terminal bounds; both caller/owner retirement orders; cancelling retains ownership; finalizing-first refuses cancel and cancel-first blocks the commit marker; late progress cannot overwrite terminal state; clear retains active ownership.");
        }
        finally
        {
            tracker.Changed -= observer;
            lifetime.Disable();
            foreach (var send in attempts) { send.ReleaseCaller.TrySetResult(); send.Complete(); }
            await Task.WhenAll(attempts.Select(send => send.Retired.Task)).WaitAsync(Deadline);
            await Task.WhenAll(attempts.Select(send => send.CallerCompleted.Task)).WaitAsync(Deadline);
            await WaitForAsync(tracker, snapshots => snapshots.All(item => item.IsSettled));
        }

        Guid Start(TrackedSend send, string displayName)
        {
            queued.Enqueue(send);
            attempts.Add(send);
            return tracker.Start(peer, new Uri("https://unused.invalid"), ["unused-source"], displayName);
        }

        HandoffSendSnapshot Snapshot(Guid id) => tracker.GetSnapshots().Single(item => item.Id == id);
    }

    private static async Task WaitForAsync(DeviceHandoffSendTracker tracker,
        Func<IReadOnlyList<HandoffSendSnapshot>, bool> predicate)
    {
        var ready = Signal();
        EventHandler observer = (_, _) => { if (predicate(tracker.GetSnapshots())) ready.TrySetResult(); };
        tracker.Changed += observer;
        try
        {
            if (predicate(tracker.GetSnapshots())) ready.TrySetResult();
            await ready.Task.WaitAsync(Deadline);
        }
        finally { tracker.Changed -= observer; }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class TrackedSend(bool holdCaller = false)
    {
        private readonly TaskCompletionSource<TransferCompleteResponse> _actual = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IProgress<TransferProgress>? _scopedProgress;
        private IProgress<TransferProgress>? _trackerProgress;
        private CancellationToken _workToken;
        private int _retired;
        internal TaskCompletionSource Started { get; } = Signal();
        internal TaskCompletionSource Retired { get; } = Signal();
        internal TaskCompletionSource CallerCompleted { get; } = Signal();
        internal TaskCompletionSource ReleaseCaller { get; } = Signal();
        internal Action? BeforeFinalizing { get; set; }
        internal int CommitMarkers { get; private set; }
        internal int RetiredCount => Volatile.Read(ref _retired);

        internal async Task<TransferCompleteResponse> SendAsync(DeviceHandoffSendLifetime lifetime,
            IProgress<TransferProgress> progress, Action onRetired, CancellationToken token)
        {
            _trackerProgress = progress;
            try
            {
                var result = await lifetime.RunAsync((workToken, scopedProgress) =>
                {
                    _workToken = workToken;
                    _scopedProgress = scopedProgress;
                    Started.TrySetResult();
                    return _actual.Task;
                }, new BeforeProgress(progress, this), token, () =>
                {
                    Interlocked.Increment(ref _retired);
                    onRetired();
                    Retired.TrySetResult();
                });
                if (holdCaller) await ReleaseCaller.Task;
                return result;
            }
            finally { CallerCompleted.TrySetResult(); }
        }

        internal void Report(TransferProgressStage stage, long bytes, long total) =>
            _scopedProgress!.Report(Progress(stage, bytes, total));
        internal void ReportDirect(TransferProgressStage stage, long bytes, long total) =>
            _trackerProgress!.Report(Progress(stage, bytes, total));
        internal void ReportAndCommit()
        {
            Report(TransferProgressStage.Finalizing, 100, 100);
            _workToken.ThrowIfCancellationRequested();
            CommitMarkers++;
        }
        internal void Complete() => _actual.TrySetResult(new TransferCompleteResponse(
            Guid.NewGuid(), TransferSessionState.Completed, ["controlled-file"], null));
        private static TransferProgress Progress(TransferProgressStage stage, long bytes, long total) =>
            new(Guid.NewGuid(), Guid.NewGuid(), bytes, total) { Stage = stage };

        private sealed class BeforeProgress(IProgress<TransferProgress> target, TrackedSend fixture) : IProgress<TransferProgress>
        {
            public void Report(TransferProgress value)
            {
                if (value.Stage == TransferProgressStage.Finalizing) fixture.BeforeFinalizing?.Invoke();
                target.Report(value);
            }
        }
    }
}
