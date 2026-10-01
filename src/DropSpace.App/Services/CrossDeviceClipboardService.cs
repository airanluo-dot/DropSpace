using DropSpace.Core.Models;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Transfer;
using DropSpace.Infrastructure.Network;
using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services;

public sealed record ClipboardPeerChannel(PeerDevice Peer, Uri Endpoint, ClipboardSyncMode Mode);

public sealed class CrossDeviceClipboardService(
    ClipboardCaptureService capture,
    IPayloadStore payloads,
    DeviceIdentityStore identities,
    DropLinkClient client,
    DropLinkHost host,
    ISettingsService settingsService,
    DeviceHandoffService handoff,
    ILogger<CrossDeviceClipboardService> logger) : IAsyncDisposable
{
    private readonly ClipboardLoopGuard _loopGuard = new();
    private readonly Dictionary<Guid, ClipboardPeerChannel> _peers = [];
    private readonly object _gate = new();
    private DeviceIdentity? _identity;
    private ClipboardPropagationQueue? _propagation;
    private AppSettings _settings = new();
    private long _originSequence;
    private DateTimeOffset _nextPeerRefreshUtc;
    private bool _initialized;
    private bool _disposed;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    public bool IsEnabled { get; private set; }

    public IReadOnlyList<ClipboardPeerChannel> Channels
    {
        get { lock (_gate) return _peers.Values.ToArray(); }
    }

    public Task InitializeAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
        UpdateSettingsAsync(settings, cancellationToken);

    public async Task UpdateSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!settings.EnableCrossDeviceClipboard)
            {
                await DisableCoreAsync().ConfigureAwait(false);
                _settings = settings;
                return;
            }
            // Peer selection is written through its own serialized operation. A UI
            // transaction may still carry an older snapshot of that independent field.
            var persisted = await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
            settings = settings with { ClipboardPeerModes = persisted.ClipboardPeerModes };
            if (_initialized) { _settings = settings; return; }
            try
            {
                var identity = await identities.GetOrCreateAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                _identity = identity;
                var restoredPeers = settings.ClipboardPeerModes.Count == 0
                    ? [] : await handoff.DiscoverTrustedPeersAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    _peers.Clear();
                    foreach (var reachable in restoredPeers)
                        if (settings.ClipboardPeerModes.TryGetValue(reachable.Peer.Id, out var mode))
                            _peers[reachable.Peer.Id] = new ClipboardPeerChannel(reachable.Peer, reachable.Endpoint, mode);
                }
                _propagation = new ClipboardPropagationQueue(
                    (item, token) => PropagateAsync(item, identity, token),
                    exception => logger.LogWarning(exception, "Cross-device clipboard propagation failed without changing local history."));
                capture.ItemCaptured += OnItemCaptured;
                host.ClipboardReceived += OnClipboardReceivedAsync;
                _settings = settings;
                _initialized = true;
                IsEnabled = true;
            }
            catch
            {
                await DisableCoreAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally { _lifecycleGate.Release(); }
    }

    private async Task DisableCoreAsync()
    {
        IsEnabled = false;
        _initialized = false;
        capture.ItemCaptured -= OnItemCaptured;
        host.ClipboardReceived -= OnClipboardReceivedAsync;
        var propagation = Interlocked.Exchange(ref _propagation, null);
        if (propagation is not null) await propagation.DisposeAsync().ConfigureAwait(false);
        lock (_gate) _peers.Clear();
        _loopGuard.Clear();
        _identity = null;
    }

    public async Task ConfigurePeerAsync(PeerDevice peer, Uri endpoint, ClipboardSyncMode? mode = null, CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(peer);
            ArgumentNullException.ThrowIfNull(endpoint);
            if (peer.Platform != DevicePlatform.Windows) throw new PlatformNotSupportedException("Cross-device clipboard v1 supports Windows peers only.");
            var selectedMode = mode ?? _settings.DefaultClipboardSyncMode;
            if (!Enum.IsDefined(selectedMode) || peer.TrustState != PeerTrustState.Trusted)
                throw new InvalidOperationException("Clipboard sync requires a trusted peer and a valid mode.");
            _settings = await settingsService.UpdateAsync(settings =>
            {
                var modes = settings.ClipboardPeerModes.ToDictionary(entry => entry.Key, entry => entry.Value);
                modes[peer.Id] = selectedMode;
                return settings with { ClipboardPeerModes = modes };
            }, cancellationToken).ConfigureAwait(false);
            lock (_gate) _peers[peer.Id] = new ClipboardPeerChannel(peer, endpoint, selectedMode);
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task RemovePeerAsync(Guid peerId, CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _settings = await settingsService.UpdateAsync(settings => settings with
            {
                ClipboardPeerModes = settings.ClipboardPeerModes.Where(entry => entry.Key != peerId)
                    .ToDictionary(entry => entry.Key, entry => entry.Value),
            }, cancellationToken).ConfigureAwait(false);
            lock (_gate) _peers.Remove(peerId);
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task<ClipboardSyncResponse> SendManualAsync(
        PeerDevice peer,
        Uri endpoint,
        DropItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var envelope = await CreateEnvelopeAsync(item, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The selected item is not a supported clipboard payload.");
        if (!_loopGuard.TryAccept(envelope)) return new ClipboardSyncResponse(false, "duplicate-loop-guard");
        try
        {
            var result = await client.SendClipboardAsync(peer, endpoint, envelope, cancellationToken).ConfigureAwait(false);
            if (!result.Accepted)
            {
                // A rejected manual send did not create a remote durable event. Let the
                // user retry it instead of reporting a local loop-guard duplicate.
                _loopGuard.Remove(envelope);
            }

            return result;
        }
        catch
        {
            _loopGuard.Remove(envelope);
            throw;
        }
    }

    private void OnItemCaptured(object? sender, DropItem item)
    {
        if (!IsEnabled || capture.IsPaused) return;
        _propagation?.TryEnqueue(item);
    }

    private async Task PropagateAsync(DropItem item, DeviceIdentity identity, CancellationToken cancellationToken)
    {
        if (!IsEnabled || capture.IsPaused) return;
        // Discovery endpoints can change after restart or a network transition. Refresh
        // only explicitly selected peers, and keep discovery work bounded per channel.
        if (_settings.ClipboardPeerModes.Count > 0 && DateTimeOffset.UtcNow >= _nextPeerRefreshUtc)
        {
            _nextPeerRefreshUtc = DateTimeOffset.UtcNow.AddSeconds(30);
            var reachable = await handoff.DiscoverTrustedPeersAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _peers.Clear();
                foreach (var peer in reachable)
                    if (_settings.ClipboardPeerModes.TryGetValue(peer.Peer.Id, out var mode))
                        _peers[peer.Peer.Id] = new ClipboardPeerChannel(peer.Peer, peer.Endpoint, mode);
            }
        }
        var envelope = await CreateEnvelopeAsync(item, cancellationToken, identity, ClipboardEnvelopePolicy.AutomaticImageLimitBytes).ConfigureAwait(false);
        if (!IsEnabled || capture.IsPaused || envelope is null || !_loopGuard.TryAccept(envelope)) return;
        ClipboardPeerChannel[] channels;
        lock (_gate) channels = _peers.Values.ToArray();
        foreach (var channel in channels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsEnabled || capture.IsPaused) return;
            lock (_gate)
            {
                if (!_peers.TryGetValue(channel.Peer.Id, out var current) || current != channel) continue;
            }
            if (!ClipboardEnvelopePolicy.IsAllowedAutomatically(envelope, channel.Mode)) continue;
            try
            {
                var result = await client.SendClipboardAsync(channel.Peer, channel.Endpoint, envelope, cancellationToken).ConfigureAwait(false);
                if (!result.Accepted) logger.LogInformation("Clipboard sync to {PeerId} was not accepted: {ErrorCategory}.", channel.Peer.Id, result.ErrorCategory);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException)
            {
                logger.LogInformation(exception, "Clipboard sync to peer {PeerId} failed without changing local history.", channel.Peer.Id);
            }
        }
    }

    private async Task OnClipboardReceivedAsync(ClipboardEnvelope envelope, CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var identity = _identity;
            if (!IsEnabled || identity is null || envelope.OriginDeviceId == identity.DeviceId) return;
            if (capture.IsPaused) throw new ClipboardPausedException();
            ClipboardEnvelopePolicy.Validate(envelope);
            if (!_loopGuard.TryAccept(envelope)) return;
            try
            {
                // A remote import is already an origin event. Do not republish it as a new
                // local event, otherwise two peers can bounce the same clipboard payload.
                await capture.ImportRemoteAsync(
                    envelope,
                    cancellationToken,
                    publishCaptured: false).ConfigureAwait(false);
            }
            catch
            {
                // Reserve only suppresses an exact retransmission. If durable import did not
                // complete, release it so a sender retry is not mistaken for a loop.
                _loopGuard.Remove(envelope);
                throw;
            }
        }
        finally { _lifecycleGate.Release(); }
    }

    private async Task<ClipboardEnvelope?> CreateEnvelopeAsync(
        DropItem item,
        CancellationToken cancellationToken,
        DeviceIdentity? identity = null,
        long maximumImageBytes = ClipboardEnvelopePolicy.HardImageLimitBytes)
    {
        identity ??= _identity;
        if (identity is null) return null;
        var sequence = Interlocked.Increment(ref _originSequence);
        if (item.Url is { NormalizedUrl: var url }) return ClipboardEnvelopePolicy.CreateText(identity.DeviceId, sequence, url, ClipboardPayloadKind.Url);
        if (item.Text?.InlineText is { } text) return ClipboardEnvelopePolicy.CreateText(identity.DeviceId, sequence, text);
        if (item.Image is not null && item.Payload is { RelativePath: var relativePath })
        {
            await using var stream = await payloads.OpenReadAsync(relativePath, cancellationToken).ConfigureAwait(false);
            using var memory = new MemoryStream();
            if (stream.CanSeek && stream.Length > maximumImageBytes)
                throw new InvalidDataException("Clipboard image exceeds the transfer read budget.");
            var buffer = new byte[81_920];
            while (true)
            {
                var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                if (memory.Length + count > maximumImageBytes)
                    throw new InvalidDataException("Clipboard image exceeds the transfer read budget.");
                await memory.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            }
            return ClipboardEnvelopePolicy.CreateImage(identity.DeviceId, sequence, memory.GetBuffer().AsSpan(0, checked((int)memory.Length)), item.Image.MimeType);
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try { _disposed = true; await DisableCoreAsync().ConfigureAwait(false); }
        finally { _lifecycleGate.Release(); }
    }
}
