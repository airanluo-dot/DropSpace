using System.Net;
using System.Reflection;
using System.Runtime.Versioning;
using DropSpace.Core.Transfer;
using DropSpace.Infrastructure.Data;
using DropSpace.Infrastructure.Network;
using DropSpace.Infrastructure.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class PairingRoundTripNativeTests
{
    [TestMethod]
    [TestCategory("NativeSmoke")]
    public async Task RealClientAndHostAgreeOnResponderIdentityAfterBilateralConfirmation()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Device stores require Windows DPAPI."); return; }
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-pairing-roundtrip", Guid.NewGuid().ToString("N"));
        try
        {
            var serverPaths = new AppStoragePaths(Path.Combine(root, "server"));
            var clientPaths = new AppStoragePaths(Path.Combine(root, "client"));
            var serverIdentity = new DeviceIdentityStore(serverPaths);
            var clientIdentity = new DeviceIdentityStore(clientPaths);
            var serverSecrets = new DeviceSecretStore(serverPaths);
            var clientSecrets = new DeviceSecretStore(clientPaths);
            await using var serverPairing = new DropLinkPairingService(serverIdentity, serverSecrets);
            await using var clientPairing = new DropLinkPairingService(clientIdentity, clientSecrets);
            var serverTransfers = new TransferRepository(new SqliteDatabase(serverPaths, NullLogger<SqliteDatabase>.Instance));
            var clientTransfers = new TransferRepository(new SqliteDatabase(clientPaths, NullLogger<SqliteDatabase>.Instance));
            await using var host = new DropLinkHost(serverIdentity, serverSecrets, serverPairing, serverTransfers,
                NullLogger<DropLinkHost>.Instance, new DropLinkNonceCache(),
                new StagingLeaseStore(serverPaths, NullLogger<StagingLeaseStore>.Instance));
            var identity = await serverIdentity.GetOrCreateAsync();
            var localConfirmations = 0;
            host.PairingOffered += (_, _) => { localConfirmations++; return Task.FromResult(true); };
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
                listen => listen.UseHttps(identity.Certificate)));
            await using var app = builder.Build();
            host.ConfigureAuthenticationPipeline(app);
            host.MapRoutes(app);
            await app.StartAsync();
            var endpoint = new Uri(app.Urls.Single());
            typeof(DropLinkHost).GetField("_identity", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, identity);
            typeof(DropLinkHost).GetField("_endpoint", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, endpoint);
            var client = new DropLinkClient(clientIdentity, clientSecrets, clientPairing, clientTransfers);
            var callerConfirmations = 0;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var peer = await client.PairAsync(endpoint, identity.Fingerprint, PeerCapability.ClipboardText,
                (_, _) => { callerConfirmations++; return Task.FromResult(true); }, deadline.Token);
            Assert.AreEqual(identity.DeviceId, peer.Id, "The confirmation identifies the responder from the caller's perspective.");
            Assert.AreEqual(PeerTrustState.Trusted, peer.TrustState);
            Assert.AreEqual(1, localConfirmations);
            Assert.AreEqual(1, callerConfirmations);
            var callerIdentity = await clientIdentity.GetOrCreateAsync();
            Assert.AreEqual(PeerTrustState.Trusted, await serverTransfers.GetPeerTrustStateAsync(callerIdentity.DeviceId));
            Assert.AreEqual(PeerTrustState.Trusted, await clientTransfers.GetPeerTrustStateAsync(identity.DeviceId));
            var serverKey = await serverSecrets.GetAsync(callerIdentity.DeviceId);
            var clientKey = await clientSecrets.GetAsync(identity.DeviceId);
            try
            {
                Assert.IsNotNull(serverKey);
                Assert.IsNotNull(clientKey);
                CollectionAssert.AreEqual(serverKey, clientKey);
            }
            finally
            {
                if (serverKey is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(serverKey);
                if (clientKey is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(clientKey);
            }
            await app.StopAsync();
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
