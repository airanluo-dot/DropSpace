using System.Runtime.Versioning;
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

public sealed record IncomingPairingOffer(
    Guid SessionId,
    PairingHello RemoteHello,
    PairingHello LocalHello,
    int Sas,
    DateTimeOffset ExpiresAtUtc);

public sealed record IncomingHandoffOffer(
    Guid SessionId,
    Guid PeerId,
    HandoffMessage Message);

[SupportedOSPlatform("windows")]
public sealed class DropLinkHost(
    AppStoragePaths paths,
    DeviceIdentityStore identities,
    DropLinkPairingService pairing,
    TransferRepository transfers,
    ILogger<DropLinkHost> logger,
    DropLinkNonceCache usedNonces) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, ReceiveTransfer> _sessions = new();
    private readonly DropLinkNonceCache _usedNonces = usedNonces;
    private readonly DropLinkReplayCache _usedHandoffSessions = new();
    private readonly object _sessionAdmissionGate = new();
    private CancellationTokenSource _sessionLifetimeCancellation = new();
    private Task? _sessionLifetimeTask;
    private readonly ConcurrentDictionary<Guid, Task> _offerNotificationTasks = new();
    private readonly object _offerNotificationGate = new();
    private CancellationTokenSource _offerNotificationCancellation = new();
    private WebApplication? _app;
    private DeviceIdentity? _identity;
    private Uri? _endpoint;
    private int _disposed;

    public bool IsRunning => _app is not null;

    public Uri? Endpoint => _endpoint;

    public event Func<ClipboardEnvelope, CancellationToken, Task>? ClipboardReceived;

    public event Func<IncomingTransferOffer, CancellationToken, Task>? TransferOffered;

    public event Func<IncomingPairingOffer, CancellationToken, Task<bool>>? PairingOffered;

    public event Func<IncomingHandoffOffer, CancellationToken, Task<bool>>? HandoffOffered;

    public async Task<Uri> StartAsync(int port = 0, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        EnsureOfferNotificationCancellation();
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
            options.Limits.MaxRequestBodySize = DropLinkProtocolPolicy.MaximumAuthenticatedBodyBytes;
            var bindAddress = TryGetPrivateAddress() is { } privateAddress
                ? IPAddress.Parse(privateAddress)
                : IPAddress.Loopback;
            options.Listen(bindAddress, port, listen => listen.UseHttps(identity.Certificate));
        });
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.UseMiddleware<DropLinkAuthenticationMiddleware>();
        MapRoutes(app);
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        _app = app;
        var serverAddress = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(serverAddress) || !Uri.TryCreate(serverAddress, UriKind.Absolute, out var uri))
        {
            await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await app.DisposeAsync().ConfigureAwait(false);
            _app = null;
            throw new InvalidOperationException("DropLink host did not expose a bound HTTPS endpoint.");
        }

        _endpoint = TryGetPrivateAddress() is { } privateAddress
            ? new Uri(string.Concat("https://", privateAddress, ":", uri.Port, "/"))
            : uri;
        StartSessionLifetimeLoop();
        logger.LogInformation("DropLink host started on port {Port} with protocol {Protocol}.", uri.Port, DropLinkProtocolVersion.V1);
        return _endpoint!;
    }

    private void MapRoutes(WebApplication app)
    {
        app.MapGet(DropLinkProtocolRoutes.Device, () => Results.Json(new DeviceDescriptor(
            DropLinkProtocolVersion.V1,
            _identity!.DeviceId,
            _identity.DisplayName,
            _identity.Platform,
            PeerCapability.HandoffFiles | PeerCapability.HandoffFolders | PeerCapability.HandoffText |
            PeerCapability.HandoffUrl | PeerCapability.ClipboardText | PeerCapability.ClipboardUrl |
            PeerCapability.ClipboardImage | PeerCapability.NearbyBrowserShare,
            _identity.Fingerprint,
            _endpoint ?? throw new InvalidOperationException("The DropLink host has not started."))));

        app.MapPost(DropLinkProtocolRoutes.PairingHello, async (HttpContext context, PairingHello hello, CancellationToken cancellationToken) =>
        {
            try
            {
                ArgumentNullException.ThrowIfNull(hello);
                var offer = await pairing.AcceptHelloAsync(
                    hello,
                    PeerCapability.HandoffFiles | PeerCapability.HandoffFolders |
                    PeerCapability.HandoffText | PeerCapability.HandoffUrl | PeerCapability.ClipboardText |
                    PeerCapability.ClipboardUrl | PeerCapability.ClipboardImage,
                    cancellationToken,
                    context.Connection.RemoteIpAddress?.ToString()).ConfigureAwait(false);
                return Results.Json(offer);
            }
            catch (PairingAdmissionException admission)
            {
                logger.LogWarning(
                    "DropLink pairing admission rejected with category {ErrorCategory}; pendingCount={PendingCount}.",
                    admission.ErrorCategory,
                    pairing.PendingCount);
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }
            catch (Exception exception) when (exception is ArgumentNullException or InvalidDataException or CryptographicException or InvalidOperationException or FormatException)
            {
                return Results.BadRequest(new { error = "pairing-invalid" });
            }
        });

        app.MapPost(DropLinkProtocolRoutes.PairingConfirm, async (PairingConfirmationRequest request, CancellationToken cancellationToken) =>
        {
            var pendingSessionId = Guid.Empty;
            var pendingSas = 0;
            try
            {
                ArgumentNullException.ThrowIfNull(request);
                pendingSessionId = request.SessionId;
                if (!pairing.TryGetPendingOffer(request.SessionId, out var pending))
                {
                    return Results.Json(new PairingConfirmationResponse(false, Guid.Empty, PairingState.Failed, "pairing-state-missing"));
                }

                pendingSas = pending.Sas;
                if (request.Decision == PairingDecision.Confirm && request.Confirmed)
                {
                    var handlers = PairingOffered?.GetInvocationList();
                    var locallyConfirmed = handlers is not null && handlers.Length > 0;
                    if (locallyConfirmed)
                    {
                        var incoming = new IncomingPairingOffer(
                            request.SessionId,
                            pending.RemoteHello,
                            pending.LocalHello,
                            pending.Sas,
                            pending.ExpiresAtUtc);
                        foreach (var handler in handlers!.Cast<Func<IncomingPairingOffer, CancellationToken, Task<bool>>>())
                        {
                            locallyConfirmed &= await handler(incoming, cancellationToken).ConfigureAwait(false);
                        }
                    }

                    request = request with
                    {
                        Confirmed = locallyConfirmed,
                        Decision = locallyConfirmed ? PairingDecision.Confirm : PairingDecision.Reject,
                    };
                }

                var result = await pairing.ConfirmAsync(
                    request.SessionId,
                    request.Sas,
                    request.Confirmed,
                    request.Decision,
                    cancellationToken).ConfigureAwait(false);
                if (!result.Trusted)
                {
                    return Results.Json(new PairingConfirmationResponse(false, result.PeerId, result.State, result.ErrorCategory));
                }

                var peer = new PeerDevice(
                    pending.RemoteHello.DeviceId,
                    pending.RemoteHello.DisplayName,
                    pending.RemoteHello.Platform,
                    pending.RemoteHello.IdentityFingerprint,
                    pending.RemoteHello.Capabilities,
                    PeerTrustState.Trusted,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow);
                await transfers.UpsertPeerAsync(peer, peer.Id.ToString("N"), cancellationToken).ConfigureAwait(false);
                return Results.Json(new PairingConfirmationResponse(true, peer.Id, PairingState.Trusted, null));
            }
            catch (Exception exception) when (exception is ArgumentNullException or CryptographicException or InvalidOperationException or TimeoutException or UnauthorizedAccessException or IOException or OperationCanceledException)
            {
                if (pendingSessionId != Guid.Empty)
                {
                    try
                    {
                        await pairing.ConfirmAsync(
                            pendingSessionId,
                            pendingSas,
                            false,
                            PairingDecision.Reject,
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception cleanupException) when (cleanupException is InvalidOperationException or IOException or UnauthorizedAccessException)
                    {
                        logger.LogDebug(cleanupException, "Pairing cleanup could not settle session {SessionId}.", pendingSessionId);
                    }
                }

                return Results.Json(new PairingConfirmationResponse(false, Guid.Empty, PairingState.Failed, "pairing-failed"));
            }
        });

        app.MapPost(DropLinkProtocolRoutes.Clipboard, async (HttpContext context, ClipboardSyncRequest request, CancellationToken cancellationToken) =>
        {
            if (request is null ||
                !await AuthorizeAsync(context, cancellationToken, request.PeerId).ConfigureAwait(false))
            {
                return Results.Unauthorized();
            }

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
            catch (ClipboardPausedException)
            {
                return Results.Json(new ClipboardSyncResponse(false, ClipboardPausedException.ErrorCategory));
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Clipboard envelope rejected with category {ErrorCategory}.", exception.GetType().Name);
                return Results.BadRequest(new ClipboardSyncResponse(false, "clipboard-invalid"));
            }
        });

        app.MapPost(DropLinkProtocolRoutes.HandoffText, async (HttpContext context, HandoffMessageRequest request, CancellationToken cancellationToken) =>
        {
            if (request is null ||
                !await AuthorizeAsync(context, cancellationToken, request.PeerId).ConfigureAwait(false))
            {
                return Results.Unauthorized();
            }

            try
            {
                if (request.Message is null || request.Message.SenderDeviceId != request.PeerId)
                {
                    return Results.BadRequest(new { error = "handoff-origin-mismatch" });
                }

                HandoffMessagePolicy.Validate(request.Message);
                var now = DateTimeOffset.UtcNow;
                if (request.Message.CreatedAtUtc < now - DropLinkProtocolPolicy.HandoffReplayRetention ||
                    request.Message.CreatedAtUtc > now + DropLinkProtocolPolicy.HandoffMaximumFutureSkew)
                {
                    return Results.Json(new HandoffMessageResponse(request.Message.SessionId, false, "expired"));
                }

                if (!_usedHandoffSessions.TryReserve(request.PeerId, request.Message.SessionId, now))
                {
                    return Results.Json(new HandoffMessageResponse(request.Message.SessionId, false, "duplicate-session"));
                }

                var handlers = HandoffOffered?.GetInvocationList();
                if (handlers is null || handlers.Length == 0)
                {
                    return Results.Json(new HandoffMessageResponse(request.Message.SessionId, false, "handoff-receiver-unavailable"));
                }

                var offer = new IncomingHandoffOffer(request.Message.SessionId, request.PeerId, request.Message);
                foreach (var handler in handlers.Cast<Func<IncomingHandoffOffer, CancellationToken, Task<bool>>>())
                {
                    if (!await handler(offer, cancellationToken).ConfigureAwait(false))
                    {
                        return Results.Json(new HandoffMessageResponse(request.Message.SessionId, false, "rejected"));
                    }
                }

                return Results.Json(new HandoffMessageResponse(request.Message.SessionId, true, null));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Explicit text/URL handoff was rejected.");
                return Results.Json(new HandoffMessageResponse(request.Message.SessionId, false, "handoff-invalid"));
            }
        });

        app.MapPost(DropLinkProtocolRoutes.TransferOffers, async (HttpContext context, TransferOfferRequest request, CancellationToken cancellationToken) =>
        {
            if (request is null ||
                !await AuthorizeAsync(context, cancellationToken, request.PeerId).ConfigureAwait(false))
            {
                return Results.Unauthorized();
            }

            try
            {
                TransferManifestPolicy.Validate(request.Manifest);
                await SweepSessionsAsync(cancellationToken).ConfigureAwait(false);

                var session = new TransferSession(
                    request.Manifest.SessionId,
                    TransferDirection.Receive,
                    TransferMode.Handoff,
                    request.PeerId,
                    TransferSessionState.AwaitingApproval,
                    DateTimeOffset.UtcNow,
                    null,
                    request.Manifest.Items.Count,
                    request.Manifest.TotalBytes,
                    0,
                    null);
                var staging = Path.Combine(paths.Staging, "transfers", request.Manifest.SessionId.ToString("N"));
                Directory.CreateDirectory(staging);
                var receive = new ReceiveTransfer(
                    session,
                    request.Manifest,
                    staging,
                    GetReceiveRoot());

                lock (_sessionAdmissionGate)
                {
                    if (_sessions.Count >= DropLinkSessionPolicy.MaximumActiveSessions)
                    {
                        receive.Dispose();
                        TryDeleteDirectory(staging);
                        return Results.StatusCode(StatusCodes.Status429TooManyRequests);
                    }

                    if (!_sessions.TryAdd(session.Id, receive))
                    {
                        receive.Dispose();
                        TryDeleteDirectory(staging);
                        return Results.Conflict(new { error = "session-exists" });
                    }
                }

                try
                {
                    await transfers.CreateSessionAsync(session, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    if (_sessions.TryRemove(session.Id, out var removed))
                    {
                        removed.Dispose();
                    }

                    TryDeleteDirectory(staging);
                    throw;
                }

                QueueTransferOfferNotification(
                    new IncomingTransferOffer(session.Id, request.PeerId, request.Manifest));
                return Results.Json(new TransferOfferResponse(session.Id, session.State, null));
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "DropLink offer rejected with category {ErrorCategory}.", exception.GetType().Name);
                return Results.BadRequest(new { error = "offer-invalid" });
            }
        });

        app.MapPost(DropLinkProtocolRoutes.TransferAcceptTemplate, async (HttpContext context, Guid sessionId, TransferAcceptRequest request, CancellationToken cancellationToken) =>
        {
            if (request is null || !_sessions.TryGetValue(sessionId, out var receive))
            {
                return request is null ? Results.BadRequest(new { error = "accept-invalid" }) : Results.NotFound();
            }

            if (!await AuthorizeAsync(context, cancellationToken, receive.PeerId).ConfigureAwait(false))
            {
                return Results.Unauthorized();
            }

            await receive.MutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (receive.Session.State != TransferSessionState.AwaitingApproval)
                {
                    return Results.Json(SnapshotUnsafe(receive));
                }

                receive.Session = receive.Session with
                {
                    State = request.Accepted ? TransferSessionState.Accepted : TransferSessionState.Rejected,
                    ErrorCategory = request.Accepted ? null : "rejected",
                };
                receive.Touch();
                await transfers.UpdateSessionAsync(receive.Session, cancellationToken).ConfigureAwait(false);
                return Results.Json(SnapshotUnsafe(receive));
            }
            finally
            {
                receive.MutationGate.Release();
            }
        });

        app.MapGet(DropLinkProtocolRoutes.TransferStatusTemplate, async (HttpContext context, Guid sessionId, CancellationToken cancellationToken) =>
        {
            if (!_sessions.TryGetValue(sessionId, out var receive))
            {
                return Results.NotFound();
            }

            if (!await AuthorizeAsync(context, cancellationToken, receive.PeerId).ConfigureAwait(false))
            {
                return Results.Unauthorized();
            }

            return Results.Json(await SnapshotAsync(receive, cancellationToken).ConfigureAwait(false));
        });

        app.MapPost(DropLinkProtocolRoutes.TransferCancelTemplate, async (HttpContext context, Guid sessionId, CancellationToken cancellationToken) =>
        {
            if (!_sessions.TryGetValue(sessionId, out var receive))
            {
                return Results.NotFound();
            }

            if (!await AuthorizeAsync(context, cancellationToken, receive.PeerId).ConfigureAwait(false))
            {
                return Results.Unauthorized();
            }

            await receive.MutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (DropLinkSessionPolicy.IsTerminal(receive.Session.State))
                {
                    return Results.Json(SnapshotUnsafe(receive));
                }

                if (receive.Session.State == TransferSessionState.Verifying)
                {
                    return Results.Conflict(new { error = "transfer-finalizing" });
                }

                receive.Session = receive.Session with
                {
                    State = TransferSessionState.Cancelled,
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                    ErrorCategory = "cancelled",
                };
                receive.Touch();
                await transfers.UpdateSessionAsync(receive.Session, cancellationToken).ConfigureAwait(false);
                return Results.Json(SnapshotUnsafe(receive));
            }
            finally
            {
                receive.MutationGate.Release();
            }
        });

        app.MapPut(DropLinkProtocolRoutes.TransferChunkTemplate, async (HttpContext context, Guid sessionId, Guid itemId, int index, CancellationToken cancellationToken) =>
        {
            if (!_sessions.TryGetValue(sessionId, out var receive))
            {
                return Results.NotFound();
            }

            if (!await AuthorizeAsync(context, cancellationToken, receive.PeerId).ConfigureAwait(false))
            {
                return Results.Unauthorized();
            }

            await receive.MutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (receive.Session.State is not (TransferSessionState.Accepted or TransferSessionState.Transferring))
                {
                    return Results.Conflict(new { error = "transfer-not-accepted" });
                }

                var item = receive.Manifest.Items.FirstOrDefault(candidate => candidate.Id == itemId);
                if (item is null ||
                    index < 0 ||
                    item.ChunkCount is null ||
                    index >= item.ChunkCount.Value)
                {
                    return Results.BadRequest(new { error = "chunk-invalid" });
                }

                var expectedLength = Math.Min(
                    TransferLimits.DefaultChunkBytes,
                    item.Size - ((long)index * TransferLimits.DefaultChunkBytes));
                if (expectedLength < 0 || context.Request.ContentLength != expectedLength)
                {
                    return Results.BadRequest(new { error = "chunk-length-invalid" });
                }

                if (receive.Chunks.TryGet(itemId, index, out var existingLength) &&
                    File.Exists(Path.Combine(receive.StagingRoot, string.Concat(itemId.ToString("N"), ".", index, ".part"))))
                {
                    return Results.Ok(new { accepted = true, duplicate = true, length = existingLength });
                }

                var partPath = Path.Combine(
                    receive.StagingRoot,
                    string.Concat(itemId.ToString("N"), ".", index, ".part"));
                string? temporaryPartPath = string.Concat(
                    partPath,
                    ".",
                    Guid.NewGuid().ToString("N"),
                    ".tmp");
                try
                {
                    await using (var output = new FileStream(
                        temporaryPartPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        81_920,
                        FileOptions.Asynchronous | FileOptions.WriteThrough))
                    {
                        await context.Request.Body.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }

                    var length = new FileInfo(temporaryPartPath).Length;
                    if (length != expectedLength)
                    {
                        return Results.BadRequest(new { error = "chunk-length-mismatch" });
                    }

                    var hash = await HashFileAsync(temporaryPartPath, cancellationToken).ConfigureAwait(false);
                    var expectedHash = context.Request.Headers[DropLinkProtocolHeaders.ChunkSha256].ToString();
                    if (!DropLinkProtocolPolicy.IsLowerHexHash(expectedHash))
                    {
                        return Results.BadRequest(new { error = "chunk-integrity" });
                    }

                    bool chunkMatches;
                    try
                    {
                        chunkMatches = CryptographicOperations.FixedTimeEquals(
                            Convert.FromHexString(hash),
                            Convert.FromHexString(expectedHash));
                    }
                    catch (FormatException)
                    {
                        chunkMatches = false;
                    }

                    if (!chunkMatches)
                    {
                        return Results.BadRequest(new { error = "chunk-integrity" });
                    }

                    File.Move(temporaryPartPath, partPath);
                    temporaryPartPath = null;

                    if (!receive.Chunks.TryAdd(itemId, index, length))
                    {
                        return Results.Ok(new { accepted = true, duplicate = true, length });
                    }

                    receive.Session = receive.Session with
                    {
                        State = TransferSessionState.Transferring,
                        TransferredBytes = checked(receive.Session.TransferredBytes + length),
                    };
                    receive.Touch();
                    await transfers.UpdateSessionAsync(receive.Session, cancellationToken).ConfigureAwait(false);
                    return Results.Ok(new { accepted = true, duplicate = false, hash, length });
                }
                finally
                {
                    if (temporaryPartPath is not null)
                    {
                        TryDelete(temporaryPartPath);
                    }
                }
            }
            finally
            {
                receive.MutationGate.Release();
            }
        });

        app.MapPost(DropLinkProtocolRoutes.TransferCompleteTemplate, async (HttpContext context, Guid sessionId, CancellationToken cancellationToken) =>
        {
            if (!_sessions.TryGetValue(sessionId, out var receive))
            {
                return Results.NotFound();
            }

            if (!await AuthorizeAsync(context, cancellationToken, receive.PeerId).ConfigureAwait(false))
            {
                return Results.Unauthorized();
            }

            try
            {
                var result = await GetOrStartFinalizationTask(receive).WaitAsync(cancellationToken).ConfigureAwait(false);
                return Results.Json(result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Results.StatusCode(499);
            }
        });
    }

    public async Task<bool> ApproveIncomingTransferAsync(
        Guid sessionId,
        bool accepted,
        CancellationToken cancellationToken = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var receive)) return false;

        await receive.MutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (receive.Session.State != TransferSessionState.AwaitingApproval) return false;
            receive.Session = receive.Session with
            {
                State = accepted ? TransferSessionState.Accepted : TransferSessionState.Rejected,
                ErrorCategory = accepted ? null : "rejected",
            };
            receive.Touch();
            await transfers.UpdateSessionAsync(receive.Session, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            receive.MutationGate.Release();
        }
    }

    private void QueueTransferOfferNotification(IncomingTransferOffer offer)
    {
        var task = RunTransferOfferNotificationAsync(offer);
        _offerNotificationTasks[offer.SessionId] = task;
        if (task.IsCompleted)
        {
            _offerNotificationTasks.TryRemove(offer.SessionId, out _);
        }
    }

    private async Task RunTransferOfferNotificationAsync(IncomingTransferOffer offer)
    {
        var cancellationToken = GetOfferNotificationToken();
        try
        {
            await NotifyTransferOfferedAsync(offer, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host shutdown or settings disablement canceled the UI approval notification.
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Incoming DropLink offer notification failed for session {SessionId}.", offer.SessionId);
        }
        finally
        {
            _offerNotificationTasks.TryRemove(offer.SessionId, out _);
        }
    }

    private async Task NotifyTransferOfferedAsync(
        IncomingTransferOffer offer,
        CancellationToken cancellationToken)
    {
        var handlers = TransferOffered?.GetInvocationList();
        if (handlers is null) return;
        foreach (var handler in handlers.Cast<Func<IncomingTransferOffer, CancellationToken, Task>>())
        {
            try
            {
                await handler(offer, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(exception, "Incoming DropLink offer notification failed for session {SessionId}.", offer.SessionId);
            }
        }
    }

    private CancellationToken GetOfferNotificationToken()
    {
        lock (_offerNotificationGate)
        {
            if (_offerNotificationCancellation.IsCancellationRequested)
            {
                _offerNotificationCancellation.Dispose();
                _offerNotificationCancellation = new CancellationTokenSource();
            }

            return _offerNotificationCancellation.Token;
        }
    }

    private void EnsureOfferNotificationCancellation()
    {
        lock (_offerNotificationGate)
        {
            if (_offerNotificationCancellation.IsCancellationRequested)
            {
                _offerNotificationCancellation.Dispose();
                _offerNotificationCancellation = new CancellationTokenSource();
            }
        }
    }

    private async Task AwaitOfferNotificationsAsync()
    {
        var tasks = _offerNotificationTasks.Values.ToArray();
        if (tasks.Length == 0) return;

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Individual UI notifications observe the host cancellation token.
        }
    }

    private static Task<bool> AuthorizeAsync(
        HttpContext context,
        CancellationToken cancellationToken,
        Guid? expectedPeerId = null)
    {
        _ = cancellationToken;
        if (!context.Items.TryGetValue(
                DropLinkAuthenticationMiddleware.AuthenticatedPeerContextKey,
                out var authenticatedPeerValue) ||
            authenticatedPeerValue is not Guid authenticatedPeer ||
            expectedPeerId is not null && expectedPeerId != authenticatedPeer)
        {
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    private static TransferStatusResponse SnapshotUnsafe(ReceiveTransfer receive) =>
        new(
            receive.Session.Id,
            receive.Session.State,
            receive.Session.TransferredBytes,
            SnapshotChunks(receive),
            receive.CompletedPaths.ToArray(),
            receive.Session.ErrorCategory);

    private static async Task<TransferStatusResponse> SnapshotAsync(
        ReceiveTransfer receive,
        CancellationToken cancellationToken)
    {
        await receive.MutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            receive.Touch();
            return SnapshotUnsafe(receive);
        }
        finally
        {
            receive.MutationGate.Release();
        }
    }

    private static TransferCompleteResponse CompleteSnapshotUnsafe(ReceiveTransfer receive) =>
        new(
            receive.Session.Id,
            receive.Session.State,
            receive.CompletedPaths.ToArray(),
            receive.Session.ErrorCategory);

    private Task<TransferCompleteResponse> GetOrStartFinalizationTask(ReceiveTransfer receive) =>
        receive.Finalization.GetOrStart(() => FinalizeTransferAsync(receive));

    private async Task<TransferCompleteResponse> FinalizeTransferAsync(ReceiveTransfer receive)
    {
        await receive.FinalizationGate.WaitAsync(receive.LifetimeCancellation.Token).ConfigureAwait(false);
        try
        {
            await receive.MutationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (DropLinkSessionPolicy.IsTerminal(receive.Session.State))
                {
                    return CompleteSnapshotUnsafe(receive);
                }

                if (receive.Session.State is not (TransferSessionState.Accepted or TransferSessionState.Transferring))
                {
                    return new TransferCompleteResponse(
                        receive.Session.Id,
                        receive.Session.State,
                        receive.CompletedPaths.ToArray(),
                        "transfer-not-accepted");
                }

                receive.Session = receive.Session with
                {
                    State = TransferSessionState.Verifying,
                    ErrorCategory = null,
                };
                receive.Touch();
                await transfers.UpdateSessionAsync(receive.Session, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                receive.MutationGate.Release();
            }

            foreach (var item in receive.Manifest.Items)
            {
                await CommitItemAsync(
                    receive,
                    item,
                    receive.LifetimeCancellation.Token).ConfigureAwait(false);
            }

            await receive.MutationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                receive.Session = receive.Session with
                {
                    State = TransferSessionState.Completed,
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                    ErrorCategory = null,
                };
                receive.Touch();
                await transfers.UpdateSessionAsync(receive.Session, CancellationToken.None).ConfigureAwait(false);
                return CompleteSnapshotUnsafe(receive);
            }
            finally
            {
                receive.MutationGate.Release();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or OperationCanceledException)
        {
            logger.LogWarning(
                exception,
                "DropLink transfer finalization failed for session {SessionId}.",
                receive.Session.Id);
            return await MarkFinalizationFailedAsync(receive, exception).ConfigureAwait(false);
        }
    }

    private async Task<TransferCompleteResponse> MarkFinalizationFailedAsync(
        ReceiveTransfer receive,
        Exception exception)
    {
        await receive.MutationGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (!DropLinkSessionPolicy.IsTerminal(receive.Session.State))
            {
                receive.Session = receive.Session with
                {
                    State = TransferSessionState.Failed,
                    CompletedAtUtc = DateTimeOffset.UtcNow,
                    ErrorCategory = exception.GetType().Name,
                };
                receive.Touch();
                try
                {
                    await transfers.UpdateSessionAsync(receive.Session, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception persistenceException) when (
                    persistenceException is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    logger.LogWarning(
                        persistenceException,
                        "DropLink failed to persist transfer failure for session {SessionId}.",
                        receive.Session.Id);
                }
            }

            return CompleteSnapshotUnsafe(receive);
        }
        finally
        {
            receive.MutationGate.Release();
        }
    }

    private void StartSessionLifetimeLoop()
    {
        if (_sessionLifetimeCancellation.IsCancellationRequested)
        {
            _sessionLifetimeCancellation.Dispose();
            _sessionLifetimeCancellation = new CancellationTokenSource();
        }

        if (_sessionLifetimeTask is null || _sessionLifetimeTask.IsCompleted)
        {
            _sessionLifetimeTask = RunSessionLifetimeLoopAsync(_sessionLifetimeCancellation.Token);
        }
    }

    private async Task RunSessionLifetimeLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(DropLinkSessionPolicy.SweepInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await SweepSessionsAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "DropLink session lifetime loop stopped unexpectedly.");
        }
    }

    private async Task SweepSessionsAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var receive in _sessions.Values.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            TransferSession? persistedFailure = null;
            var retire = false;

            await receive.MutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var state = receive.Session.State;
                if (!DropLinkSessionPolicy.IsTerminal(state) &&
                    state != TransferSessionState.Verifying &&
                    now - receive.LastActivityUtc > DropLinkSessionPolicy.ActiveSessionLifetime)
                {
                    receive.Session = receive.Session with
                    {
                        State = TransferSessionState.Failed,
                        CompletedAtUtc = now,
                        ErrorCategory = "session-timeout",
                    };
                    receive.Touch();
                    persistedFailure = receive.Session;
                }
                else if (DropLinkSessionPolicy.IsTerminal(state))
                {
                    var terminalAt = receive.Session.CompletedAtUtc ?? receive.LastActivityUtc;
                    retire = now - terminalAt >= DropLinkSessionPolicy.RetentionFor(state);
                }
            }
            finally
            {
                receive.MutationGate.Release();
            }

            if (persistedFailure is not null)
            {
                try
                {
                    await transfers.UpdateSessionAsync(persistedFailure, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    logger.LogWarning(
                        exception,
                        "DropLink could not persist timeout for session {SessionId}.",
                        persistedFailure.Id);
                }
            }

            if (retire && _sessions.TryRemove(receive.Session.Id, out var removed))
            {
                removed.Dispose();
                TryDeleteDirectory(removed.StagingRoot);
            }
        }
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

    private static IReadOnlyDictionary<Guid, IReadOnlyList<int>> SnapshotChunks(ReceiveTransfer receive) => receive.Chunks.Snapshot();

    private static async Task CommitItemAsync(ReceiveTransfer receive, TransferItemManifest item, CancellationToken cancellationToken)
    {
        if (item.ChunkCount is null || receive.Chunks.GetItemCount(item.Id) != item.ChunkCount.Value)
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
        receive.CompletedPaths.Enqueue(relative);
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
        lock (_offerNotificationGate)
        {
            _offerNotificationCancellation.Dispose();
        }

        _sessionLifetimeCancellation.Dispose();
    }

    public async Task StopAsync()
    {
        lock (_offerNotificationGate)
        {
            _offerNotificationCancellation.Cancel();
        }
        await AwaitOfferNotificationsAsync().ConfigureAwait(false);

        _sessionLifetimeCancellation.Cancel();
        var lifetimeTask = Interlocked.Exchange(ref _sessionLifetimeTask, null);
        if (lifetimeTask is not null)
        {
            try
            {
                await lifetimeTask.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
            {
            }
        }

        if (_app is not null)
        {
            await _app.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
        }

        _app = null;
        _endpoint = null;

        var sessions = _sessions.Values.ToArray();
        foreach (var session in sessions)
        {
            session.CancelLifetime();
        }

        var finalizers = sessions
            .Select(session => session.GetFinalizationTask())
            .Where(task => task is not null)
            .Cast<Task>()
            .ToArray();
        if (finalizers.Length > 0)
        {
            try
            {
                await Task.WhenAll(finalizers).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "DropLink transfer finalization did not settle cleanly during shutdown.");
            }
        }

        foreach (var session in sessions)
        {
            if (_sessions.TryRemove(session.Session.Id, out var removed))
            {
                removed.Dispose();
                TryDeleteDirectory(removed.StagingRoot);
            }
        }

        _usedNonces.Clear();
        _usedHandoffSessions.Clear();
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "DropLink staging cleanup failed for {StagingPath}.", path);
        }
    }

    private sealed class ReceiveTransfer : IDisposable
    {
        private readonly Guid _peerId;
        private int _disposed;

        public ReceiveTransfer(
            TransferSession session,
            TransferManifest manifest,
            string stagingRoot,
            string destinationRoot)
        {
            _peerId = session.PeerId ?? throw new InvalidDataException("A receive transfer peer is required.");
            Session = session;
            Manifest = manifest;
            StagingRoot = stagingRoot;
            DestinationRoot = destinationRoot;
            LastActivityUtc = session.CreatedAtUtc;
        }

        public TransferSession Session { get; set; }

        public Guid PeerId => _peerId;

        public TransferManifest Manifest { get; }

        public string StagingRoot { get; }

        public string DestinationRoot { get; }

        public DropLinkChunkLedger Chunks { get; } = new();

        public ConcurrentQueue<string> CompletedPaths { get; } = new();

        public SemaphoreSlim MutationGate { get; } = new(1, 1);

        public SemaphoreSlim FinalizationGate { get; } = new(1, 1);

        public DropLinkSingleFlight<TransferCompleteResponse> Finalization { get; } = new();

        public CancellationTokenSource LifetimeCancellation { get; } = new();

        public DateTimeOffset LastActivityUtc { get; private set; }

        public void Touch() => LastActivityUtc = DateTimeOffset.UtcNow;

        public Task? GetFinalizationTask()
        {
            lock (FinalizationTaskGate)
            {
                return FinalizationTask;
            }
        }

        public void CancelLifetime() => LifetimeCancellation.Cancel();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            LifetimeCancellation.Cancel();
            LifetimeCancellation.Dispose();
            MutationGate.Dispose();
            FinalizationGate.Dispose();
        }
    }
}
