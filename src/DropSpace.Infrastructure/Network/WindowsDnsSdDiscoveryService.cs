using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using DropSpace.Core.Transfer;

namespace DropSpace.Infrastructure.Network;

/// <summary>
/// Small, dependency-free DNS-SD adapter for the Windows LAN profile. It uses the standard
/// mDNS multicast address and keeps TXT records limited to the public device descriptor.
/// </summary>
public sealed class WindowsDnsSdDiscoveryService : IAsyncDisposable
{
    private static readonly IPAddress MulticastAddress = IPAddress.Parse("224.0.0.251");
    private const int MulticastPort = 5353;
    private const string ServiceType = "_dropspace._tcp.local";
    private readonly object _gate = new();
    private DnsRegistration? _registration;

    public async Task<IDisposable> RegisterAsync(DeviceDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        lock (_gate)
        {
            if (_registration is not null) return _registration;
        }

        var socket = new UdpClient(AddressFamily.InterNetwork);
        socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Client.Bind(new IPEndPoint(IPAddress.Any, MulticastPort));
        socket.JoinMulticastGroup(MulticastAddress);
        var registration = new DnsRegistration(socket, descriptor, GetLocalHostName(descriptor.DeviceId));
        lock (_gate) _registration = registration;
        try
        {
            await registration.StartAsync(cancellationToken).ConfigureAwait(false);
            return registration;
        }
        catch
        {
            lock (_gate)
            {
                if (ReferenceEquals(_registration, registration)) _registration = null;
            }
            await registration.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<DeviceDescriptor>> BrowseAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        using var socket = new UdpClient(AddressFamily.InterNetwork);
        socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        socket.JoinMulticastGroup(MulticastAddress);
        var query = BuildQuery();
        await socket.SendAsync(query, query.Length, new IPEndPoint(MulticastAddress, MulticastPort), cancellationToken).ConfigureAwait(false);
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        var results = new Dictionary<Guid, DeviceDescriptor>();
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = deadline - DateTimeOffset.UtcNow;
            var receiveTask = socket.ReceiveAsync(cancellationToken).AsTask();
            var completed = await Task.WhenAny(receiveTask, Task.Delay(remaining, cancellationToken)).ConfigureAwait(false);
            if (completed != receiveTask) break;
            var packet = await receiveTask.ConfigureAwait(false);
            foreach (var descriptor in ParseAnnouncement(packet.Buffer, packet.RemoteEndPoint.Address)) results[descriptor.DeviceId] = descriptor;
        }

        return results.Values.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        DnsRegistration? registration;
        lock (_gate) { registration = _registration; _registration = null; }
        if (registration is not null) await registration.DisposeAsync().ConfigureAwait(false);
    }

