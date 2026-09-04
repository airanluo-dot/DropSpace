using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DropSpace.Infrastructure.Network;

public sealed record LocalNetworkCandidate(string Id, IPAddress Address, bool Operational,
    bool Physical, bool HasGateway, long Speed);

/// <summary>One deterministic adapter policy for all LAN listeners and advertisements.</summary>
public static class LocalNetworkInterfaceResolver
{
    public static IPAddress Resolve() => Select(NetworkInterface.GetAllNetworkInterfaces().SelectMany(network =>
    {
        var properties = network.GetIPProperties();
        var physical = network.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 &&
            !new[] { "virtual", "hyper-v", "wsl", "vpn", "tunnel", "tap", "tun" }
                .Any(marker => (network.Name + " " + network.Description).Contains(marker, StringComparison.OrdinalIgnoreCase));
        return properties.UnicastAddresses.Select(address => new LocalNetworkCandidate(network.Id, address.Address,
            network.OperationalStatus == OperationalStatus.Up, physical,
            properties.GatewayAddresses.Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork &&
                !gateway.Address.Equals(IPAddress.Any)), network.Speed));
    })).Address;

    public static LocalNetworkCandidate Select(IEnumerable<LocalNetworkCandidate> candidates) => candidates
        .Where(candidate => candidate.Operational && IsPrivate(candidate.Address))
        .OrderByDescending(candidate => candidate.Physical)
        .ThenByDescending(candidate => candidate.HasGateway)
        .ThenByDescending(candidate => candidate.Speed)
        .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
        .ThenBy(candidate => candidate.Address.ToString(), StringComparer.Ordinal)
        .FirstOrDefault() ?? throw new InvalidOperationException("No reachable private IPv4 adapter is available.");

    public static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && (bytes[0] == 10 || bytes[0] == 192 && bytes[1] == 168 ||
            bytes[0] == 172 && bytes[1] is >= 16 and <= 31);
    }
}
