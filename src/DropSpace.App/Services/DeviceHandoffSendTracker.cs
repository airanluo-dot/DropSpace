using DropSpace.Core.Transfer;
using DropSpace.Infrastructure.Network;
using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services;

public enum HandoffSendStage
{
    Preparing,
    WaitingForApproval,
    Sending,
    Finalizing,
    Cancelling,
    Completed,
    Cancelled,
    CancelledOutcomeUnknown,
    Failed,
}

public enum HandoffSendFailureReason
{
    None,
    Disabled,
    Busy,
    PreparationLimit,
    Rejected,
    ApprovalTimeout,
    Network,
    Source,
    Unknown,
}

public sealed record HandoffSendSnapshot(
    Guid Id,
    long Revision,
    string DisplayName,
    string PeerDisplayName,
    HandoffSendStage Stage,
    long TransferredBytes,
    long? TotalBytes,
    HandoffSendFailureReason FailureReason,
    bool CanCancel,
    bool IsSettled,
    bool MayHaveReachedPeer);

internal delegate Task<TransferCompleteResponse> TrackedHandoffSend(
    PeerDevice peer,
    Uri endpoint,
    IReadOnlyList<string> sourcePaths,
    IProgress<TransferProgress> progress,
    Action onRetired,
    CancellationToken cancellationToken);

/// <summary>Session-only user operations; windows subscribe without owning their cancellation.</summary>
internal sealed class DeviceHandoffSendTracker(TrackedHandoffSend send, ILogger logger)
{
    private const int MaximumActive = 2;
    private const int MaximumCompleted = 10;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly Queue<Guid> _completed = new();
    private long _revision;

    internal event EventHandler? Changed;

    internal Guid Start(PeerDevice peer, Uri endpoint, IReadOnlyList<string> sourcePaths, string displayName)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(sourcePaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        var paths = sourcePaths.ToArray();
        Entry entry;
        lock (_gate)
        {
            // Keep the two user operations until BOTH caller outcome and real
            // owner retirement are known; service admission also includes text sends.
            if (_entries.Values.Count(item => !item.IsSettled) >= MaximumActive)
                throw new HandoffSendUnavailableException(HandoffSendUnavailableReason.Busy);
            entry = new Entry(displayName, peer.DisplayName, ++_revision);
            _entries.Add(entry.Id, entry);
        }

        PublishChanged();
        // Register before invoking: an admission rejection or healthy completion
        // can retire synchronously, before its caller outcome is delivered.
        _ = ObserveSendAsync(entry, peer, endpoint, paths);
        return entry.Id;
    }

    internal IReadOnlyList<HandoffSendSnapshot> GetSnapshots()
    {
        lock (_gate)
            return _entries.Values.OrderBy(entry => entry.IsSettled)
                .ThenByDescending(entry => entry.CreatedOrder).Select(Snapshot).ToArray();
    }

