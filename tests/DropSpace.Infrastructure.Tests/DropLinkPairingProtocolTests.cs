using System.Security.Cryptography;
using System.Runtime.Versioning;
using DropSpace.Core.Transfer;
using DropSpace.Infrastructure.Network;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
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
        var expected = new[]
        {
            PairingState.None,
            PairingState.Created,
            PairingState.HelloExchanged,
            PairingState.AwaitingLocalSasConfirmation,
            PairingState.LocalConfirmed,
            PairingState.RemoteConfirmed,
            PairingState.Trusted,
            PairingState.Rejected,
            PairingState.Expired,
            PairingState.Cancelled,
            PairingState.Failed,
        };

        var actual = Enum.GetValues<PairingState>()
            .Distinct()
            .OrderBy(value => (int)value)
            .ToArray();

        CollectionAssert.AreEqual(expected, actual);
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
