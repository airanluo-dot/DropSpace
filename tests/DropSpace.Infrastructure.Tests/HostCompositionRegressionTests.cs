using System.Runtime.Versioning;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using DropSpace.Core.Transfer;
using DropSpace.Infrastructure.Data;
using DropSpace.Infrastructure.Network;
using DropSpace.Infrastructure.Sharing;
using DropSpace.Infrastructure.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class HostCompositionRegressionTests
{
    [TestMethod]
    public async Task HostAuthenticationBuildsInsideIndependentApplicationServices()
    {
        var paths = new AppStoragePaths(Path.Combine(Path.GetTempPath(), "DropSpace-host-composition", Guid.NewGuid().ToString("N")));
        var identities = new DeviceIdentityStore(paths);
        var secrets = new DeviceSecretStore(paths);
        await using var pairing = new DropLinkPairingService(identities, secrets);
        var repository = new TransferRepository(new SqliteDatabase(paths, NullLogger<SqliteDatabase>.Instance));
        await using var host = new DropLinkHost(identities, secrets, pairing, repository,
            NullLogger<DropLinkHost>.Instance, new DropLinkNonceCache(),
            new StagingLeaseStore(paths, NullLogger<StagingLeaseStore>.Instance));
        await using var app = WebApplication.CreateSlimBuilder().Build();
        host.ConfigureAuthenticationPipeline(app);
        var reached = false;
        app.Run(_ => { reached = true; return Task.CompletedTask; });

        var pipeline = ((IApplicationBuilder)app).Build();
        var context = new DefaultHttpContext();
        context.Request.Path = DropLinkProtocolRoutes.TransferOffers;
        context.Request.Body = new MemoryStream();
        await pipeline(context);

        Assert.IsFalse(reached);
        Assert.AreEqual(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [TestMethod]
    [DataRow("文件.txt")]
    [DataRow("résumé.pdf")]
    [DataRow("emoji-😀.png")]
    public void NearbyDownloadDispositionPreservesUnicodeFilename(string filename)
    {
        var disposition = System.Net.Http.Headers.ContentDispositionHeaderValue.Parse(
            NearbyShareServer.CreateDownloadDisposition(filename));
        Assert.AreEqual("attachment", disposition.DispositionType);
        Assert.AreEqual(filename, disposition.FileNameStar);
    }

    [TestMethod]
    public async Task RemoteSenderCannotApproveItsOwnOfferAndLocalReceiverCan()
    {
        var paths = new AppStoragePaths(Path.Combine(Path.GetTempPath(), "DropSpace-host-consent", Guid.NewGuid().ToString("N")));
        try
        {
            var identities = new DeviceIdentityStore(paths);
            var secrets = new DeviceSecretStore(paths);
            await using var pairing = new DropLinkPairingService(identities, secrets);
            var repository = new TransferRepository(new SqliteDatabase(paths, NullLogger<SqliteDatabase>.Instance));
            var leases = new StagingLeaseStore(paths, NullLogger<StagingLeaseStore>.Instance);
            await using var host = new DropLinkHost(identities, secrets, pairing, repository,
                NullLogger<DropLinkHost>.Instance, new DropLinkNonceCache(), leases);
            var peerId = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
            var manifest = TransferManifestPolicy.Create(sessionId,
                [new TransferItemManifest(Guid.NewGuid(), TransferItemKind.File, "empty.txt", "empty.txt", 0,
                    Convert.ToHexString(SHA256.HashData([])), "text/plain", 0)]);
            var session = new TransferSession(sessionId, TransferDirection.Receive, TransferMode.Handoff, peerId,
                TransferSessionState.AwaitingApproval, DateTimeOffset.UtcNow, null, 1, 0, 0, null);
            await repository.CreateSessionAsync(session);
            var lease = await leases.AcquireAsync("droplink-receive", "transfers/consent", sensitivePlaintext: false);
            var receiveType = typeof(DropLinkHost).GetNestedType("ReceiveTransfer", BindingFlags.NonPublic)!;
            var receive = Activator.CreateInstance(receiveType, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null, args: [session, manifest, lease.RootPath, Path.Combine(paths.Root, "received"), lease], culture: null)!;
            var sessions = typeof(DropLinkHost).GetField("_sessions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
            Assert.IsTrue((bool)sessions.GetType().GetMethod("TryAdd")!.Invoke(sessions, [sessionId, receive])!);

            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            await using var app = builder.Build();
            app.Use(async (context, next) =>
            {
                // This request has already authenticated as the original sender. The
                // route must still require the receiver's independent consent.
                context.Items[DropLinkAuthenticationMiddleware.AuthenticatedPeerContextKey] = peerId;
                await next(context);
            });
            host.MapRoutes(app);
            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            using var remoteApproval = await client.PostAsJsonAsync(DropLinkProtocolRoutes.TransferAccept(sessionId), new TransferAcceptRequest(true));
            Assert.AreEqual(HttpStatusCode.Forbidden, remoteApproval.StatusCode);
            Assert.AreEqual(TransferSessionState.AwaitingApproval, ((TransferSession)receiveType.GetProperty("Session")!.GetValue(receive)!).State);
            Assert.IsTrue(await host.ApproveIncomingTransferAsync(sessionId, true));
            Assert.AreEqual(TransferSessionState.Accepted, ((TransferSession)receiveType.GetProperty("Session")!.GetValue(receive)!).State);

            // A receiver can also approve automatically from its local offer event.
            // Host startup's Windows identity work is outside this route test.
            typeof(DropLinkHost).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(host, DropLinkHost.HostLifecycleState.Running);
            var locallyApproved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Func<IncomingTransferOffer, CancellationToken, Task> approveLocally = async (offer, cancellationToken) =>
            {
                locallyApproved.TrySetResult(await host.ApproveIncomingTransferAsync(offer.SessionId, true, cancellationToken));
            };
            host.TransferOffered += approveLocally;
            var automaticId = Guid.NewGuid();
            var automaticManifest = TransferManifestPolicy.Create(automaticId, manifest.Items);
            using var automaticOffer = await client.PostAsJsonAsync(DropLinkProtocolRoutes.TransferOffers,
                new TransferOfferRequest(peerId, automaticManifest));
            Assert.AreEqual(HttpStatusCode.OK, automaticOffer.StatusCode);
            Assert.IsTrue(await locallyApproved.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            var automaticStatus = await client.GetFromJsonAsync<TransferStatusResponse>(DropLinkProtocolRoutes.TransferStatus(automaticId));
            Assert.AreEqual(TransferSessionState.Accepted, automaticStatus!.State);

            var canceledId = Guid.NewGuid();
            // No local handler approves this second offer; the sender can reject it.
            host.TransferOffered -= approveLocally;
            using var pendingOffer = await client.PostAsJsonAsync(DropLinkProtocolRoutes.TransferOffers,
                new TransferOfferRequest(peerId, TransferManifestPolicy.Create(canceledId, manifest.Items)));
            Assert.AreEqual(HttpStatusCode.OK, pendingOffer.StatusCode);
            using var senderRejection = await client.PostAsJsonAsync(DropLinkProtocolRoutes.TransferAccept(canceledId), new TransferAcceptRequest(false));
            Assert.AreEqual(HttpStatusCode.OK, senderRejection.StatusCode);
            var canceledStatus = await client.GetFromJsonAsync<TransferStatusResponse>(DropLinkProtocolRoutes.TransferStatus(canceledId));
            Assert.AreEqual(TransferSessionState.Rejected, canceledStatus!.State);
            await app.StopAsync();
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(paths.Root)) Directory.Delete(paths.Root, recursive: true);
        }
    }
}
