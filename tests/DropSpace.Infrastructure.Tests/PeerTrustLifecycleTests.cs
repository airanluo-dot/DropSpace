using DropSpace.Core.Transfer;
using DropSpace.Infrastructure.Data;
using DropSpace.Infrastructure.Network;
using DropSpace.Infrastructure.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class PeerTrustLifecycleTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), "DropSpace-peer-tests", Guid.NewGuid().ToString("N"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public async Task PairingPendingCanBecomeTrustedAndBlockedPeersCannotRestartPairing()
    {
        var paths = new AppStoragePaths(_root);
        var database = new SqliteDatabase(paths, NullLogger<SqliteDatabase>.Instance);
        var transfers = new TransferRepository(database);
        var peerId = Guid.NewGuid();
        var descriptor = new DeviceDescriptor(
            DropLinkProtocolVersion.V1,
            peerId,
            "Trusted Windows",
            DevicePlatform.Windows,
            PeerCapability.HandoffFiles,
            new string('a', 64),
            new Uri("https://192.168.50.20:47831/"));

        await transfers.EnsurePairingPendingAsync(descriptor);
        Assert.AreEqual(PeerTrustState.PairingPending, (await transfers.GetPeersAsync())[0].TrustState);

        var trusted = new PeerDevice(
            peerId,
            descriptor.DisplayName,
            descriptor.Platform,
            descriptor.IdentityFingerprint,
            descriptor.Capabilities,
            PeerTrustState.Trusted,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        await transfers.UpsertPeerAsync(trusted, peerId.ToString("N"));
        Assert.AreEqual(PeerTrustState.Trusted, (await transfers.GetPeersAsync())[0].TrustState);

        await transfers.UpdatePeerTrustStateAsync(peerId, PeerTrustState.Blocked);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => transfers.EnsurePairingPendingAsync(descriptor));
        Assert.AreEqual(PeerTrustState.Blocked, (await transfers.GetPeersAsync())[0].TrustState);
    }
}
