using System.Buffers;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using DropSpace.Core.Transfer;
using Microsoft.AspNetCore.Http;

namespace DropSpace.Infrastructure.Network;

/// <summary>
/// Authenticates DropLink requests before endpoint model binding can materialize attacker-controlled JSON.
/// The request body is hashed from the raw bytes and then rewound for the endpoint.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DropLinkAuthenticationMiddleware
{
    internal const string AuthenticatedPeerContextKey = "DropLink.AuthenticatedPeer";
    private const int BufferSize = 64 * 1024;
    private const int AuthenticationTagBytes = 32;

    private readonly SemaphoreSlim _requests = new(8, 8);
    private readonly RequestDelegate _next;
    private readonly DeviceSecretStore _secrets;
    private readonly TransferRepository _transfers;
    private readonly DropLinkNonceCache _nonces;

    public DropLinkAuthenticationMiddleware(
        RequestDelegate next,
        DeviceSecretStore secrets,
        TransferRepository transfers,
        DropLinkNonceCache nonces)
    {
        _next = next;
        _secrets = secrets;
        _transfers = transfers;
        _nonces = nonces;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!await _requests.WaitAsync(0, context.RequestAborted).ConfigureAwait(false))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            return;
        }
        try { await InvokeAdmittedAsync(context).ConfigureAwait(false); }
        finally { _requests.Release(); }
    }

    private async Task InvokeAdmittedAsync(HttpContext context)
    {
        var path = context.Request.Path.ToString();
        if (DropLinkProtocolRoutes.IsPairing(path))
        {
            if (!await BufferBodyAsync(
                    context,
                    DropLinkProtocolPolicy.MaximumPairingBodyBytes,
                    context.RequestAborted).ConfigureAwait(false))
            {
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                }

                return;
            }

            await _next(context).ConfigureAwait(false);
            return;
        }

        if (!DropLinkProtocolRoutes.RequiresAuthentication(path))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (!await AuthenticateAsync(context, context.RequestAborted).ConfigureAwait(false))
        {
            if (!context.Response.HasStarted && context.Response.StatusCode == StatusCodes.Status200OK)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            }

            return;
        }

        await _next(context).ConfigureAwait(false);
    }

    private async Task<bool> AuthenticateAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var request = context.Request;
        var bodyLimit = DropLinkProtocolPolicy.BodyLimitFor(request.Path.ToString());
        if (request.ContentLength > bodyLimit)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return false;
        }
        // Authenticate the declared hash before reading or buffering any body bytes.
        var bodyHash = request.Headers[DropLinkProtocolHeaders.BodySha256].ToString();
        var device = request.Headers[DropLinkProtocolHeaders.Device].ToString();
        var nonce = request.Headers[DropLinkProtocolHeaders.Nonce].ToString();
        var auth = request.Headers[DropLinkProtocolHeaders.Auth].ToString();
        if (!DropLinkProtocolPolicy.IsLowerHexHash(bodyHash) || !Guid.TryParse(device, out var peerId) ||
            peerId == Guid.Empty || !DropLinkProtocolPolicy.IsAuthenticationNonce(nonce) || auth.Length != 44)
            return false;
        byte[] authBytes;
        try { authBytes = Convert.FromBase64String(auth); }
        catch (FormatException) { return false; }
        byte[]? secret = null;
        var reserved = false;
        var accepted = false;
        try
        {
            if (authBytes.Length != AuthenticationTagBytes) return false;
            if (await _transfers.GetPeerTrustStateAsync(peerId, cancellationToken).ConfigureAwait(false) != PeerTrustState.Trusted)
                return false;
            secret = await _secrets.GetAsync(peerId, cancellationToken).ConfigureAwait(false);
            if (secret is null) return false;
            var expected = DropLinkPairingService.ComputeAuth(secret, request.Method, request.Path.ToString(), nonce, bodyHash);
            if (!DropLinkPairingService.FixedTimeEquals(expected, auth)) return false;
            if (!_nonces.TryReserve(peerId, nonce, DateTimeOffset.UtcNow)) return false;
            reserved = true;

            request.EnableBuffering(bufferThreshold: BufferSize, bufferLimit: bodyLimit);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            byte[]? actualHash = null;
            var suppliedHash = Convert.FromHexString(bodyHash);
            try
            {
                long total = 0;
                int read;
                while ((read = await request.Body.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > bodyLimit)
                    {
                        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                        return false;
                    }
                    digest.AppendData(buffer, 0, read);
                }
                request.Body.Position = 0;
                actualHash = digest.GetHashAndReset();
                if (!CryptographicOperations.FixedTimeEquals(actualHash, suppliedHash)) return false;
                context.Items[AuthenticatedPeerContextKey] = peerId;
                accepted = true;
                return true;
            }
            finally
            {
                if (actualHash is not null) CryptographicOperations.ZeroMemory(actualHash);
                CryptographicOperations.ZeroMemory(suppliedHash);
                CryptographicOperations.ZeroMemory(buffer);
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (IOException)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return false;
        }
        catch (InvalidOperationException) { return false; }
        finally
        {
            if (reserved && !accepted) _nonces.Remove(peerId, nonce);
            if (secret is not null) CryptographicOperations.ZeroMemory(secret);
            CryptographicOperations.ZeroMemory(authBytes);
        }
    }

    private static async Task<bool> BufferBodyAsync(
        HttpContext context,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var request = context.Request;
        if (request.ContentLength is > 0 && request.ContentLength > maximumBytes)
        {
            return false;
        }

        try
        {
            request.EnableBuffering(
                bufferThreshold: BufferSize,
                bufferLimit: maximumBytes);
            await request.Body.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
            request.Body.Position = 0;
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IOException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
