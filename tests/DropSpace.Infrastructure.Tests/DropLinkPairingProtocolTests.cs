using System.Security.Cryptography;
using DropSpace.Core.Transfer;
using DropSpace.Infrastructure.Network;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class DropLinkPairingProtocolTests
{
    [TestMethod]
    public void SasIsIndependentOfInitiatorOrdering()
    {
        var first = Hello(Guid.Parse("11111111-1111-1111-1111-111111111111"), "first", 0x11);
        var second = Hello(Guid.Parse("22222222-2222-2222-2222-222222222222"), "second", 0x22);
        var secret = SHA256.HashData(new byte[] { 1, 2, 3, 4 });

        Assert.AreEqual(
            DropLinkPairingService.ComputeSas(secret, first, second),
            DropLinkPairingService.ComputeSas(secret, second, first));
    }

    [TestMethod]
    public void PairingStateIncludesBilateralAndTerminalStates()
    {
        Assert.AreEqual(1, (int)PairingState.Created);
        Assert.AreEqual(2, (int)PairingState.HelloExchanged);
        Assert.AreEqual(3, (int)PairingState.AwaitingLocalSasConfirmation);
        Assert.AreEqual(4, (int)PairingState.LocalConfirmed);
        Assert.AreEqual(5, (int)PairingState.RemoteConfirmed);
        Assert.AreEqual(6, (int)PairingState.Trusted);
        Assert.AreEqual(7, (int)PairingState.Rejected);
        Assert.AreEqual(8, (int)PairingState.Expired);
        Assert.AreEqual(9, (int)PairingState.Cancelled);
        Assert.AreEqual(10, (int)PairingState.Failed);
    }

    private static PairingHello Hello(Guid id, string name, byte nonceByte)
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        return new PairingHello(
            DropLinkProtocolVersion.V1,
            id,
            name,
            DevicePlatform.Windows,
            PeerCapability.HandoffText,
            new string(name[0], 64),
            Convert.ToBase64String(key.PublicKey.ExportSubjectPublicKeyInfo()),
            Convert.ToBase64String(Enumerable.Repeat(nonceByte, 32).ToArray()));
    }
}
