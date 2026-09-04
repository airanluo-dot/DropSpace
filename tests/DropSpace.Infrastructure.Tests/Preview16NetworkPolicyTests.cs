using System.Net;
using DropSpace.Infrastructure.Network;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class Preview16NetworkPolicyTests
{
    [TestMethod]
    [DataRow("Hyper-V")]
    [DataRow("WSL")]
    [DataRow("VPN")]
    public void PhysicalGatewayWinsOverVirtualAdapter(string virtualName)
    {
        var wifi = new LocalNetworkCandidate("wifi", IPAddress.Parse("192.168.1.2"), true, true, true, 100);
        var other = new LocalNetworkCandidate(virtualName, IPAddress.Parse("172.20.0.1"), true, false, true, 1000);
        Assert.AreEqual(wifi, LocalNetworkInterfaceResolver.Select([other, wifi]));
        Assert.AreEqual(wifi, LocalNetworkInterfaceResolver.Select([wifi, other]));
    }

    [TestMethod]
    public void SingleAdaptersAndTiesAreDeterministic()
    {
        var wifi = new LocalNetworkCandidate("wifi", IPAddress.Parse("192.168.1.2"), true, true, true, 100);
        var ethernet = new LocalNetworkCandidate("ethernet", IPAddress.Parse("192.168.1.3"), true, true, true, 1000);
        Assert.AreEqual(wifi, LocalNetworkInterfaceResolver.Select([wifi]));
        Assert.AreEqual(ethernet, LocalNetworkInterfaceResolver.Select([ethernet]));
        Assert.AreEqual(ethernet, LocalNetworkInterfaceResolver.Select([wifi, ethernet]));
        Assert.ThrowsExactly<InvalidOperationException>(() => LocalNetworkInterfaceResolver.Select([wifi with { Operational = false }]));
        Assert.ThrowsExactly<InvalidOperationException>(() => LocalNetworkInterfaceResolver.Select([wifi with { Address = IPAddress.Loopback }]));
    }
}
