using DropSpace.Core.Models;
using DropSpace.Core.Transfer;
using DropSpace.Infrastructure.Network;
using Microsoft.Extensions.Logging;
using System.Net.Sockets;

namespace DropSpace.App.Services;

public sealed class DeviceHandoffService(
    DeviceIdentityStore identities,
    DeviceSecretStore secrets,
    DropLinkHost host,
    DropLinkClient client,
    TransferRepository transfers,
    WindowsDnsSdDiscoveryService discovery,
    FirewallCapabilityService firewall,
    ILogger<DeviceHandoffService> logger) : IAsyncDisposable
{
    private bool _initialized;
    private bool _disposed;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private IAsyncDisposable? _registration;
    private string? _unavailableReason;

    public bool IsEnabled { get; private set; }

    public Uri? Endpoint => host.Endpoint;

    public FirewallCapability? FirewallStatus { get; private set; }

    public string? UnavailableReason => _unavailableReason;

    public Task InitializeAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
        UpdateSettingsAsync(settings, cancellationToken);

    public Task<IReadOnlyList<DeviceDescriptor>> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        discovery.BrowseAsync(timeout, cancellationToken);

    public async Task UpdateSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!settings.EnableDeviceHandoff)
            {
                await DisableCoreAsync().ConfigureAwait(false);
                _unavailableReason = null;
                return;
            }
            if (_initialized && host.IsRunning && _registration is not null && discovery.IsRegistered) return;
            await DisableCoreAsync().ConfigureAwait(false);
            try
            {
            var endpoint = await host.StartAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            FirewallStatus = firewall.CheckInboundCapability(endpoint.Port);
            var identity = await identities.GetOrCreateAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var descriptor = new DeviceDescriptor(
                DropLinkProtocolVersion.V1,
                identity.DeviceId,
                identity.DisplayName,
                identity.Platform,
                PeerCapability.HandoffFiles | PeerCapability.HandoffFolders | PeerCapability.HandoffText |
                PeerCapability.HandoffUrl | PeerCapability.ClipboardText | PeerCapability.ClipboardUrl |
                PeerCapability.ClipboardImage | PeerCapability.NearbyBrowserShare,
                identity.Fingerprint,
                endpoint);
                _registration = await discovery.RegisterAsync(descriptor, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                _unavailableReason = null;
                _initialized = true;
                IsEnabled = true;
            }
            catch (Exception exception)
            {
                _unavailableReason = exception.GetType().Name;
                try { await DisableCoreAsync().ConfigureAwait(false); }
                catch (Exception cleanup) { logger.LogError("Handoff rollback failed: {Category}.", cleanup.GetType().Name); }
                throw;
            }
        }
        finally { _lifecycleGate.Release(); }
    }

    private async Task DisableCoreAsync()
    {
        IsEnabled = false;
        _initialized = false;
        try
        {
            try { await discovery.DisposeAsync().ConfigureAwait(false); }
            finally { _registration = null; }
        }
        finally
        {
            await host.StopAsync().ConfigureAwait(false);
            FirewallStatus = null;
        }
    }

    public async Task<PeerDevice> PairAsync(
        DeviceDescriptor descriptor,
        Func<int, CancellationToken, Task<bool>>? confirmSas = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.Platform != DevicePlatform.Windows) throw new PlatformNotSupportedException("DropLink v1 supports Windows peers only.");
        return await client.PairAsync(descriptor.Endpoint, descriptor.IdentityFingerprint,
            PeerCapability.HandoffFiles | PeerCapability.HandoffFolders | PeerCapability.HandoffText |
            PeerCapability.HandoffUrl | PeerCapability.ClipboardText | PeerCapability.ClipboardUrl |
            PeerCapability.ClipboardImage, confirmSas, cancellationToken).ConfigureAwait(false);
    }

    public Task<TransferCompleteResponse> SendFilesAsync(
        PeerDevice peer,
        Uri endpoint,
        IReadOnlyList<string> sourcePaths,
        IProgress<TransferProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        client.SendFilesAsync(peer, endpoint, sourcePaths, progress, cancellationToken: cancellationToken);

    public Task<HandoffMessageResponse> SendTextOrUrlAsync(
        PeerDevice peer,
        Uri endpoint,
        HandoffMessageKind kind,
        string payload,
        string? displayLabel = null,
        CancellationToken cancellationToken = default) =>
        client.SendHandoffAsync(peer, endpoint, kind, payload, displayLabel, cancellationToken);

    public Task<TransferStatusResponse> ApproveTransferAsync(
        PeerDevice peer,
        Uri endpoint,
        Guid sessionId,
        bool accepted,
        CancellationToken cancellationToken = default) =>
        client.SetTransferApprovalAsync(peer, endpoint, sessionId, accepted, cancellationToken);

    public Task<TransferStatusResponse> CancelTransferAsync(
        PeerDevice peer,
        Uri endpoint,
        Guid sessionId,
        CancellationToken cancellationToken = default) =>
        client.CancelTransferAsync(peer, endpoint, sessionId, cancellationToken);

    public Task<bool> ApproveIncomingTransferAsync(
        Guid sessionId,
        bool accepted,
        CancellationToken cancellationToken = default) =>
        host.ApproveIncomingTransferAsync(sessionId, accepted, cancellationToken);

    public Task<IReadOnlyList<PeerDevice>> GetPeersAsync(CancellationToken cancellationToken = default) =>
        transfers.GetPeersAsync(cancellationToken);

    public async Task UnpairAsync(Guid peerId, CancellationToken cancellationToken = default)
    {
        if (peerId == Guid.Empty) throw new ArgumentOutOfRangeException(nameof(peerId));
        await secrets.DeleteAsync(peerId, cancellationToken).ConfigureAwait(false);
        await transfers.DeletePeerAsync(peerId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try { _disposed = true; await DisableCoreAsync().ConfigureAwait(false); }
        finally { _lifecycleGate.Release(); }
    }
}
