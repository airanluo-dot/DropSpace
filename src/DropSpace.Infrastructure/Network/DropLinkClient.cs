using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DropSpace.Core.Transfer;
using System.Runtime.Versioning;
using System.Net.Http.Json;

namespace DropSpace.Infrastructure.Network;

public sealed record TransferProgress(Guid SessionId, Guid ItemId, long TransferredBytes, long TotalBytes);

[SupportedOSPlatform("windows")]
public sealed class DropLinkClient(
    DeviceIdentityStore identities,
    DeviceSecretStore secrets,
    DropLinkPairingService pairing,
    TransferRepository transfers)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<DeviceDescriptor> GetDeviceAsync(Uri endpoint, string expectedFingerprint, CancellationToken cancellationToken = default)
    {
        using var client = CreateClient(endpoint, expectedFingerprint);
        using var response = await client.GetAsync(DropLinkProtocolRoutes.Device, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DeviceDescriptor>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The remote device descriptor was empty.");
    }

    public async Task<PeerDevice> PairAsync(
        Uri endpoint,
        string expectedFingerprint,
        PeerCapability capabilities,
        Func<int, CancellationToken, Task<bool>>? confirmSas = null,
        CancellationToken cancellationToken = default)
    {
        using var handshake = await pairing.CreateHelloAsync(capabilities, cancellationToken).ConfigureAwait(false);
        using var client = CreateClient(endpoint, expectedFingerprint);
        using var helloContent = JsonContent.Create(handshake.Hello, options: JsonOptions);
        using var response = await client.PostAsync(DropLinkProtocolRoutes.PairingHello, helloContent, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var offer = await response.Content.ReadFromJsonAsync<PairingOffer>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The remote pairing offer was empty.");
        if (offer.State != PairingState.AwaitingLocalSasConfirmation || offer.SessionId == Guid.Empty)
        {
            throw new InvalidDataException("The remote pairing offer is not awaiting bilateral confirmation.");
        }
        if (offer.LocalHello.DeviceId == handshake.Hello.DeviceId)
        {
            throw new InvalidDataException("A device cannot pair with itself.");
        }
        var secret = DropLinkPairingService.DeriveSecret(handshake, offer.LocalHello);
        var saved = false;
        try
        {
            var sas = DropLinkPairingService.ComputeSas(secret, handshake.Hello, offer.LocalHello);
            if (sas != offer.Sas)
            {
                await SendPairingDecisionAsync(client, offer, offer.Sas, false, PairingDecision.Reject, CancellationToken.None).ConfigureAwait(false);
                throw new UnauthorizedAccessException("The displayed pairing SAS did not match the remote transcript.");
            }

            if (confirmSas is null)
            {
                await SendPairingDecisionAsync(client, offer, sas, false, PairingDecision.Reject, CancellationToken.None).ConfigureAwait(false);
                throw new UnauthorizedAccessException("Pairing requires an explicit local SAS confirmation.");
            }

            if (!await confirmSas(sas, cancellationToken).ConfigureAwait(false))
            {
                await SendPairingDecisionAsync(client, offer, sas, false, PairingDecision.Reject, CancellationToken.None).ConfigureAwait(false);
                throw new UnauthorizedAccessException("Pairing confirmation was declined.");
            }

            PairingConfirmationResponse result;
            try
            {
                result = await SendPairingDecisionAsync(client, offer, sas, true, PairingDecision.Confirm, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await SendPairingDecisionAsync(client, offer, sas, false, PairingDecision.Cancel, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException)
                {
                    // The local cancellation remains authoritative if the peer is already gone.
                }

                throw;
            }

            if (!result.Trusted || result.State != PairingState.Trusted || result.PeerId != offer.LocalHello.DeviceId)
            {
                throw new UnauthorizedAccessException(string.Concat("The remote device did not complete bilateral pairing (", result.State, ")."));
            }

            await secrets.SaveAsync(offer.LocalHello.DeviceId, secret, cancellationToken).ConfigureAwait(false);
            saved = true;
            var peer = new PeerDevice(
                offer.LocalHello.DeviceId,
                offer.LocalHello.DisplayName,
                offer.LocalHello.Platform,
                offer.LocalHello.IdentityFingerprint,
                offer.LocalHello.Capabilities,
                PeerTrustState.Trusted,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow);
            await transfers.UpsertPeerAsync(peer, offer.LocalHello.DeviceId.ToString("N"), cancellationToken).ConfigureAwait(false);
            return peer;
        }
        finally
        {
            if (!saved) CryptographicOperations.ZeroMemory(secret);
        }
    }

    public async Task<HandoffMessageResponse> SendHandoffAsync(
        PeerDevice peer,
        Uri endpoint,
        HandoffMessageKind kind,
        string payload,
        string? displayLabel = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peer);
        var identity = await identities.GetOrCreateAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var message = HandoffMessagePolicy.Create(identity.DeviceId, identity.DisplayName, kind, payload, displayLabel);
        using var authenticated = await CreateAuthenticatedClientAsync(peer, endpoint, cancellationToken).ConfigureAwait(false);
        return await SendAuthenticatedJsonAsync<HandoffMessageRequest, HandoffMessageResponse>(
                authenticated,
                DropLinkProtocolRoutes.HandoffText,
                new HandoffMessageRequest(authenticated.LocalDeviceId, message),
                HttpMethod.Post,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ClipboardSyncResponse> SendClipboardAsync(
        PeerDevice peer,
        Uri endpoint,
        ClipboardEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(envelope);
        ClipboardEnvelopePolicy.Validate(envelope);
        var identity = await identities.GetOrCreateAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (envelope.OriginDeviceId != identity.DeviceId)
        {
            throw new InvalidDataException("A clipboard envelope must originate from the local device.");
        }

        using var authenticated = await CreateAuthenticatedClientAsync(peer, endpoint, cancellationToken).ConfigureAwait(false);
        return await SendAuthenticatedJsonAsync<ClipboardSyncRequest, ClipboardSyncResponse>(
                authenticated,
                DropLinkProtocolRoutes.Clipboard,
                new ClipboardSyncRequest(authenticated.LocalDeviceId, envelope),
                HttpMethod.Post,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<TransferCompleteResponse> SendFilesAsync(
        PeerDevice peer,
        Uri endpoint,
        IReadOnlyList<string> sourcePaths,
        IProgress<TransferProgress>? progress = null,
        TransferLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(sourcePaths);
        limits ??= new TransferLimits();
        limits.Validate();
        if (sourcePaths.Count == 0) throw new ArgumentException("At least one source path is required.", nameof(sourcePaths));
        var files = await EnumerateFilesAsync(sourcePaths, limits, cancellationToken).ConfigureAwait(false);
        var sessionId = Guid.NewGuid();
        var items = new List<TransferItemManifest>(files.Count);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hash = await HashFileAsync(file.Path, cancellationToken).ConfigureAwait(false);
            var size = new FileInfo(file.Path).Length;
            items.Add(new TransferItemManifest(Guid.NewGuid(), TransferItemKind.File, TransferManifestPolicy.SafeDisplayName(Path.GetFileName(file.Path)), file.RelativePath, size, hash, file.MimeType, size == 0 ? 0 : (int)Math.Ceiling(size / (double)limits.ChunkBytes)));
        }

        var manifest = TransferManifestPolicy.Create(sessionId, items, limits);
        using var authenticated = await CreateAuthenticatedClientAsync(peer, endpoint, cancellationToken).ConfigureAwait(false);
        var offer = await SendAuthenticatedJsonAsync<TransferOfferRequest, TransferOfferResponse>(authenticated, DropLinkProtocolRoutes.TransferOffers, new TransferOfferRequest(authenticated.LocalDeviceId, manifest), HttpMethod.Post, cancellationToken).ConfigureAwait(false);
        var accepted = await WaitForAcceptanceAsync(authenticated, offer.SessionId, cancellationToken).ConfigureAwait(false);
        if (accepted.State != TransferSessionState.Accepted) return new TransferCompleteResponse(offer.SessionId, accepted.State, [], accepted.ErrorCategory);

        try
        {
            var transferred = 0L;
            foreach (var pair in files.Zip(items))
            {
                var item = pair.Second;
                accepted.ReceivedChunks.TryGetValue(item.Id, out var receivedChunks);
                await SendFileChunksAsync(authenticated, offer.SessionId, item, pair.First.Path, limits, receivedChunks, progress, transferred, manifest.TotalBytes, cancellationToken).ConfigureAwait(false);
                transferred += item.Size;
            }

            return await SendAuthenticatedJsonAsync<object, TransferCompleteResponse>(authenticated, DropLinkProtocolRoutes.TransferComplete(offer.SessionId), new { }, HttpMethod.Post, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                await CancelTransferAsync(authenticated, offer.SessionId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException)
            {
                // Cancellation remains authoritative even when the peer has already disconnected.
            }

            throw;
        }
    }

    public async Task<TransferStatusResponse> CancelTransferAsync(
        PeerDevice peer,
        Uri endpoint,
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        using var authenticated = await CreateAuthenticatedClientAsync(peer, endpoint, cancellationToken).ConfigureAwait(false);
        return await CancelTransferAsync(authenticated, sessionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TransferStatusResponse> SetTransferApprovalAsync(
        PeerDevice peer,
        Uri endpoint,
        Guid sessionId,
        bool accepted,
        CancellationToken cancellationToken = default)
    {
        using var authenticated = await CreateAuthenticatedClientAsync(peer, endpoint, cancellationToken).ConfigureAwait(false);
        return await SendAuthenticatedJsonAsync<TransferAcceptRequest, TransferStatusResponse>(authenticated, DropLinkProtocolRoutes.TransferAccept(sessionId), new TransferAcceptRequest(accepted), HttpMethod.Post, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TransferStatusResponse> WaitForAcceptanceAsync(AuthenticatedClient authenticated, Guid sessionId, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await SendAuthenticatedJsonAsync<object, TransferStatusResponse>(authenticated, DropLinkProtocolRoutes.TransferStatus(sessionId), new { }, HttpMethod.Get, cancellationToken).ConfigureAwait(false);
            if (status.State is TransferSessionState.Accepted or TransferSessionState.Rejected or TransferSessionState.Cancelled or TransferSessionState.Failed) return status;
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }

        return new TransferStatusResponse(sessionId, TransferSessionState.Failed, 0, new Dictionary<Guid, IReadOnlyList<int>>(), [], "approval-timeout");
    }

    private Task<TransferStatusResponse> CancelTransferAsync(
        AuthenticatedClient authenticated,
        Guid sessionId,
        CancellationToken cancellationToken) =>
        SendAuthenticatedJsonAsync<object, TransferStatusResponse>(
            authenticated,
            DropLinkProtocolRoutes.TransferCancel(sessionId),
            new { },
            HttpMethod.Post,
            cancellationToken);

    private static async Task<PairingConfirmationResponse> SendPairingDecisionAsync(
        HttpClient client,
        PairingOffer offer,
        int sas,
        bool confirmed,
        PairingDecision decision,
        CancellationToken cancellationToken)
    {
        var confirmation = new PairingConfirmationRequest(offer.SessionId, sas, confirmed, decision);
        using var confirmationContent = JsonContent.Create(confirmation, options: JsonOptions);
        using var response = await client.PostAsync(DropLinkProtocolRoutes.PairingConfirm, confirmationContent, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PairingConfirmationResponse>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The remote pairing confirmation was empty.");
    }

    private async Task SendFileChunksAsync(
        AuthenticatedClient authenticated,
        Guid sessionId,
        TransferItemManifest item,
        string path,
        TransferLimits limits,
        IReadOnlyList<int>? receivedChunks,
        IProgress<TransferProgress>? progress,
        long alreadyTransferred,
        long totalBytes,
        CancellationToken cancellationToken)
    {
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        for (var index = 0; index < item.ChunkCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (receivedChunks?.Contains(index) == true)
            {
                var receivedLength = (int)Math.Min(limits.ChunkBytes, item.Size - (long)index * limits.ChunkBytes);
                progress?.Report(new TransferProgress(sessionId, item.Id, alreadyTransferred + (long)index * limits.ChunkBytes + receivedLength, totalBytes));
                continue;
            }
            var offset = (long)index * limits.ChunkBytes;
            source.Position = offset;
            var length = (int)Math.Min(limits.ChunkBytes, item.Size - offset);
            var bytes = new byte[length];
            var read = 0;
            while (read < length)
            {
                var count = await source.ReadAsync(bytes.AsMemory(read, length - read), cancellationToken).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException("The source changed during transfer.");
                read += count;
            }

            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var pathValue = DropLinkProtocolRoutes.TransferChunk(sessionId, item.Id, index);
            await SendAuthenticatedBytesAsync(authenticated, pathValue, bytes, hash, cancellationToken).ConfigureAwait(false);
            progress?.Report(new TransferProgress(sessionId, item.Id, alreadyTransferred + offset + length, totalBytes));
        }
    }

    private async Task<AuthenticatedClient> CreateAuthenticatedClientAsync(PeerDevice peer, Uri endpoint, CancellationToken cancellationToken)
    {
        var secret = await secrets.GetAsync(peer.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new UnauthorizedAccessException("The peer secret is unavailable; pair the device again.");
        var identity = await identities.GetOrCreateAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return new AuthenticatedClient(CreateClient(endpoint, peer.IdentityFingerprint), peer.Id, identity.DeviceId, secret);
    }

    private async Task SendAuthenticatedBytesAsync(AuthenticatedClient authenticated, string path, byte[] bytes, string chunkHash, CancellationToken cancellationToken)
    {
        var bodyHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        using var request = CreateRequest(HttpMethod.Put, path, authenticated.Secret, bodyHash, authenticated.LocalDeviceId, new ByteArrayContent(bytes));
        request.Headers.Add("X-DropLink-Chunk-SHA256", chunkHash);
        using var response = await authenticated.Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private async Task<TResponse> SendAuthenticatedJsonAsync<TRequest, TResponse>(AuthenticatedClient authenticated, string path, TRequest body, HttpMethod method, CancellationToken cancellationToken)
    {
        var bytes = method == HttpMethod.Get ? Array.Empty<byte>() : JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions);
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(DropLinkProtocolHeaders.JsonContentType);
        using var request = CreateRequest(method, path, authenticated.Secret, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), authenticated.LocalDeviceId, content);
        using var response = await authenticated.Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The DropLink response body was empty.");
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path, byte[] secret, string bodyHash, Guid peerId, HttpContent content)
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(DropLinkProtocolPolicy.AuthenticationNonceBytes));
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Add(DropLinkProtocolHeaders.Device, peerId.ToString("D"));
        request.Headers.Add(DropLinkProtocolHeaders.Nonce, nonce);
        request.Headers.Add(DropLinkProtocolHeaders.BodySha256, bodyHash);
        request.Headers.Add(DropLinkProtocolHeaders.Auth, DropLinkPairingService.ComputeAuth(secret, method.Method, path, nonce, bodyHash));
        return request;
    }

    private static HttpClient CreateClient(Uri endpoint, string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new InvalidDataException("DropLink endpoints must be HTTPS origins without query or fragment data.");
        }
        var normalized = fingerprint?.Replace(":", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length != 64 || normalized.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("The DropLink certificate fingerprint is invalid.");
        }
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
            {
                if (certificate is null || errors is System.Net.Security.SslPolicyErrors.RemoteCertificateNotAvailable) return false;
                var actual = Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())).ToLowerInvariant();
                return string.Equals(actual, normalized, StringComparison.OrdinalIgnoreCase);
            },
        };
        return new HttpClient(handler) { BaseAddress = new Uri(endpoint.ToString().TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(10) };
    }

    private static Task<List<SourceFile>> EnumerateFilesAsync(IReadOnlyList<string> sourcePaths, TransferLimits limits, CancellationToken cancellationToken)
    {
        var result = new List<SourceFile>();
        foreach (var source in sourcePaths)
        {
            var full = Path.GetFullPath(source);
            if (File.Exists(full))
            {
                result.Add(new SourceFile(full, TransferManifestPolicy.SafeDisplayName(Path.GetFileName(full)), GuessMime(full)));
                continue;
            }
            if (!Directory.Exists(full)) throw new FileNotFoundException("Transfer source is unavailable.");
            var rootName = TransferManifestPolicy.SafeDisplayName(Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(file);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                var relative = Path.GetRelativePath(full, file).Replace(Path.DirectorySeparatorChar, '/');
                result.Add(new SourceFile(file, TransferManifestPolicy.NormalizeRelativePath(string.Concat(rootName, "/", relative)), GuessMime(file)));
                if (result.Count > limits.MaxItems) throw new InvalidDataException("The transfer item limit was exceeded.");
            }
        }
        if (result.Count == 0) throw new InvalidDataException("The transfer contains no regular files.");
        return Task.FromResult(result);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static string GuessMime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".txt" or ".md" or ".json" or ".csv" => "text/plain",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".pdf" => "application/pdf",
        _ => "application/octet-stream",
    };

    private sealed record SourceFile(string Path, string RelativePath, string MimeType);

    private sealed class AuthenticatedClient(HttpClient client, Guid remoteDeviceId, Guid localDeviceId, byte[] secret) : IDisposable
    {
        public HttpClient Client { get; } = client;
        public Guid RemoteDeviceId { get; } = remoteDeviceId;
        public Guid LocalDeviceId { get; } = localDeviceId;
        public byte[] Secret { get; } = secret;

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(Secret);
            Client.Dispose();
        }
    }
}
