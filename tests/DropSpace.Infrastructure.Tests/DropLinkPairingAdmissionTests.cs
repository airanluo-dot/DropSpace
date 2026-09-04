using System.Security.Cryptography;
using System.Runtime.Versioning;
using DropSpace.Core.Transfer;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class DropLinkPairingAdmissionTests
{
    [TestMethod]
    public async Task PendingPairingsAreBoundedPerRemoteAddress()
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-tests", Guid.NewGuid().ToString("N"));
        var paths = new DropSpace.Infrastructure.Storage.AppStoragePaths(root);
        var identities = new DeviceIdentityStore(paths);
        var secrets = new DeviceSecretStore(paths);
        var pairing = new DropLinkPairingService(identities, secrets);
        const string remoteAddress = "192.168.1.50";

        try
        {
            for (var index = 0; index < DropLinkPairingPolicy.MaximumPendingPerAddress; index++)
            {
                var offer = await pairing.AcceptHelloAsync(
                    CreateHello(index),
                    PeerCapability.HandoffFiles,
                    remoteAddress: remoteAddress);
                Assert.AreNotEqual(Guid.Empty, offer.SessionId);
            }

            PairingAdmissionException? rejection = null;
            try
            {
                await pairing.AcceptHelloAsync(
                    CreateHello(DropLinkPairingPolicy.MaximumPendingPerAddress),
                    PeerCapability.HandoffFiles,
                    remoteAddress: remoteAddress);
            }
            catch (PairingAdmissionException exception)
            {
                rejection = exception;
            }

            Assert.IsNotNull(rejection);
            Assert.AreEqual("pairing-address-capacity", rejection!.ErrorCategory);
            Assert.AreEqual(DropLinkPairingPolicy.MaximumPendingPerAddress, pairing.PendingCount);
        }
        finally
        {
            await pairing.DisposeAsync();
            TryDelete(root);
        }
    }

    private static PairingHello CreateHello(int index)
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return new PairingHello(
            DropLinkProtocolVersion.V1,
            Guid.NewGuid(),
            string.Concat("Peer-", index),
            DevicePlatform.Windows,
            PeerCapability.HandoffFiles,
            new string('a', 64),
            Convert.ToBase64String(key.PublicKey.ExportSubjectPublicKeyInfo()),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(DropLinkProtocolPolicy.PairingNonceBytes)));
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
