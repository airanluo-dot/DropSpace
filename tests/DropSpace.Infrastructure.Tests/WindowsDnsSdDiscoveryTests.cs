using System.Buffers.Binary;
using System.Net;
using DropSpace.Core.Transfer;
using DropSpace.Infrastructure.Network;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class WindowsDnsSdDiscoveryTests
{
    [TestMethod]
    public void QueryUsesTheStandardMdnsServiceAndPort()
    {
        var query = WindowsDnsSdDiscoveryService.BuildQuery();

        Assert.AreEqual(0, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(0, 2)));
        Assert.AreEqual(1, BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(4, 2)));
        Assert.AreEqual(WindowsDnsSdDiscoveryService.MulticastPort, 5353);
        Assert.AreEqual("224.0.0.251", WindowsDnsSdDiscoveryService.MulticastAddress.ToString());
    }

    [TestMethod]
    public void AnnouncementHasNoQuestionsAndParsesAllFourRecords()
    {
        var id = Guid.NewGuid();
        var descriptor = new DeviceDescriptor(
            DropLinkProtocolVersion.V1,
            id,
            "Office Windows",
            DevicePlatform.Windows,
            PeerCapability.HandoffFiles | PeerCapability.HandoffText,
            new string('a', 64),
            new Uri("https://192.168.50.21:47831/"));

        var packet = WindowsDnsSdDiscoveryService.BuildAnnouncement(descriptor, "dropspace-office", IPAddress.Parse("192.168.50.21"));

        Assert.AreEqual(0, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4, 2)));
        Assert.AreEqual(4, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(6, 2)));
        var parsed = WindowsDnsSdDiscoveryService.ParseAnnouncement(packet);
        var result = Assert.Single(parsed);
        Assert.AreEqual(id, result.DeviceId);
        Assert.AreEqual(descriptor.DisplayName, result.DisplayName);
        Assert.AreEqual(descriptor.Endpoint.Port, result.Endpoint.Port);
        Assert.AreEqual(descriptor.IdentityFingerprint, result.IdentityFingerprint);
        Assert.AreEqual("https://192.168.50.21:47831/", result.Endpoint.ToString());
    }

    [TestMethod]
    public void TruncatedOrNonResponseAnnouncementsFailClosed()
    {
        var descriptor = new DeviceDescriptor(
            DropLinkProtocolVersion.V1,
            Guid.NewGuid(),
            "Office Windows",
            DevicePlatform.Windows,
            PeerCapability.HandoffFiles,
            new string('b', 64),
            new Uri("https://10.0.0.4:47831/"));
        var packet = WindowsDnsSdDiscoveryService.BuildAnnouncement(descriptor, "dropspace-office", IPAddress.Parse("10.0.0.4"));

        Assert.IsEmpty(WindowsDnsSdDiscoveryService.ParseAnnouncement(packet[..^1]));
        packet[2] = 0;
        packet[3] = 0;
        Assert.IsEmpty(WindowsDnsSdDiscoveryService.ParseAnnouncement(packet));
    }
}
