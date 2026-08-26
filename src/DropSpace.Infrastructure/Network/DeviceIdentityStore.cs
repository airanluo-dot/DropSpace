using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.Versioning;
using System.Text;
using DropSpace.Core.Transfer;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Network;

public sealed record DeviceIdentity(
    Guid DeviceId,
    string DisplayName,
    DevicePlatform Platform,
    X509Certificate2 Certificate,
    string Fingerprint)
{
    public byte[] ExportPublicKey() => Certificate.GetECDsaPublicKey()?.ExportSubjectPublicKeyInfo()
        ?? throw new InvalidOperationException("The device certificate does not contain an ECDSA public key.");
}

[SupportedOSPlatform("windows")]
public sealed class DeviceIdentityStore(AppStoragePaths paths)
{
    private const string IdentityFileName = "device-identity.bin";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DeviceIdentity? _cached;

    public async Task<DeviceIdentity> GetOrCreateAsync(string? displayName = null, CancellationToken cancellationToken = default)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is not null) return _cached;
            paths.EnsureCreated();
            var path = Path.Combine(paths.Data, IdentityFileName);
            if (File.Exists(path))
            {
                try
                {
                    var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                    var bytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                    _cached = Deserialize(bytes);
                    return _cached;
                }
                catch (Exception exception) when (exception is CryptographicException or IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    throw new InvalidDataException("The DropSpace device identity could not be opened without replacing it.", exception);
                }
            }

            var identity = Create(displayName);
            var serialized = Serialize(identity);
            var protectedIdentity = ProtectedData.Protect(serialized, null, DataProtectionScope.CurrentUser);
            var temporary = string.Concat(path, ".", Guid.NewGuid().ToString("N"), ".tmp");
            await File.WriteAllBytesAsync(temporary, protectedIdentity, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, true);
            _cached = identity;
            return identity;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static DeviceIdentity Create(string? displayName)
    {
        var id = Guid.NewGuid();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(string.Concat("CN=DropSpace-", id.ToString("N")), key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
        [
            new Oid("1.3.6.1.5.5.7.3.1"),
            new Oid("1.3.6.1.5.5.7.3.2"),
        ], false));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddYears(10));
        var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
        var fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();
        var name = string.IsNullOrWhiteSpace(displayName) ? Environment.MachineName : displayName.Trim();
        name = name.Length > 64 ? name[..64] : name;
        return new DeviceIdentity(id, name, DevicePlatform.Windows, certificate, fingerprint);
    }

    private static byte[] Serialize(DeviceIdentity identity)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(identity.DeviceId.ToByteArray());
        writer.Write(identity.DisplayName);
        writer.Write((int)identity.Platform);
        var pfx = identity.Certificate.Export(X509ContentType.Pfx);
        writer.Write(pfx.Length);
        writer.Write(pfx);
        writer.Flush();
        return stream.ToArray();
    }

    private static DeviceIdentity Deserialize(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
        var idBytes = reader.ReadBytes(16);
        if (idBytes.Length != 16) throw new InvalidDataException("The stored device identity identifier is truncated.");
        var id = new Guid(idBytes);
        var name = reader.ReadString();
        var platform = (DevicePlatform)reader.ReadInt32();
        var length = reader.ReadInt32();
        if (id == Guid.Empty || name.Length is < 1 or > 64 || !Enum.IsDefined(platform) || length is < 1 or > 128 * 1024)
        {
            throw new InvalidDataException("The stored device identity metadata is invalid.");
        }

        var pfx = reader.ReadBytes(length);
        if (pfx.Length != length) throw new InvalidDataException("The stored device identity certificate is truncated.");
        var certificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
        var fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData)).ToLowerInvariant();
        return new DeviceIdentity(id, name, platform, certificate, fingerprint);
    }
}
