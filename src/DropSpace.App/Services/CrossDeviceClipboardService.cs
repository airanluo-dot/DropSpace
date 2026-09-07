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
    ILogger<CrossDeviceClipboardService> logger) : IAsyncDisposable
{
    private readonly ClipboardLoopGuard _loopGuard = new();
    private readonly Dictionary<Guid, ClipboardPeerChannel> _peers = [];
    private readonly object _gate = new();
    private DeviceIdentity? _identity;
    private ClipboardPropagationQueue? _propagation;
    private AppSettings _settings = new();
    private long _originSequence;
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
            if (_initialized) { _settings = settings; return; }
            try
            {
                var identity = await identities.GetOrCreateAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                _identity = identity;
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

    public void ConfigurePeer(PeerDevice peer, Uri endpoint, ClipboardSyncMode? mode = null)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (peer.Platform != DevicePlatform.Windows) throw new PlatformNotSupportedException("Cross-device clipboard v1 supports Windows peers only.");
        lock (_gate) _peers[peer.Id] = new ClipboardPeerChannel(peer, endpoint, mode ?? _settings.DefaultClipboardSyncMode);
    }

    public void RemovePeer(Guid peerId)
    {
        lock (_gate) _peers.Remove(peerId);
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
        return await client.SendClipboardAsync(peer, endpoint, envelope, cancellationToken).ConfigureAwait(false);
    }

    private void OnItemCaptured(object? sender, DropItem item)
    {
        if (!IsEnabled || capture.IsPaused) return;
        _propagation?.TryEnqueue(item);
    }

    private async Task PropagateAsync(DropItem item, DeviceIdentity identity, CancellationToken cancellationToken)
    {
        if (!IsEnabled || capture.IsPaused) return;
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
            await capture.ImportRemoteAsync(envelope, cancellationToken).ConfigureAwait(false);
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