    private static byte[] BuildQuery()
    {
        using var stream = new MemoryStream();
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 1);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        WriteName(stream, ServiceType);
        WriteUInt16(stream, 12);
        WriteUInt16(stream, 1);
        return stream.ToArray();
    }

    private static IEnumerable<DeviceDescriptor> ParseAnnouncement(byte[] packet, IPAddress address)
    {
        if (packet.Length < 12) yield break;
        var answers = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(6, 2));
        var offset = 12;
        var instances = new Dictionary<string, (string? Id, string? Name, DevicePlatform Platform, PeerCapability Caps, string? Fingerprint, int Port)>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < answers && offset < packet.Length; index++)
        {
            var name = ReadName(packet, ref offset);
            if (offset + 10 > packet.Length) yield break;
            var type = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset, 2));
            var dataLength = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset + 8, 2));
            offset += 10;
            if (offset + dataLength > packet.Length) yield break;
            if (type == 16 && name is not null)
            {
                var text = ParseTxt(packet.AsSpan(offset, dataLength));
                if (text.TryGetValue("id", out var id) && Guid.TryParse(id, out var guid))
                {
                    var current = instances.GetValueOrDefault(name);
                    instances[name] = (id, text.GetValueOrDefault("name"), ParsePlatform(text.GetValueOrDefault("platform")), ParseCaps(text.GetValueOrDefault("caps")), text.GetValueOrDefault("fp"), current.Port);
                }
            }
            else if (type == 33 && name is not null && dataLength >= 6)
            {
                var port = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset + 4, 2));
                var current = instances.GetValueOrDefault(name);
                instances[name] = (current.Id, current.Name, current.Platform, current.Caps, current.Fingerprint, port);
            }
            offset += dataLength;
        }

        foreach (var entry in instances.Values)
        {
            if (!Guid.TryParse(entry.Id, out var id) || entry.Port is < 1 or > 65535 || string.IsNullOrWhiteSpace(entry.Fingerprint)) continue;
            yield return new DeviceDescriptor(DropLinkProtocolVersion.V1, id, entry.Name ?? "DropSpace device", entry.Platform, entry.Caps, entry.Fingerprint, new Uri($"https://{address}:{entry.Port}/"));
        }
    }

    private static byte[] BuildAnnouncement(DeviceDescriptor descriptor, string hostName)
    {
        var instance = string.Concat(SafeLabel(descriptor.DisplayName), "._dropspace._tcp.local");
        var host = string.Concat(hostName, ".local");
        var address = GetLocalAddress();
        using var stream = new MemoryStream();
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0x8400);
        WriteUInt16(stream, 4);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        WritePtr(stream, ServiceType, instance);
        WriteSrv(stream, instance, host, descriptor.Endpoint.Port);
        WriteTxt(stream, instance, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["v"] = descriptor.Protocol.ToString(),
            ["id"] = descriptor.DeviceId.ToString("D"),
            ["name"] = SafeLabel(descriptor.DisplayName),
            ["platform"] = descriptor.Platform.ToString().ToLowerInvariant(),
            ["caps"] = ((int)descriptor.Capabilities).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["fp"] = descriptor.IdentityFingerprint,
        });
        WriteA(stream, host, address);
        return stream.ToArray();
    }

    private static void WritePtr(Stream stream, string name, string value)
    {
        WriteRecordHeader(stream, name, 12, 120, out var lengthPosition);
        using var data = new MemoryStream(); WriteName(data, value);
        WriteRdata(stream, lengthPosition, data.ToArray());
    }

    private static void WriteSrv(Stream stream, string name, string host, int port)
    {
        WriteRecordHeader(stream, name, 33, 120, out var lengthPosition);
        using var data = new MemoryStream(); WriteUInt16(data, 0); WriteUInt16(data, 0); WriteUInt16(data, checked((ushort)port)); WriteName(data, host);
        WriteRdata(stream, lengthPosition, data.ToArray());
    }

    private static void WriteTxt(Stream stream, string name, IReadOnlyDictionary<string, string> values)
    {
        WriteRecordHeader(stream, name, 16, 120, out var lengthPosition);
        using var data = new MemoryStream();
        foreach (var pair in values)
        {
            var value = Encoding.UTF8.GetBytes(string.Concat(pair.Key, "=", pair.Value));
            if (value.Length is > 0 and <= 255)
            {
                data.WriteByte((byte)value.Length);
                data.Write(value);
            }
        }
        WriteRdata(stream, lengthPosition, data.ToArray());
    }

    private static void WriteA(Stream stream, string name, IPAddress address)
    {
        WriteRecordHeader(stream, name, 1, 120, out var lengthPosition);
        WriteRdata(stream, lengthPosition, address.GetAddressBytes());
    }

    private static void WriteRecordHeader(Stream stream, string name, ushort type, uint ttl, out long lengthPosition)
    {
        WriteName(stream, name); WriteUInt16(stream, type); WriteUInt16(stream, 1); WriteUInt32(stream, ttl); lengthPosition = stream.Position; WriteUInt16(stream, 0);
    }

    private static void WriteRdata(Stream stream, long lengthPosition, byte[] bytes)
    {
        var end = stream.Position; stream.Position = lengthPosition; WriteUInt16(stream, checked((ushort)bytes.Length)); stream.Position = end; stream.Write(bytes);
    }

    private static void WriteName(Stream stream, string name)
    {
        foreach (var label in name.TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.UTF8.GetBytes(label); if (bytes.Length is 0 or > 63) throw new InvalidDataException("DNS-SD label is invalid."); stream.WriteByte((byte)bytes.Length); stream.Write(bytes);
        }
        stream.WriteByte(0);
    }

    private static string? ReadName(byte[] bytes, ref int offset)
    {
        var labels = new List<string>(); var guard = 0; var local = offset;
        while (local < bytes.Length && guard++ < 32)
        {
            var length = bytes[local++]; if (length == 0) { if (offset == local - 1) offset = local; break; }
            if ((length & 0xC0) == 0xC0)
            {
                if (local >= bytes.Length) return null; var pointer = ((length & 0x3F) << 8) | bytes[local++]; if (offset == local) offset = local; local = pointer; continue;
            }
            if (length > 63 || local + length > bytes.Length) return null;
            labels.Add(Encoding.UTF8.GetString(bytes, local, length)); local += length; if (offset == local - length - 1) offset = local;
        }
        return labels.Count == 0 ? string.Empty : string.Join('.', labels);
    }

    private static Dictionary<string, string> ParseTxt(ReadOnlySpan<byte> data)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); var offset = 0;
        while (offset < data.Length)
        {
            var length = data[offset++]; if (offset + length > data.Length) break; var text = Encoding.UTF8.GetString(data.Slice(offset, length)); offset += length; var separator = text.IndexOf('='); if (separator > 0) result[text[..separator]] = text[(separator + 1)..];
        }
        return result;
    }

    private static DevicePlatform ParsePlatform(string? value) => Enum.TryParse<DevicePlatform>(value, true, out var platform) ? platform : DevicePlatform.Unknown;
    private static PeerCapability ParseCaps(string? value) => int.TryParse(value, out var caps) ? (PeerCapability)caps : PeerCapability.None;
    private static string SafeLabel(string value) => new((value ?? "DropSpace").Where(character => char.IsLetterOrDigit(character) || character is '-' or '_').Take(48).ToArray());
    private static string GetLocalHostName(Guid id) => string.Concat("dropspace-", id.ToString("N"));
    private static IPAddress GetLocalAddress() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(network => network.OperationalStatus == OperationalStatus.Up && network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(network => network.GetIPProperties().UnicastAddresses)
        .Select(address => address.Address)
        .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && IsPrivate(address))
        ?? throw new InvalidOperationException("DropLink discovery requires an active private-network IPv4 address.");

    private static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && (bytes[0] == 10 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31);
    }
    private static void WriteUInt16(Stream stream, ushort value) { Span<byte> bytes = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(bytes, value); stream.Write(bytes); }
    private static void WriteUInt32(Stream stream, uint value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value); stream.Write(bytes); }

    private sealed class DnsRegistration(UdpClient socket, DeviceDescriptor descriptor, string hostName) : IAsyncDisposable, IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private Task? _loop;
        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var packet = BuildAnnouncement(descriptor, hostName);
            await socket.SendAsync(packet, packet.Length, new IPEndPoint(MulticastAddress, MulticastPort), cancellationToken).ConfigureAwait(false);
            _loop = Task.Run(async () =>
            {
                while (!_cancellation.IsCancellationRequested)
                {
                    try
                    {
                        var result = await socket.ReceiveAsync(_cancellation.Token).ConfigureAwait(false);
                        if (result.Buffer.Length >= 12)
                        {
                            var response = BuildAnnouncement(descriptor, hostName);
                            await socket.SendAsync(response, response.Length, result.RemoteEndPoint).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (SocketException) { break; }
                }
            }, CancellationToken.None);
        }
        public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
        public async ValueTask DisposeAsync()
        {
            _cancellation.Cancel(); socket.Dispose(); if (_loop is not null) { try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { } } _cancellation.Dispose();
        }
    }
}
