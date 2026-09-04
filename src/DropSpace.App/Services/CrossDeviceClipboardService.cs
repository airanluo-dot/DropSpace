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
                DisableCore();
                _settings = settings;
                return;
            }
            if (_initialized) { _settings = settings; return; }
            try
            {
                var identity = await identities.GetOrCreateAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                _identity = identity;
                capture.ItemCaptured += OnItemCaptured;
                host.ClipboardReceived += OnClipboardReceivedAsync;
                _settings = settings;
                _initialized = true;
                IsEnabled = true;
            }
            catch
            {
                DisableCore();
                throw;
            }
        }
        finally { _lifecycleGate.Release(); }
    }

    private void DisableCore()
    {
        IsEnabled = false;
        _initialized = false;
        capture.ItemCaptured -= OnItemCaptured;
        host.ClipboardReceived -= OnClipboardReceivedAsync;
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

    private async void OnItemCaptured(object? sender, DropItem item)
    {
        try
        {
            if (!IsEnabled || _identity is null || capture.IsPaused) return;
            var envelope = await CreateEnvelopeAsync(item, CancellationToken.None).ConfigureAwait(false);
            if (capture.IsPaused || envelope is null || !_loopGuard.TryAccept(envelope)) return;
            ClipboardPeerChannel[] channels;
            lock (_gate) channels = _peers.Values.ToArray();
            foreach (var channel in channels)
            {
                if (!ClipboardEnvelopePolicy.IsAllowedAutomatically(envelope, channel.Mode)) continue;
                try
                {
                    var result = await client.SendClipboardAsync(channel.Peer, channel.Endpoint, envelope).ConfigureAwait(false);
                    if (!result.Accepted) logger.LogInformation("Clipboard sync to {PeerId} was not accepted: {ErrorCategory}.", channel.Peer.Id, result.ErrorCategory);
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException)
                {
                    logger.LogInformation(exception, "Clipboard sync to peer {PeerId} failed without changing local history.", channel.Peer.Id);
                }
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Cross-device clipboard capture could not be propagated.");
        }
    }

    private async Task OnClipboardReceivedAsync(ClipboardEnvelope envelope, CancellationToken cancellationToken)
    {
        if (!IsEnabled || _identity is null || envelope.OriginDeviceId == _identity.DeviceId) return;
        if (capture.IsPaused) throw new ClipboardPausedException();
        ClipboardEnvelopePolicy.Validate(envelope);
        if (!_loopGuard.TryAccept(envelope)) return;
        await capture.ImportRemoteAsync(envelope, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ClipboardEnvelope?> CreateEnvelopeAsync(DropItem item, CancellationToken cancellationToken)
    {
        if (_identity is null) return null;
        var sequence = Interlocked.Increment(ref _originSequence);
        if (item.Url is { NormalizedUrl: var url }) return ClipboardEnvelopePolicy.CreateText(_identity.DeviceId, sequence, url, ClipboardPayloadKind.Url);
        if (item.Text?.InlineText is { } text) return ClipboardEnvelopePolicy.CreateText(_identity.DeviceId, sequence, text);
        if (item.Image is not null && item.Payload is { RelativePath: var relativePath })
        {
            await using var stream = await payloads.OpenReadAsync(relativePath, cancellationToken).ConfigureAwait(false);
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, cancellationToken).ConfigureAwait(false);
            return ClipboardEnvelopePolicy.CreateImage(_identity.DeviceId, sequence, memory.GetBuffer().AsSpan(0, checked((int)memory.Length)), item.Image.MimeType);
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try { _disposed = true; DisableCore(); }
        finally { _lifecycleGate.Release(); }
    }
}
