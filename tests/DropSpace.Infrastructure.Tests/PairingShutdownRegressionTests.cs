using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DropSpace.Core.Transfer;
using DropSpace.Infrastructure.Network;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class PairingShutdownRegressionTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task IdentityLookupFinishingAfterShutdownCannotReturnAHandshakeOrRetainPendingState(bool accept)
    {
        var paths = new AppStoragePaths(Path.Combine(Path.GetTempPath(), "DropSpace-tests", Guid.NewGuid().ToString("N")));
        var identities = new DeviceIdentityStore(paths);
        var pairing = new DropLinkPairingService(identities, new DeviceSecretStore(paths));
        var gate = (SemaphoreSlim)typeof(DeviceIdentityStore)
            .GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(identities)!;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=DropSpace-shutdown-test", key, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var identity = new DeviceIdentity(Guid.NewGuid(), "Local", DevicePlatform.Windows,
            certificate, new string('a', 64));
        await gate.WaitAsync();
        Task operation = accept ? pairing.AcceptHelloAsync(RemoteHello(), PeerCapability.HandoffFiles)
            : CreateAndDisposeHelloAsync(pairing);
        Assert.IsFalse(operation.IsCompleted);

        await pairing.DisposeAsync();
        // Supply a real cached identity only after shutdown, avoiding Windows DPAPI activation.
        typeof(DeviceIdentityStore).GetField("_cached", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(identities, identity);
        gate.Release();
        var rejected = false;
        try { await operation.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (ObjectDisposedException) { rejected = true; }

        Assert.IsTrue(rejected, "A lookup already waiting at shutdown must observe disposal before returning a handshake.");
        Assert.AreEqual(0, pairing.PendingCount, "No session may be admitted after shutdown has released pending state.");
    }

    private static async Task CreateAndDisposeHelloAsync(DropLinkPairingService pairing)
    {
        using var handshake = await pairing.CreateHelloAsync(PeerCapability.HandoffFiles);
    }

    private static PairingHello RemoteHello()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return new PairingHello(DropLinkProtocolVersion.V1, Guid.NewGuid(), "Remote", DevicePlatform.Windows,
            PeerCapability.HandoffFiles, new string('b', 64),
            Convert.ToBase64String(key.PublicKey.ExportSubjectPublicKeyInfo()),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(DropLinkProtocolPolicy.PairingNonceBytes)));
    }
}
