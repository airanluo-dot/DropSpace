using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json;
using DropSpace.Core.Transfer;
using DropSpace.Infrastructure.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DropSpace.Infrastructure.Network;

public sealed record IncomingTransferOffer(
    Guid SessionId,
    Guid PeerId,
    TransferManifest Manifest);

public sealed class DropLinkHost(
    AppStoragePaths paths,
    DeviceIdentityStore identities,
    DeviceSecretStore secrets,
    DropLinkPairingService pairing,
    TransferRepository transfers,
    ILogger<DropLinkHost> logger) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, ReceiveTransfer> _sessions = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _usedNonces = new(StringComparer.Ordinal);
    private WebApplication? _app;
    private DeviceIdentity? _identity;
    private Uri? _endpoint;
    private int _disposed;

    public bool IsRunning => _app is not null;

    public Uri? Endpoint => _endpoint;

    public event Func<ClipboardEnvelope, CancellationToken, Task>? ClipboardReceived;

    public event Func<IncomingTransferOffer, Task>? TransferOffered;

    public async Task<Uri> StartAsync(int port = 0, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_endpoint is not null) return _endpoint;
        var identity = _identity = await identities.GetOrCreateAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(DropLinkHost).Assembly.GetName().Name ?? "DropSpace",
            EnvironmentName = "Production",
        });
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = TransferLimits.DefaultChunkBytes + 64 * 1024;
            var bindAddress = TryGetPrivateAddress() is { } privateAddress
                ? IPAddress.Parse(privateAddress)
                : IPAddress.Loopback;
            options.Listen(bindAddress, port, listen => listen.UseHttps(identity.Certificate));
        });
        builder.Logging.ClearProviders();
        var app = builder.Build();
        MapRoutes(app);
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        _app = app;
        var address = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(address) || !Uri.TryCreate(address, UriKind.Absolute, out var uri))
        {
            await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await app.DisposeAsync().ConfigureAwait(false);
            _app = null;
            throw new InvalidOperationException("DropLink host did not expose a bound HTTPS endpoint.");
        }

        _endpoint = TryGetPrivateAddress() is { } address
            ? new Uri(string.Concat("https://", address, ":", uri.Port, "/"))
            : uri;
        logger.LogInformation("DropLink host started on port {Port} with protocol {Protocol}.", uri.Port, DropLinkProtocolVersion.V1);
        return _endpoint!;
    }

    private void MapRoutes(WebApplication app)
    {
        app.MapGet("/v1/device", () => Results.Json(new DeviceDescriptor(
            DropLinkProtocolVersion.V1,
            _identity!.DeviceId,
            _identity.DisplayName,
            _identity.Platform,
            PeerCapability.HandoffFiles | PeerCapability.HandoffFolders | PeerCapability.HandoffText |
            PeerCapability.HandoffUrl | PeerCapability.ClipboardText | PeerCapability.ClipboardUrl |
            PeerCapability.ClipboardImage | PeerCapability.NearbyBrowserShare,
            _identity.Fingerprint,
            _endpoint ?? throw new InvalidOperationException("The DropLink host has not started."))));

        app.MapPost("/v1/pairing/hello", async (PairingHello hello, CancellationToken cancellationToken) =>
        {
            try
            {
                var offer = await pairing.AcceptHelloAsync(hello, PeerCapability.HandoffFiles | PeerCapability.HandoffFolders |
                    PeerCapability.HandoffText | PeerCapability.HandoffUrl | PeerCapability.ClipboardText |
                    PeerCapability.ClipboardUrl | PeerCapability.ClipboardImage, cancellationToken).ConfigureAwait(false);
                _pendingPeers[offer.SessionId] = new PendingPeer(hello.DeviceId, hello.DisplayName, hello.Platform, hello.IdentityFingerprint, hello.Capabilities);
                return Results.Json(offer);
            }
            catch (Exception exception) when (exception is InvalidDataException or CryptographicException or InvalidOperationException or FormatException)
            {
                return Results.BadRequest(new { error = "pairing-invalid" });
            }
        });

        app.MapPost("/v1/pairing/confirm", async (PairingConfirmationRequest request, CancellationToken cancellationToken) =>
        {
            try
            {
                await pairing.ConfirmAsync(request.SessionId, request.Sas, request.Confirmed, cancellationToken).ConfigureAwait(false);
                if (!_pendingPeers.TryRemove(request.SessionId, out var pending)) return Results.BadRequest(new { error = "pairing-state-missing" });
                var peer = new PeerDevice(
                    pending.DeviceId,
                    pending.DisplayName,
                    pending.Platform,
                    pending.IdentityFingerprint,
                    pending.Capabilities,
                    PeerTrustState.Trusted,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow);
                await transfers.UpsertPeerAsync(peer, pending.DeviceId.ToString("N"), cancellationToken).ConfigureAwait(false);
                return Results.Json(new PairingConfirmationResponse(true, peer.Id));
            }
            catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or UnauthorizedAccessException)
            {
                return Results.BadRequest(new { error = "pairing-rejected" });
            }
        });

        app.MapPost("/v1/clipboard", async (HttpContext context, ClipboardSyncRequest request, CancellationToken cancellationToken) =>
        {
            if (!await AuthorizeAsync(context, cancellationToken, request.PeerId).ConfigureAwait(false)) return Results.Unauthorized();
            try
            {
                ClipboardEnvelopePolicy.Validate(request.Envelope);
                if (request.PeerId != request.Envelope.OriginDeviceId)
                {
                    return Results.BadRequest(new ClipboardSyncResponse(false, "origin-device-mismatch"));
                }

                var handlers = ClipboardReceived?.GetInvocationList();
                if (handlers is null || handlers.Length == 0)
                {
                    return Results.Json(new ClipboardSyncResponse(false, "clipboard-receiver-unavailable"));
                }

                foreach (var handler in handlers.Cast<Func<ClipboardEnvelope, CancellationToken, Task>>())
                {
                    await handler(request.Envelope, cancellationToken).ConfigureAwait(false);
                }

                return Results.Json(new ClipboardSyncResponse(true, null));
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Clipboard envelope rejected with category {ErrorCategory}.", exception.GetType().Name);
                return Results.BadRequest(new ClipboardSyncResponse(false, "clipboard-invalid"));
            }
        });

        app.MapPost("/v1/transfers/offers", async (HttpContext context, TransferOfferRequest request, CancellationToken cancellationToken) =>
        {
            if (!Guid.TryParse(context.Request.Headers["X-DropLink-Device"].ToString(), out var authenticatedPeer) || request.PeerId != authenticatedPeer ||
                !await AuthorizeAsync(context, cancellationToken, request.PeerId).ConfigureAwait(false)) return Results.Unauthorized();
            try
            {
                TransferManifestPolicy.Validate(request.Manifest);
                var session = new TransferSession(request.Manifest.SessionId, TransferDirection.Receive, TransferMode.Handoff, request.PeerId,
                    TransferSessionState.AwaitingApproval, DateTimeOffset.UtcNow, null, request.Manifest.Items.Count, request.Manifest.TotalBytes, 0, null);
                var staging = Path.Combine(paths.Staging, "transfers", request.Manifest.SessionId.ToString("N"));
                Directory.CreateDirectory(staging);
                var receive = new ReceiveTransfer(session, request.Manifest, staging, GetReceiveRoot(), new ConcurrentDictionary<Guid, ConcurrentDictionary<int, long>>());
                if (!_sessions.TryAdd(session.Id, receive)) return Results.Conflict(new { error = "session-exists" });
                await transfers.CreateSessionAsync(session, cancellationToken).ConfigureAwait(false);
                _ = NotifyTransferOfferedAsync(new IncomingTransferOffer(session.Id, request.PeerId, request.Manifest));
                return Results.Json(new TransferOfferResponse(session.Id, session.State, null));
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("DropLink offer rejected with category {ErrorCategory}.", exception.GetType().Name);
                return Results.BadRequest(new { error = "offer-invalid" });
            }
        });

        app.MapPost("/v1/transfers/{sessionId:guid}/accept", async (HttpContext context, Guid sessionId, TransferAcceptRequest request, CancellationToken cancellationToken) =>
        {
            if (!_sessions.TryGetValue(sessionId, out var receive)) return Results.NotFound();
            if (!await AuthorizeAsync(context, cancellationToken, receive.Session.PeerId).ConfigureAwait(false)) return Results.Unauthorized();
            receive.Session = receive.Session with
            {
                State = request.Accepted ? TransferSessionState.Accepted : TransferSessionState.Rejected,
                ErrorCategory = request.Accepted ? null : "rejected",
            };
            await transfers.UpdateSessionAsync(receive.Session, cancellationToken).ConfigureAwait(false);
            return Results.Json(new TransferStatusResponse(sessionId, receive.Session.State, receive.Session.TransferredBytes, SnapshotChunks(receive), receive.CompletedPaths.ToArray(), receive.Session.ErrorCategory));
        });

        app.MapGet("/v1/transfers/{sessionId:guid}/status", async (HttpContext context, Guid sessionId, CancellationToken cancellationToken) =>
        {
            if (!_sessions.TryGetValue(sessionId, out var receive)) return Results.NotFound();
            if (!await AuthorizeAsync(context, cancellationToken, receive.Session.PeerId).ConfigureAwait(false)) return Results.Unauthorized();
            return Results.Json(new TransferStatusResponse(sessionId, receive.Session.State, receive.Session.TransferredBytes, SnapshotChunks(receive), receive.CompletedPaths.ToArray(), receive.Session.ErrorCategory));
        });

        app.MapPost("/v1/transfers/{sessionId:guid}/cancel", async (HttpContext context, Guid sessionId, CancellationToken cancellationToken) =>
        {
            if (!_sessions.TryGetValue(sessionId, out var receive)) return Results.NotFound();
            if (!await AuthorizeAsync(context, cancellationToken, receive.Session.PeerId).ConfigureAwait(false)) return Results.Unauthorized();
            if (receive.Session.State is TransferSessionState.Completed or TransferSessionState.Rejected or TransferSessionState.Cancelled or TransferSessionState.Failed)
            {
                return Results.Json(new TransferStatusResponse(sessionId, receive.Session.State, receive.Session.TransferredBytes, SnapshotChunks(receive), receive.CompletedPaths.ToArray(), receive.Session.ErrorCategory));
            }

            receive.Session = receive.Session with { State = TransferSessionState.Cancelled, ErrorCategory = "cancelled" };
            await transfers.UpdateSessionAsync(receive.Session, cancellationToken).ConfigureAwait(false);
            return Results.Json(new TransferStatusResponse(sessionId, receive.Session.State, receive.Session.TransferredBytes, SnapshotChunks(receive), receive.CompletedPaths.ToArray(), receive.Session.ErrorCategory));
        });

        app.MapPut("/v1/transfers/{sessionId:guid}/items/{itemId:guid}/chunks/{index:int}", async (HttpContext context, Guid sessionId, Guid itemId, int index, CancellationToken cancellationToken) =>
        {
            if (!_sessions.TryGetValue(sessionId, out var receive)) return Results.NotFound();
            if (!await AuthorizeAsync(context, cancellationToken, receive.Session.PeerId).ConfigureAwait(false)) return Results.Unauthorized();
            if (receive.Session.State != TransferSessionState.Accepted && receive.Session.State != TransferSessionState.Transferring)
            {
                return Results.Conflict(new { error = "transfer-not-accepted" });
            }
            var item = receive.Manifest.Items.FirstOrDefault(candidate => candidate.Id == itemId);
            if (item is null || index < 0 || item.ChunkCount is not null && index >= item.ChunkCount.Value) return Results.BadRequest(new { error = "chunk-invalid" });
            var contentLength = context.Request.ContentLength;
            if (contentLength is null || contentLength < 0 || contentLength > TransferLimits.DefaultChunkBytes + 64 * 1024)
            {
                return Results.BadRequest(new { error = "chunk-length-invalid" });
            }

            var partPath = Path.Combine(receive.StagingRoot, string.Concat(itemId.ToString("N"), ".", index, ".part"));
            await using (var output = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81_920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await context.Request.Body.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            var length = new FileInfo(partPath).Length;
            if (length != contentLength.Value) return Results.BadRequest(new { error = "chunk-length-mismatch" });
            var hash = await HashFileAsync(partPath, cancellationToken).ConfigureAwait(false);
            var expectedHash = context.Request.Headers["X-DropLink-Chunk-SHA256"].ToString();
            var chunkMatches = false;
            try
            {
                chunkMatches = !string.IsNullOrWhiteSpace(expectedHash) && CryptographicOperations.FixedTimeEquals(Convert.FromHexString(hash), Convert.FromHexString(expectedHash));
            }
            catch (FormatException) { }
            if (!chunkMatches)
            {
                TryDelete(partPath);
                return Results.BadRequest(new { error = "chunk-integrity" });
            }

            var itemChunks = receive.Chunks.GetOrAdd(itemId, _ => new ConcurrentDictionary<int, long>());
            if (itemChunks.TryAdd(index, length))
            {
                receive.Session = receive.Session with { State = TransferSessionState.Transferring, TransferredBytes = receive.Session.TransferredBytes + length };
                await transfers.UpdateSessionAsync(receive.Session, cancellationToken).ConfigureAwait(false);
            }
            return Results.Ok(new { accepted = true, hash });
        });

        app.MapPost("/v1/transfers/{sessionId:guid}/complete", async (HttpContext context, Guid sessionId, CancellationToken cancellationToken) =>
        {
            if (!_sessions.TryGetValue(sessionId, out var receive)) return Results.NotFound();
            if (!await AuthorizeAsync(context, cancellationToken, receive.Session.PeerId).ConfigureAwait(false)) return Results.Unauthorized();
            try
            {
                receive.Session = receive.Session with { State = TransferSessionState.Verifying };
                await transfers.UpdateSessionAsync(receive.Session, cancellationToken).ConfigureAwait(false);
                foreach (var item in receive.Manifest.Items)
                {
                    await CommitItemAsync(receive, item, cancellationToken).ConfigureAwait(false);
                }

                receive.Session = receive.Session with { State = TransferSessionState.Completed, CompletedAtUtc = DateTimeOffset.UtcNow };
                await transfers.UpdateSessionAsync(receive.Session, cancellationToken).ConfigureAwait(false);
                return Results.Json(new TransferCompleteResponse(sessionId, receive.Session.State, receive.CompletedPaths.ToArray(), null));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                receive.Session = receive.Session with { State = TransferSessionState.Failed, ErrorCategory = exception.GetType().Name };
                await transfers.UpdateSessionAsync(receive.Session, CancellationToken.None).ConfigureAwait(false);
                return Results.BadRequest(new TransferCompleteResponse(sessionId, receive.Session.State, receive.CompletedPaths.ToArray(), "integrity-or-commit-failed"));
            }
        });
    }

    public async Task<bool> ApproveIncomingTransferAsync(
        Guid sessionId,
        bool accepted,
        CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var receive)) return false;
        if (receive.Session.State != TransferSessionState.AwaitingApproval) return false;
        receive.Session = receive.Session with
        {
            State = accepted ? TransferSessionState.Accepted : TransferSessionState.Rejected,
            ErrorCategory = accepted ? null : "rejected",
        };
        await transfers.UpdateSessionAsync(receive.Session, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task NotifyTransferOfferedAsync(IncomingTransferOffer offer)
    {
        var handlers = TransferOffered?.GetInvocationList();
        if (handlers is null) return;
        foreach (var handler in handlers.Cast<Func<IncomingTransferOffer, Task>>())
        {
            try
            {
                await handler(offer).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Incoming DropLink offer notification failed for session {SessionId}.", offer.SessionId);
            }
        }
    }

    private async Task<bool> AuthorizeAsync(HttpContext context, CancellationToken cancellationToken, Guid? expectedPeerId = null)
    {
        var deviceHeader = context.Request.Headers["X-DropLink-Device"].ToString();
        var nonce = context.Request.Headers["X-DropLink-Nonce"].ToString();
        var auth = context.Request.Headers["X-DropLink-Auth"].ToString();
        if (!Guid.TryParse(deviceHeader, out var peerId) || expectedPeerId is not null && expectedPeerId != peerId || string.IsNullOrWhiteSpace(nonce) || string.IsNullOrWhiteSpace(auth)) return false;
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _usedNonces.Where(entry => now - entry.Value > TimeSpan.FromMinutes(10))) _usedNonces.TryRemove(entry.Key, out _);
        if (!_usedNonces.TryAdd(string.Concat(peerId.ToString("N"), ":", nonce), now)) return false;
        var secret = await secrets.GetAsync(peerId, cancellationToken).ConfigureAwait(false);
        if (secret is null) return false;
        var bodyHash = context.Request.Headers["X-DropLink-Body-SHA256"].ToString();
        if (string.IsNullOrWhiteSpace(bodyHash)) bodyHash = Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant();
        var expected = DropLinkPairingService.ComputeAuth(secret, context.Request.Method, context.Request.Path.ToString(), nonce, bodyHash);
        var valid = DropLinkPairingService.FixedTimeEquals(expected, auth);
        if (!valid) _usedNonces.TryRemove(string.Concat(peerId.ToString("N"), ":", nonce), out _);
        return valid;
    }

    private string GetReceiveRoot()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var root = Path.Combine(profile, "Downloads", "DropSpace");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string? TryGetPrivateAddress() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(network => network.OperationalStatus == OperationalStatus.Up && network.NetworkInterfaceType is not NetworkInterfaceType.Loopback)
        .SelectMany(network => network.GetIPProperties().UnicastAddresses)
        .Select(address => address.Address)
        .Where(address => address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        .Select(address => new { Address = address, Bytes = address.GetAddressBytes() })
        .Where(value => value.Bytes.Length == 4 && (value.Bytes[0] == 10 || value.Bytes[0] == 192 && value.Bytes[1] == 168 || value.Bytes[0] == 172 && value.Bytes[1] is >= 16 and <= 31))
        .Select(value => value.Address.ToString())
        .FirstOrDefault();

    private static IReadOnlyDictionary<Guid, IReadOnlyList<int>> SnapshotChunks(ReceiveTransfer receive) =>
        receive.Chunks.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<int>)pair.Value.Keys.Order().ToArray());

    private static async Task CommitItemAsync(ReceiveTransfer receive, TransferItemManifest item, CancellationToken cancellationToken)
    {
        var chunks = receive.Chunks.GetValueOrDefault(item.Id);
        if (item.ChunkCount is null || (chunks?.Count ?? 0) != item.ChunkCount.Value)
        {
            throw new InvalidDataException("A transfer item is missing chunks.");
        }

        var relative = TransferManifestPolicy.NormalizeRelativePath(item.RelativePath);
        var destination = Path.GetFullPath(Path.Combine(receive.DestinationRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(receive.DestinationRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The transfer destination escaped its root.");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = string.Concat(destination, ".", receive.Session.Id.ToString("N"), ".tmp");
        await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81_920, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            for (var index = 0; index < item.ChunkCount.Value; index++)
            {
                var part = Path.Combine(receive.StagingRoot, string.Concat(item.Id.ToString("N"), ".", index, ".part"));
                await using var input = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        var hash = await HashFileAsync(temporary, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(hash, item.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(temporary);
            throw new InvalidDataException("The completed transfer hash did not match the manifest.");
        }
        File.Move(temporary, destination, overwrite: false);
        receive.CompletedPaths.Add(relative);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await StopAsync().ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }
        _app = null;
        _endpoint = null;
        foreach (var session in _sessions.Values)
        {
            try { Directory.Delete(session.StagingRoot, recursive: true); } catch (IOException) { }
        }
        _sessions.Clear();
        _pendingPeers.Clear();
        _usedNonces.Clear();
    }

    private sealed class ReceiveTransfer(
        TransferSession session,
        TransferManifest manifest,
        string stagingRoot,
        string destinationRoot,
        ConcurrentDictionary<Guid, ConcurrentDictionary<int, long>> chunks)
    {
        public TransferSession Session { get; set; } = session;
        public TransferManifest Manifest { get; } = manifest;
        public string StagingRoot { get; } = stagingRoot;
        public string DestinationRoot { get; } = destinationRoot;
        public ConcurrentDictionary<Guid, ConcurrentDictionary<int, long>> Chunks { get; } = chunks;
        public List<string> CompletedPaths { get; } = [];
    }

    private readonly ConcurrentDictionary<Guid, PendingPeer> _pendingPeers = new();

    private sealed record PendingPeer(Guid DeviceId, string DisplayName, DevicePlatform Platform, string IdentityFingerprint, PeerCapability Capabilities);
}
