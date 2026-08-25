using System.Net.NetworkInformation;

namespace DropSpace.Infrastructure.Network;

public enum ReceiveFirewallState
{
    Unknown = 0,
    Available = 1,
    Blocked = 2,
    NotAuthorized = 3,
}

public sealed record FirewallCapability(ReceiveFirewallState State, bool CanReceive, string? Reason);

public sealed class FirewallCapabilityService
{
    public FirewallCapability CheckInboundCapability(int port)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var hasPrivateInterface = NetworkInterface.GetAllNetworkInterfaces().Any(network =>
            network.OperationalStatus == OperationalStatus.Up && network.NetworkInterfaceType is not NetworkInterfaceType.Loopback &&
            network.GetIPProperties().UnicastAddresses.Any(address => address.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork));
        return hasPrivateInterface
            ? new FirewallCapability(ReceiveFirewallState.Unknown, true, "Windows Firewall state must be verified by the explicit user action.")
            : new FirewallCapability(ReceiveFirewallState.Blocked, false, "No active private-network interface was found.");
    }

    public Task<FirewallCapability> EnablePrivateProfileAsync(int port, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = port;
        return Task.FromResult(new FirewallCapability(ReceiveFirewallState.NotAuthorized, false, "Enabling a Windows Firewall rule requires the explicit elevated helper flow."));
    }
}
