using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DropSpace.Core.Transfer;
using DropSpace.Infrastructure.Network;
using DropSpace.Infrastructure.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class DropLinkClientCancellationNativeSmokeTests
{
    [TestMethod]
    [TestCategory("NativeSmoke")]
    public async Task CancellingWhileApprovalIsPendingCancelsTheRemoteOffer()
    {
#if !AUDIT_CLIENT_SOURCE_LINKED
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The repository smoke test uses Windows DPAPI device stores.");
        }
#endif
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "send.txt");
        await File.WriteAllTextAsync(source, "approval cancellation");
        var paths = new AppStoragePaths(root);
        var identities = new DeviceIdentityStore(paths);
        var secrets = new DeviceSecretStore(paths);
        var remoteId = Guid.NewGuid();
        var secret = RandomNumberGenerator.GetBytes(32);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
            listen => listen.UseHttps(certificate)));
        await using var app = builder.Build();
        using var cancellation = new CancellationTokenSource();
        var approvalSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelSeen = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var offeredSession = Guid.Empty;
        app.Use(async (context, next) =>
        {
            context.Request.EnableBuffering();
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body);
            context.Request.Body.Position = 0;
            var hash = Convert.ToHexString(SHA256.HashData(body.ToArray())).ToLowerInvariant();
            var expected = DropLinkPairingService.ComputeAuth(secret, context.Request.Method,
                context.Request.Path.Value!, context.Request.Headers[DropLinkProtocolHeaders.Nonce].ToString(), hash);
            if (context.Request.Headers[DropLinkProtocolHeaders.BodySha256] != hash ||
                !DropLinkPairingService.FixedTimeEquals(expected, context.Request.Headers[DropLinkProtocolHeaders.Auth].ToString()))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next();
        });
        app.MapPost(DropLinkProtocolRoutes.TransferOffers, async (HttpContext context) =>
        {
            var offer = await context.Request.ReadFromJsonAsync<TransferOfferRequest>();
            offeredSession = offer!.Manifest.SessionId;
            return Results.Json(new TransferOfferResponse(offeredSession, TransferSessionState.AwaitingApproval, null));
        });
        app.MapGet(DropLinkProtocolRoutes.TransferStatusTemplate, (Guid sessionId) =>
        {
            approvalSeen.TrySetResult();
            return Results.Json(Status(sessionId, TransferSessionState.AwaitingApproval));
        });
        app.MapPost(DropLinkProtocolRoutes.TransferCancelTemplate, (Guid sessionId) =>
        {
            cancelSeen.TrySetResult(sessionId);
            return Results.Json(Status(sessionId, TransferSessionState.Cancelled));
        });
        try
        {
            await secrets.SaveAsync(remoteId, secret);
            await app.StartAsync();
            var endpoint = new Uri(app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single());
            var peer = new PeerDevice(remoteId, "Peer", DevicePlatform.Windows, fingerprint,
                PeerCapability.HandoffFiles, PeerTrustState.Trusted, DateTimeOffset.UtcNow, null);
            var client = new DropLinkClient(identities, secrets, null!, null!);
            var send = client.SendFilesAsync(peer, endpoint, [source], cancellationToken: cancellation.Token);
            var first = await Task.WhenAny(approvalSeen.Task, send).WaitAsync(TimeSpan.FromSeconds(10));
            if (first == send)
            {
                var earlyResult = await send;
                Assert.Fail($"The send completed with {earlyResult.State} before the approval poll.");
            }

            cancellation.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() => send.WaitAsync(TimeSpan.FromSeconds(10)));
            var cancelledSession = await cancelSeen.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreNotEqual(Guid.Empty, offeredSession);
            Assert.AreEqual(offeredSession, cancelledSession);
        }
        finally
        {
            await app.StopAsync();
            CryptographicOperations.ZeroMemory(secret);
            Directory.Delete(root, recursive: true);
        }
    }

    private static TransferStatusResponse Status(Guid sessionId, TransferSessionState state) =>
        new(sessionId, state, 0, new Dictionary<Guid, IReadOnlyList<int>>(), [], null);
}
