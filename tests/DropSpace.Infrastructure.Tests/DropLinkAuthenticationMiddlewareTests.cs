using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using DropSpace.Core.Transfer;
using DropSpace.Infrastructure.Data;
using DropSpace.Infrastructure.Storage;
using DropSpace.Infrastructure.Network;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class DropLinkAuthenticationMiddlewareTests
{
    [TestMethod]
    public async Task ValidBodyHashAndHmacReachEndpointAndRewindBody()
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-tests", Guid.NewGuid().ToString("N"));
        var paths = new AppStoragePaths(root);
        var peerId = Guid.NewGuid();
        var secret = RandomNumberGenerator.GetBytes(32);
        try
        {
            var secrets = new DeviceSecretStore(paths);
            await secrets.SaveAsync(peerId, secret);
            var transfers = await CreateTrustedTransfersAsync(paths, peerId);

            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(DropLinkProtocolPolicy.AuthenticationNonceBytes));
            var body = """{"eventId":"auth-test"}""";
            var cache = new DropLinkNonceCache();
            var reached = 0;
            RequestDelegate next = context =>
            {
                Assert.AreEqual(0, context.Request.Body.Position);
                Interlocked.Increment(ref reached);
                return Task.CompletedTask;
            };
            var middleware = new DropLinkAuthenticationMiddleware(next, secrets, transfers, cache);

            var valid = CreateContext(peerId, secret, nonce, body);
            await middleware.InvokeAsync(valid);

            Assert.AreEqual(1, reached);
            Assert.AreEqual(StatusCodes.Status200OK, valid.Response.StatusCode);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task BodyHashMismatchIsRejectedBeforeEndpoint()
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-tests", Guid.NewGuid().ToString("N"));
        var paths = new AppStoragePaths(root);
        var peerId = Guid.NewGuid();
        var secret = RandomNumberGenerator.GetBytes(32);
        try
        {
            var secrets = new DeviceSecretStore(paths);
            await secrets.SaveAsync(peerId, secret);
            var transfers = await CreateTrustedTransfersAsync(paths, peerId);

            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(DropLinkProtocolPolicy.AuthenticationNonceBytes));
            var body = """{"eventId":"hash-mismatch"}""";
            var cache = new DropLinkNonceCache();
            var reached = false;
            RequestDelegate next = _ =>
            {
                reached = true;
                return Task.CompletedTask;
            };
            var middleware = new DropLinkAuthenticationMiddleware(next, secrets, transfers, cache);
            var incorrectHash = new string('0', DropLinkProtocolPolicy.BodyHashHexLength);
            var context = CreateContext(peerId, secret, nonce, body, incorrectHash);

            await middleware.InvokeAsync(context);

            Assert.IsFalse(reached);
            Assert.AreEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
            Assert.AreEqual(0, cache.Count);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task ReplayedNonceIsRejectedAfterFirstValidRequest()
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-tests", Guid.NewGuid().ToString("N"));
        var paths = new AppStoragePaths(root);
        var peerId = Guid.NewGuid();
        var secret = RandomNumberGenerator.GetBytes(32);
        try
        {
            var secrets = new DeviceSecretStore(paths);
            await secrets.SaveAsync(peerId, secret);
            var transfers = await CreateTrustedTransfersAsync(paths, peerId);

            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(DropLinkProtocolPolicy.AuthenticationNonceBytes));
            var body = """{"eventId":"replay"}""";
            var cache = new DropLinkNonceCache();
            var reached = 0;
            RequestDelegate next = _ =>
            {
                Interlocked.Increment(ref reached);
                return Task.CompletedTask;
            };
            var middleware = new DropLinkAuthenticationMiddleware(next, secrets, transfers, cache);

            await middleware.InvokeAsync(CreateContext(peerId, secret, nonce, body));
            var replay = CreateContext(peerId, secret, nonce, body);
            await middleware.InvokeAsync(replay);

            Assert.AreEqual(1, reached);
            Assert.AreEqual(StatusCodes.Status401Unauthorized, replay.Response.StatusCode);
            Assert.AreEqual(1, cache.Count);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            TryDelete(root);
        }
    }

    [TestMethod]
    public async Task SecretWithoutTrustedPeerStateIsRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-tests", Guid.NewGuid().ToString("N"));
        var paths = new AppStoragePaths(root);
        var peerId = Guid.NewGuid();
        var secret = RandomNumberGenerator.GetBytes(32);
        try
        {
            var secrets = new DeviceSecretStore(paths);
            await secrets.SaveAsync(peerId, secret);
            var transfers = await CreateTrustedTransfersAsync(paths, peerId);
            await transfers.UpdatePeerTrustStateAsync(peerId, PeerTrustState.PairingPending);

            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(DropLinkProtocolPolicy.AuthenticationNonceBytes));
            var body = "{\"eventId\":\"pending-peer\"}";
            var cache = new DropLinkNonceCache();
            var reached = false;
            RequestDelegate next = _ =>
            {
                reached = true;
                return Task.CompletedTask;
            };
            var middleware = new DropLinkAuthenticationMiddleware(next, secrets, transfers, cache);

            var context = CreateContext(peerId, secret, nonce, body);
            await middleware.InvokeAsync(context);

            Assert.IsFalse(reached);
            Assert.AreEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
            Assert.AreEqual(0, cache.Count);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            TryDelete(root);
        }
    }

    private static DefaultHttpContext CreateContext(
        Guid peerId,
        ReadOnlySpan<byte> secret,
        string nonce,
        string body,
        string? bodyHashOverride = null)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var bodyHash = Convert.ToHexString(SHA256.HashData(bodyBytes)).ToLowerInvariant();
        var signedHash = bodyHashOverride ?? bodyHash;
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = DropLinkProtocolRoutes.Clipboard;
        context.Request.Body = new MemoryStream(bodyBytes);
        context.Request.ContentLength = bodyBytes.Length;
        context.Request.Headers[DropLinkProtocolHeaders.Device] = peerId.ToString();
        context.Request.Headers[DropLinkProtocolHeaders.Nonce] = nonce;
        context.Request.Headers[DropLinkProtocolHeaders.BodySha256] = signedHash;
        context.Request.Headers[DropLinkProtocolHeaders.Auth] = DropLinkPairingService.ComputeAuth(
            secret,
            context.Request.Method,
            context.Request.Path.ToString(),
            nonce,
            signedHash);
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<TransferRepository> CreateTrustedTransfersAsync(AppStoragePaths paths, Guid peerId)
    {
        var transfers = new TransferRepository(new SqliteDatabase(paths, NullLogger<SqliteDatabase>.Instance));
        await transfers.UpsertPeerAsync(new PeerDevice(
            peerId,
            "Trusted peer",
            DevicePlatform.Windows,
            new string('a', 64),
            PeerCapability.HandoffFiles,
            PeerTrustState.Trusted,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow), peerId.ToString("N"));
        return transfers;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
