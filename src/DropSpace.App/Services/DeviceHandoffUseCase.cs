using DropSpace.Core.Transfer;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Network;
using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services;

/// <summary>
/// UI-facing device handoff boundary. Lifecycle ownership remains in
/// <see cref="DeviceHandoffService"/>; this class owns user-intent operations.
/// </summary>
public sealed class DeviceHandoffUseCase(DeviceHandoffService service, ILogger<DeviceHandoffUseCase> logger)
{
    private readonly DeviceHandoffSendTracker _sends = new(service.SendFilesTrackedAsync, logger);

    public event EventHandler? SendsChanged
    {
        add => _sends.Changed += value;
        remove => _sends.Changed -= value;
    }

    public Guid StartFileTransfer(PeerDevice peer, Uri endpoint, IReadOnlyList<string> sourcePaths, string displayName) =>
        _sends.Start(peer, endpoint, sourcePaths, displayName);

    public IReadOnlyList<HandoffSendSnapshot> GetSendSnapshots() => _sends.GetSnapshots();

    public bool CancelSend(Guid id) => _sends.Cancel(id);

    public void ClearCompletedSends() => _sends.ClearCompleted();

    public bool IsEnabled => service.IsEnabled;

    public FirewallCapability? FirewallStatus => service.FirewallStatus;

    public string? UnavailableReason => service.UnavailableReason;

    public Task<IReadOnlyList<DeviceDescriptor>> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        service.DiscoverAsync(timeout, cancellationToken);

    public Task<IReadOnlyList<(PeerDevice Peer, Uri Endpoint)>> DiscoverTrustedPeersAsync(
        TimeSpan timeout, CancellationToken cancellationToken = default) =>
        service.DiscoverTrustedPeersAsync(timeout, cancellationToken);

    public Task<PeerDevice> PairAsync(
        DeviceDescriptor descriptor,
        Func<int, CancellationToken, Task<bool>>? confirmSas = null,
        CancellationToken cancellationToken = default) =>
        service.PairAsync(descriptor, confirmSas, cancellationToken);

    public Task<TransferCompleteResponse> SendFilesAsync(
        PeerDevice peer,
        Uri endpoint,
        IReadOnlyList<string> sourcePaths,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        service.SendFilesAsync(peer, endpoint, sourcePaths, progress, cancellationToken);

    public Task<HandoffMessageResponse> SendTextOrUrlAsync(
        PeerDevice peer,
        Uri endpoint,
        HandoffMessageKind kind,
        string payload,
        string? displayLabel = null,
        CancellationToken cancellationToken = default) =>
        service.SendTextOrUrlAsync(peer, endpoint, kind, payload, displayLabel, cancellationToken);

    public Task<bool> ApproveIncomingTransferAsync(
        Guid sessionId,
        bool accepted,
        CancellationToken cancellationToken = default) =>
        service.ApproveIncomingTransferAsync(sessionId, accepted, cancellationToken);
}