    internal bool Cancel(Guid id)
    {
        Entry entry;
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out entry!) || !CanCancel(entry)) return false;
            entry.CancelRequested = true;
            entry.Stage = HandoffSendStage.Cancelling;
            entry.Revision = ++_revision;
            // Mark our token before releasing stage arbitration. CancelAsync
            // schedules callbacks; no external cancellation callback runs here.
            entry.RequestCancellation();
        }

        PublishChanged();
        return true;
    }

    internal void ClearCompleted()
    {
        bool changed;
        lock (_gate)
        {
            changed = _completed.Count > 0;
            while (_completed.TryDequeue(out var id)) _entries.Remove(id);
        }
        if (changed) PublishChanged();
    }

    private async Task ObserveSendAsync(Entry entry, PeerDevice peer, Uri endpoint, IReadOnlyList<string> paths)
    {
        Outcome outcome;
        try
        {
            Task<TransferCompleteResponse> pending;
            try
            {
                pending = send(peer, endpoint, paths,
                    new SendProgress(this, entry), () => OnOwnerRetired(entry), entry.Token);
            }
            catch
            {
                // A synchronously throwing adapter never returned an owned task.
                // The real service also guarantees retirement on all admission failures.
                OnOwnerRetired(entry);
                throw;
            }
            var result = await pending.ConfigureAwait(false);
            outcome = result.State switch
            {
                TransferSessionState.Completed => new(HandoffSendStage.Completed),
                TransferSessionState.Cancelled => new(HandoffSendStage.Cancelled),
                TransferSessionState.Rejected => new(HandoffSendStage.Failed, HandoffSendFailureReason.Rejected),
                _ when result.ErrorCategory == "approval-timeout" =>
                    new(HandoffSendStage.Failed, HandoffSendFailureReason.ApprovalTimeout),
                _ => new(HandoffSendStage.Failed, HandoffSendFailureReason.Unknown),
            };
        }
        catch (OperationCanceledException)
        {
            // The service distinguishes its intentional cancellation from a
            // provider-internal timeout. Only the former reaches this branch.
            outcome = new(HandoffSendStage.Cancelled, LocallyCancelled: true);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Tracked device handoff {OperationId} failed.", entry.Id);
            outcome = new(HandoffSendStage.Failed, FailureReason(exception));
        }

        bool settled;
        lock (_gate)
        {
            if (!IsRegistered(entry) || entry.Outcome is not null) return;
            entry.Outcome = outcome;
            if (outcome.LocallyCancelled) entry.Stage = HandoffSendStage.Cancelling;
            entry.Revision = ++_revision;
            settled = TrySettle(entry);
        }
        if (settled) _ = entry.DisposeCancellationAsync(logger);
        PublishChanged();
    }

    private void OnOwnerRetired(Entry entry)
    {
        bool settled;
        lock (_gate)
        {
            if (!IsRegistered(entry) || entry.OwnerRetired) return;
            entry.OwnerRetired = true;
            entry.Revision = ++_revision;
            settled = TrySettle(entry);
        }
        if (settled) _ = entry.DisposeCancellationAsync(logger);
        PublishChanged();
    }

    private void Report(Entry entry, TransferProgress progress)
    {
        lock (_gate)
        {
            if (!IsRegistered(entry) || entry.OwnerRetired || entry.Outcome is not null || entry.CancelRequested)
                return;
            var stage = progress.Stage switch
            {
                TransferProgressStage.Preparing => HandoffSendStage.Preparing,
                TransferProgressStage.WaitingForApproval => HandoffSendStage.WaitingForApproval,
                TransferProgressStage.Sending => HandoffSendStage.Sending,
                TransferProgressStage.Finalizing => HandoffSendStage.Finalizing,
                _ => entry.Stage,
            };
            if ((int)stage < (int)entry.Stage) return;
            entry.Stage = stage;
            entry.MayHaveReachedPeer |= (int)stage >= (int)HandoffSendStage.WaitingForApproval;
            if (stage != HandoffSendStage.Preparing)
            {
                entry.TotalBytes = Math.Max(entry.TotalBytes ?? 0, Math.Max(0, progress.TotalBytes));
                entry.TransferredBytes = Math.Max(entry.TransferredBytes,
                    Math.Clamp(progress.TransferredBytes, 0, entry.TotalBytes.Value));
            }
            entry.Revision = ++_revision;
            // Finalizing becomes authoritative here, before Report returns to
            // the client and before its commit request. UI dispatch is only a view.
        }
        PublishChanged();
    }

    private bool IsRegistered(Entry entry) =>
        _entries.TryGetValue(entry.Id, out var current) && ReferenceEquals(current, entry);

    private bool TrySettle(Entry entry)
    {
        if (entry.IsSettled || !entry.OwnerRetired || entry.Outcome is not { } outcome) return false;
        entry.IsSettled = true;
        entry.Stage = outcome.LocallyCancelled && entry.MayHaveReachedPeer
            ? HandoffSendStage.CancelledOutcomeUnknown : outcome.Stage;
        entry.FailureReason = outcome.FailureReason;
        _completed.Enqueue(entry.Id);
        while (_completed.Count > MaximumCompleted) _entries.Remove(_completed.Dequeue());
        return true;
    }

    private static bool CanCancel(Entry entry) => !entry.IsSettled && !entry.OwnerRetired &&
        entry.Outcome is null && !entry.CancelRequested && entry.Stage != HandoffSendStage.Finalizing;

    private static HandoffSendSnapshot Snapshot(Entry entry) => new(
        entry.Id, entry.Revision, entry.DisplayName, entry.PeerDisplayName,
        entry.Stage, entry.TransferredBytes, entry.TotalBytes, entry.FailureReason,
        CanCancel(entry), entry.IsSettled, entry.MayHaveReachedPeer);

    private static HandoffSendFailureReason FailureReason(Exception exception) => exception switch
    {
        HandoffSendUnavailableException { Reason: HandoffSendUnavailableReason.Disabled } => HandoffSendFailureReason.Disabled,
        HandoffSendUnavailableException { Reason: HandoffSendUnavailableReason.Busy } => HandoffSendFailureReason.Busy,
        TransferPreparationLimitException => HandoffSendFailureReason.PreparationLimit,
        FileNotFoundException or DirectoryNotFoundException => HandoffSendFailureReason.Source,
        HttpRequestException => HandoffSendFailureReason.Network,
        _ => HandoffSendFailureReason.Unknown,
    };

    private void PublishChanged()
    {
        var handlers = Changed;
        if (handlers is null) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            try { handler(this, EventArgs.Empty); }
            catch (Exception exception) { logger.LogWarning(exception, "Device handoff tracking observer failed."); }
        }
    }

    private sealed class SendProgress(DeviceHandoffSendTracker tracker, Entry entry) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => tracker.Report(entry, value);
    }

    private sealed record Outcome(HandoffSendStage Stage,
        HandoffSendFailureReason FailureReason = HandoffSendFailureReason.None,
        bool LocallyCancelled = false);

    private sealed class Entry(string displayName, string peerDisplayName, long createdOrder)
    {
        private readonly object _cancellationGate = new();
        private readonly CancellationTokenSource _cancellation = new();
        private Task? _cancelTask;
        private bool _cancellationDisposed;

        internal Guid Id { get; } = Guid.NewGuid();
        internal string DisplayName { get; } = displayName;
        internal string PeerDisplayName { get; } = peerDisplayName;
        internal long CreatedOrder { get; } = createdOrder;
        internal long Revision { get; set; } = createdOrder;
        internal HandoffSendStage Stage { get; set; } = HandoffSendStage.Preparing;
        internal long TransferredBytes { get; set; }
        internal long? TotalBytes { get; set; }
        internal HandoffSendFailureReason FailureReason { get; set; }
        internal bool CancelRequested { get; set; }
        internal bool OwnerRetired { get; set; }
        internal bool IsSettled { get; set; }
        internal bool MayHaveReachedPeer { get; set; }
        internal Outcome? Outcome { get; set; }
        internal CancellationToken Token => _cancellation.Token;

        internal void RequestCancellation()
        {
            lock (_cancellationGate)
            {
                if (_cancellationDisposed || _cancelTask is not null) return;
                _cancelTask = _cancellation.CancelAsync();
            }
        }

        internal async Task DisposeCancellationAsync(ILogger logger)
        {
            Task cancellation;
            lock (_cancellationGate)
            {
                if (_cancellationDisposed) return;
                _cancellationDisposed = true;
                cancellation = _cancelTask ?? Task.CompletedTask;
            }
            try { await cancellation.ConfigureAwait(false); }
            catch (Exception exception) { logger.LogWarning(exception, "Tracked handoff cancellation failed."); }
            finally { _cancellation.Dispose(); }
        }
    }
}
