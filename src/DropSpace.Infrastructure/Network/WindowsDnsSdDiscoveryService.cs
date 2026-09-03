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
    public static readonly IPAddress MulticastAddress = IPAddress.Parse("224.0.0.251");
    public static readonly int MulticastPort = 5353;
    public const string ServiceType = "_dropspace._tcp.local";
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly object _gate = new();
    private DnsRegistration? _registration;

    public async Task<IDisposable> RegisterAsync(DeviceDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        lock (_gate)
        {
            if (_registration is not null) return _registration;
        }

        var socket = CreateMulticastSocket();
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
        using var socket = CreateMulticastSocket();
        socket.JoinMulticastGroup(MulticastAddress);
        var query = BuildQuery();
        await socket.SendAsync(query.AsMemory(), new IPEndPoint(MulticastAddress, MulticastPort), cancellationToken).ConfigureAwait(false);
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
            foreach (var descriptor in ParseAnnouncement(packet.Buffer))
            {
                // DeviceId is the stable identity. A device may answer more than once
                // while a browse is running, but must not create duplicate UI rows.
                results[descriptor.DeviceId] = descriptor;
            }
        }

        return results.Values.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        DnsRegistration? registration;
        lock (_gate) { registration = _registration; _registration = null; }
        if (registration is not null) await registration.DisposeAsync().ConfigureAwait(false);
    }

    public static byte[] BuildQuery()
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

    public static IReadOnlyList<DeviceDescriptor> ParseAnnouncement(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 12) return [];
        var bytes = packet.ToArray();
        var flags = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(2, 2));
        if ((flags & 0x8000) == 0) return [];

        var reader = new DnsReader(bytes);
        if (!reader.TrySkipQuestions(out var answers, out var authorities, out var additionals)) return [];
        var records = new List<DnsRecord>();
        for (var index = 0; index < answers + authorities + additionals; index++)
        {
            if (!reader.TryReadRecord(out var record)) return [];
            records.Add(record);
        }

        var serviceInstances = records
            .Where(record => record.Type == 12 && string.Equals(record.Name, NormalizeName(ServiceType), StringComparison.OrdinalIgnoreCase) && record.Ptr is not null)
            .Select(record => record.Ptr!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (serviceInstances.Count == 0) return [];

        var results = new List<DeviceDescriptor>();
        foreach (var instance in serviceInstances)
        {
            var srv = records.FirstOrDefault(record => record.Type == 33 && string.Equals(record.Name, instance, StringComparison.OrdinalIgnoreCase));
            var txt = records.FirstOrDefault(record => record.Type == 16 && string.Equals(record.Name, instance, StringComparison.OrdinalIgnoreCase));
            if (srv is null || txt is null || srv.Target is null || srv.Port is null || txt.Txt is null) continue;
            if (srv.Port is < 1 or > 65535) continue;

            var hostAddress = records.FirstOrDefault(record => record.Type == 1 &&
                string.Equals(record.Name, srv.Target, StringComparison.OrdinalIgnoreCase))?.Address;
            if (hostAddress is null || hostAddress.AddressFamily != AddressFamily.InterNetwork || !IsPrivate(hostAddress)) continue;

            var values = txt.Txt;
            if (!string.Equals(values.GetValueOrDefault("v"), DropLinkProtocolVersion.V1.ToString(), StringComparison.Ordinal)) continue;
            if (!Guid.TryParse(values.GetValueOrDefault("id"), out var id) || id == Guid.Empty) continue;
            var name = values.GetValueOrDefault("name");
            if (string.IsNullOrWhiteSpace(name) || name.Length > 64 || name.Any(char.IsControl)) continue;
            if (!Enum.TryParse<DevicePlatform>(values.GetValueOrDefault("platform"), true, out var platform) || platform == DevicePlatform.Unknown) continue;
            if (!int.TryParse(values.GetValueOrDefault("caps"), out var caps) || caps < 0) continue;
            var fingerprint = values.GetValueOrDefault("fp");
            if (fingerprint is null || fingerprint.Length != 64 || fingerprint.Any(character => !Uri.IsHexDigit(character))) continue;

            results.Add(new DeviceDescriptor(
                DropLinkProtocolVersion.V1,
                id,
                name,
                platform,
                (PeerCapability)caps,
                fingerprint.ToLowerInvariant(),
                new UriBuilder(Uri.UriSchemeHttps, hostAddress.ToString(), srv.Port.Value).Uri));
        }

        return results
            .GroupBy(descriptor => descriptor.DeviceId)
            .Select(group => group.Last())
            .ToArray();
    }

    public static byte[] BuildAnnouncement(DeviceDescriptor descriptor, string hostName, IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostName);
        if (!descriptor.Protocol.IsCompatibleWith(DropLinkProtocolVersion.V1) || descriptor.DeviceId == Guid.Empty ||
            descriptor.Platform != DevicePlatform.Windows ||
            descriptor.Endpoint is null || descriptor.Endpoint.Scheme != Uri.UriSchemeHttps || descriptor.Endpoint.Port is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(descriptor.IdentityFingerprint) || descriptor.IdentityFingerprint.Length != 64 || descriptor.IdentityFingerprint.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("The DNS-SD device descriptor is invalid.");
        }
        if (address.AddressFamily != AddressFamily.InterNetwork || !IsPrivate(address)) throw new InvalidDataException("DNS-SD requires a private IPv4 address.");
        var label = SafeLabel(descriptor.DisplayName);
        var instance = string.Concat(label, "-", descriptor.DeviceId.ToString("N")[..8], ".", ServiceType);
        var host = string.Concat(hostName.TrimEnd('.'), ".local");
        using var stream = new MemoryStream();
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0x8400);
        // This is an unsolicited answer: four answer RRs and no questions.
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 4);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        WritePtr(stream, ServiceType, instance);
        WriteSrv(stream, instance, host, descriptor.Endpoint.Port);
        WriteTxt(stream, instance, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["v"] = descriptor.Protocol.ToString(),
            ["id"] = descriptor.DeviceId.ToString("D"),
            ["name"] = SafeDisplayName(descriptor.DisplayName),
            ["platform"] = descriptor.Platform.ToString().ToLowerInvariant(),
            ["caps"] = ((int)descriptor.Capabilities).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["fp"] = descriptor.IdentityFingerprint.Replace(":", string.Empty, StringComparison.Ordinal).ToLowerInvariant(),
        });
        WriteA(stream, host, address);
        return stream.ToArray();
    }

    private static UdpClient CreateMulticastSocket()
    {
        var socket = new UdpClient(AddressFamily.InterNetwork);
        socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ExclusiveAddressUse, false);
        socket.Client.Bind(new IPEndPoint(IPAddress.Any, MulticastPort));
        return socket;
    }

    private static void WritePtr(Stream stream, string name, string value)
    {
        WriteRecordHeader(stream, name, 12, 120, out var lengthPosition);
        using var data = new MemoryStream(); WriteName(data, value);
        WriteRdata(stream, lengthPosition, data.ToArray());
    }

    private static void WriteSrv(Stream stream, string name, string host, int port)
    {
        if (port is < 1 or > 65535) throw new InvalidDataException("DNS-SD port is invalid.");
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
            var value = StrictUtf8.GetBytes(string.Concat(pair.Key, "=", pair.Value));
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
            var bytes = StrictUtf8.GetBytes(label);
            if (bytes.Length is 0 or > 63) throw new InvalidDataException("DNS-SD label is invalid.");
            stream.WriteByte((byte)bytes.Length); stream.Write(bytes);
        }
        stream.WriteByte(0);
    }

    private static bool TryReadQueryName(ReadOnlySpan<byte> bytes, ref int offset, out string name)
    {
        name = string.Empty;
        var labels = new List<string>();
        var cursor = offset;
        var jumped = false;
        var visited = new HashSet<int>();
        for (var guard = 0; guard < 64; guard++)
        {
            if (cursor < 0 || cursor >= bytes.Length || !visited.Add(cursor)) return false;
            var length = bytes[cursor++];
            if (length == 0)
            {
                if (!jumped) offset = cursor;
                name = string.Join('.', labels);
                return true;
            }
            if ((length & 0xC0) == 0xC0)
            {
                if (cursor >= bytes.Length) return false;
                var pointer = ((length & 0x3F) << 8) | bytes[cursor++];
                if (pointer >= bytes.Length) return false;
                if (!jumped) offset = cursor;
                cursor = pointer;
                jumped = true;
                continue;
            }
            if ((length & 0xC0) != 0 || length > 63 || cursor + length > bytes.Length) return false;
            try
            {
                labels.Add(StrictUtf8.GetString(bytes.Slice(cursor, length)));
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
            cursor += length;
        }
        return false;
    }

    private static string NormalizeName(string value) => value.TrimEnd('.').ToLowerInvariant();
    private static string SafeDisplayName(string value)
    {
        var characters = (value ?? string.Empty)
            .Where(character => !char.IsControl(character))
            .Take(48)
            .ToArray();
        while (characters.Length > 0)
        {
            var candidate = new string(characters).Trim();
            try
            {
                if (!string.IsNullOrWhiteSpace(candidate) && StrictUtf8.GetByteCount(candidate) <= 180) return candidate;
            }
            catch (EncoderFallbackException)
            {
                // Drop an incomplete surrogate or other invalid UTF-16 tail.
            }

            Array.Resize(ref characters, characters.Length - 1);
        }

        return "DropSpace";
    }
    private static string SafeLabel(string value)
    {
        var label = new string((value ?? string.Empty).Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_').Take(48).ToArray());
        return string.IsNullOrWhiteSpace(label) ? "dropspace" : label;
    }
    private static string GetLocalHostName(Guid id) => string.Concat("dropspace-", id.ToString("N"));
    private static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && (bytes[0] == 10 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31);
    }
    private static void WriteUInt16(Stream stream, ushort value) { Span<byte> bytes = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(bytes, value); stream.Write(bytes); }
    private static void WriteUInt32(Stream stream, uint value) { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value); stream.Write(bytes); }

    private sealed class DnsReader(byte[] bytes)
    {
        private int _offset = 12;

        public bool TrySkipQuestions(out int answers, out int authorities, out int additionals)
        {
            answers = authorities = additionals = 0;
            if (bytes.Length < 12) return false;
            var questions = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(4, 2));
            answers = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(6, 2));
            authorities = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(8, 2));
            additionals = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(10, 2));
            for (var index = 0; index < questions; index++)
            {
                if (!TryReadName(ref _offset, out _) || !TryReadUInt16(ref _offset, out _) || !TryReadUInt16(ref _offset, out _)) return false;
            }
            return answers + authorities + additionals <= 4096;
        }

        public bool TryReadRecord(out DnsRecord record)
        {
            record = default!;
            if (!TryReadName(ref _offset, out var name) || !TryReadUInt16(ref _offset, out var type) ||
                !TryReadUInt16(ref _offset, out var @class) || !TryReadUInt32(ref _offset, out _) ||
                !TryReadUInt16(ref _offset, out var length) || _offset + length > bytes.Length ||
                (@class & 0x7FFF) is not (1 or 255))
            {
                return false;
            }

            var dataOffset = _offset;
            _offset += length;
            var result = new DnsRecord(NormalizeName(name), type);
            switch (type)
            {
                case 1 when length == 4:
                    result.Address = new IPAddress(bytes.AsSpan(dataOffset, 4));
                    break;
                case 12:
                    var ptrOffset = dataOffset;
                    if (!TryReadName(ref ptrOffset, out var ptr) || ptrOffset > dataOffset + length) return false;
                    result.Ptr = NormalizeName(ptr);
                    break;
                case 16:
                    result.Txt = ParseTxt(dataOffset, length);
                    if (result.Txt is null) return false;
                    break;
            case 33 when length >= 6:
                result.Port = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(dataOffset + 4, 2));
                var targetOffset = dataOffset + 6;
                if (!TryReadName(ref targetOffset, out var target) || targetOffset > dataOffset + length) return false;
                result.Target = NormalizeName(target);
                break;
            }

            record = result;
            return true;
        }

        private Dictionary<string, string>? ParseTxt(int offset, int length)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var end = offset + length;
            while (offset < end)
            {
                var count = bytes[offset++];
                if (offset + count > end) return null;
                string value;
                try
                {
                    value = StrictUtf8.GetString(bytes, offset, count);
                }
                catch (DecoderFallbackException)
                {
                    return null;
                }
                offset += count;
                var separator = value.IndexOf('=');
                if (separator > 0)
                {
                    var key = value[..separator];
                    if (key is "v" or "id" or "name" or "platform" or "caps" or "fp") result[key] = value[(separator + 1)..];
                }
            }
            return result;
        }

        private bool TryReadName(ref int offset, out string name) => TryReadQueryName(bytes, ref offset, out name);
        private bool TryReadUInt16(ref int offset, out ushort value)
        {
            value = 0;
            if (offset + 2 > bytes.Length) return false;
            value = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2)); offset += 2; return true;
        }
        private bool TryReadUInt32(ref int offset, out uint value)
        {
            value = 0;
            if (offset + 4 > bytes.Length) return false;
            value = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4)); offset += 4; return true;
        }
    }

    private sealed class DnsRecord(string name, ushort type)
    {
        public string Name { get; } = name;
        public ushort Type { get; } = type;
        public string? Ptr { get; set; }
        public string? Target { get; set; }
        public ushort? Port { get; set; }
        public IPAddress? Address { get; set; }
        public Dictionary<string, string>? Txt { get; set; }
    }

    private sealed class DnsRegistration(UdpClient socket, DeviceDescriptor descriptor, string hostName) : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private Task? _loop;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            var packet = BuildAnnouncement(descriptor, hostName, GetLocalAddress());
            await socket.SendAsync(packet.AsMemory(), new IPEndPoint(MulticastAddress, MulticastPort), cancellationToken).ConfigureAwait(false);
            _loop = Task.Run(async () =>
            {
                while (!_cancellation.IsCancellationRequested)
                {
                    try
                    {
                        var result = await socket.ReceiveAsync(_cancellation.Token).ConfigureAwait(false);
                        if (IsServiceQuery(result.Buffer))
                        {
                            var response = BuildAnnouncement(descriptor, hostName, GetLocalAddress());
                            await socket.SendAsync(response.AsMemory(), new IPEndPoint(MulticastAddress, MulticastPort), _cancellation.Token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (SocketException) { break; }
                    catch (InvalidOperationException) { break; }
                }
            }, CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            _cancellation.Cancel(); socket.Dispose();
            if (_loop is not null) { try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { } }
            _cancellation.Dispose();
        }

        private static bool IsServiceQuery(ReadOnlySpan<byte> packet)
        {
            if (packet.Length < 12 || (BinaryPrimitives.ReadUInt16BigEndian(packet[2..4]) & 0x8000) != 0) return false;
            var questions = BinaryPrimitives.ReadUInt16BigEndian(packet[4..6]);
            var offset = 12;
            for (var index = 0; index < questions; index++)
            {
                if (!TryReadQueryName(packet, ref offset, out var name) || offset + 4 > packet.Length) return false;
                var type = BinaryPrimitives.ReadUInt16BigEndian(packet[offset..(offset + 2)]); offset += 2;
                var @class = BinaryPrimitives.ReadUInt16BigEndian(packet[offset..(offset + 2)]); offset += 2;
                if (string.Equals(NormalizeName(name), NormalizeName(ServiceType), StringComparison.OrdinalIgnoreCase) &&
                    (type is 12 or 255) && ((@class & 0x7FFF) is 1 or 255)) return true;
            }
            return false;
        }

        private static IPAddress GetLocalAddress() => NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.OperationalStatus == OperationalStatus.Up && network.NetworkInterfaceType is not NetworkInterfaceType.Loopback)
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Select(address => address.Address)
            .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && IsPrivate(address))
            ?? throw new InvalidOperationException("DropLink discovery requires an active private-network IPv4 address.");
    }
}
