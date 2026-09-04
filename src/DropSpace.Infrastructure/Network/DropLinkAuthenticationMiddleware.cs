using System.Buffers;
using System.Security.Cryptography;
using System.Runtime.Versioning;
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

    private readonly RequestDelegate _next;
    private readonly DeviceSecretStore _secrets;
    private readonly DropLinkNonceCache _nonces;

    public DropLinkAuthenticationMiddleware(
        RequestDelegate next,
        DeviceSecretStore secrets,
        DropLinkNonceCache nonces)
    {
        _next = next;
        _secrets = secrets;
        _nonces = nonces;
    }

    public async Task InvokeAsync(HttpContext context)
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
        if (request.ContentLength is > DropLinkProtocolPolicy.MaximumAuthenticatedBodyBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return false;
        }

        try
        {
            request.EnableBuffering(
                bufferThreshold: BufferSize,
                bufferLimit: DropLinkProtocolPolicy.MaximumAuthenticatedBodyBytes);

            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            byte[]? actualBodyHash = null;
            byte[]? suppliedBodyHash = null;
            try
            {
                long totalBytes = 0;
                int read;
                while ((read = await request.Body.ReadAsync(
                           buffer.AsMemory(0, BufferSize),
                           cancellationToken).ConfigureAwait(false)) > 0)
                {
                    totalBytes += read;
                    if (totalBytes > DropLinkProtocolPolicy.MaximumAuthenticatedBodyBytes)
                    {
                        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                        return false;
                    }

                    digest.AppendData(buffer, 0, read);
                }

                request.Body.Position = 0;
                actualBodyHash = digest.GetHashAndReset();

                var bodyHashHeader = request.Headers[DropLinkProtocolHeaders.BodySha256].ToString();
                if (!DropLinkProtocolPolicy.IsLowerHexHash(bodyHashHeader))
                {
                    return false;
                }

                suppliedBodyHash = Convert.FromHexString(bodyHashHeader);
                if (!CryptographicOperations.FixedTimeEquals(actualBodyHash, suppliedBodyHash))
                {
                    return false;
                }

                var deviceHeader = request.Headers[DropLinkProtocolHeaders.Device].ToString();
                var nonce = request.Headers[DropLinkProtocolHeaders.Nonce].ToString();
                var auth = request.Headers[DropLinkProtocolHeaders.Auth].ToString();
                if (!Guid.TryParse(deviceHeader, out var peerId) ||
                    !DropLinkProtocolPolicy.IsAuthenticationNonce(nonce) ||
                    string.IsNullOrWhiteSpace(auth))
                {
                    return false;
                }

                byte[] authBytes;
                try
                {
                    authBytes = Convert.FromBase64String(auth);
                }
                catch (FormatException)
                {
                    return false;
                }

                if (authBytes.Length != AuthenticationTagBytes)
                {
                    CryptographicOperations.ZeroMemory(authBytes);
                    return false;
                }

                var secret = await _secrets.GetAsync(peerId, cancellationToken).ConfigureAwait(false);
                if (secret is null)
                {
                    CryptographicOperations.ZeroMemory(authBytes);
                    return false;
                }

                try
                {
                    if (!_nonces.TryReserve(peerId, nonce, DateTimeOffset.UtcNow))
                    {
                        return false;
                    }

                    var expected = DropLinkPairingService.ComputeAuth(
                        secret,
                        request.Method,
                        request.Path.ToString(),
                        nonce,
                        bodyHashHeader);
                    if (!DropLinkPairingService.FixedTimeEquals(expected, auth))
                    {
                        _nonces.Remove(peerId, nonce);
                        return false;
                    }

                    context.Items[AuthenticatedPeerContextKey] = peerId;
                    return true;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(secret);
                    CryptographicOperations.ZeroMemory(authBytes);
                }
            }
            finally
            {
                if (actualBodyHash is not null)
                {
                    CryptographicOperations.ZeroMemory(actualBodyHash);
                }

                if (suppliedBodyHash is not null)
                {
                    CryptographicOperations.ZeroMemory(suppliedBodyHash);
                }

                CryptographicOperations.ZeroMemory(buffer);
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IOException)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
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
