using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using DropSpace.Core.Transfer;

namespace DropSpace.Infrastructure.Network;

public sealed record PairingHello(
    DropLinkProtocolVersion Protocol,
    Guid DeviceId,
    string DisplayName,
    DevicePlatform Platform,
    PeerCapability Capabilities,
    string IdentityFingerprint,
    string PublicKeyBase64,
    string NonceBase64);

public sealed record PairingOffer(
    Guid SessionId,
    PairingHello RemoteHello,
    PairingHello LocalHello,
    int Sas,
    DateTimeOffset ExpiresAtUtc,
    PairingState State = PairingState.AwaitingLocalSasConfirmation);

public sealed record PairingConfirmationResult(
    bool Trusted,
    Guid PeerId,
    PairingState State,
    string? ErrorCategory);

public sealed class PairingHandshake : IDisposable
{
    internal PairingHandshake(DeviceIdentity identity, PairingHello hello, ECDiffieHellman key)
    {
        Identity = identity;
        Hello = hello;
        Key = key;
    }

    public DeviceIdentity Identity { get; }

    public PairingHello Hello { get; }

    internal ECDiffieHellman Key { get; }

    public void Dispose() => Key.Dispose();
}

[SupportedOSPlatform("windows")]
public sealed class DropLinkPairingService(DeviceIdentityStore identities, DeviceSecretStore secrets)
{
    private readonly ConcurrentDictionary<Guid, PendingPairing> _pending = new();

    public async Task<PairingHandshake> CreateHelloAsync(
        PeerCapability capabilities,
        CancellationToken cancellationToken = default)
    {
        var identity = await identities.GetOrCreateAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var hello = new PairingHello(
            DropLinkProtocolVersion.V1,
            identity.DeviceId,
            identity.DisplayName,
            identity.Platform,
            capabilities,
            identity.Fingerprint,
            Convert.ToBase64String(ephemeral.PublicKey.ExportSubjectPublicKeyInfo()),
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        return new PairingHandshake(identity, hello, ephemeral);
    }

    public async Task<PairingOffer> AcceptHelloAsync(
        PairingHello remote,
        PeerCapability localCapabilities,
        CancellationToken cancellationToken = default)
    {
        ValidateHello(remote);
        var local = await CreateHelloAsync(localCapabilities, cancellationToken).ConfigureAwait(false);
        try
        {
            var secret = DeriveSecret(local, remote);
            var sessionId = Guid.NewGuid();
            var expires = DateTimeOffset.UtcNow.AddMinutes(5);
            var sas = ComputeSas(secret, local.Hello, remote);
            _pending[sessionId] = new PendingPairing(sessionId, remote, local.Hello, secret, sas, expires, local);
            _ = ExpirePendingAsync(sessionId, expires);
            return new PairingOffer(sessionId, remote, local.Hello, sas, expires, PairingState.AwaitingLocalSasConfirmation);
        }
        catch
        {
            local.Dispose();
            throw;
        }
    }

    public async Task<PairingConfirmationResult> ConfirmAsync(
        Guid sessionId,
        int sas,
        bool confirmed,
        PairingDecision decision = PairingDecision.Confirm,
        CancellationToken cancellationToken = default)
    {
        if (!_pending.TryRemove(sessionId, out var pending)) throw new InvalidOperationException("Pairing session is not available.");
        try
        {
            if (pending.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            {
                return new PairingConfirmationResult(false, pending.RemoteHello.DeviceId, PairingState.Expired, "expired");
            }

            if (decision == PairingDecision.Cancel)
            {
                return new PairingConfirmationResult(false, pending.RemoteHello.DeviceId, PairingState.Cancelled, "cancelled");
            }

            if (decision != PairingDecision.Confirm || !confirmed || sas != pending.Sas)
            {
                return new PairingConfirmationResult(false, pending.RemoteHello.DeviceId, PairingState.Rejected, "sas-mismatch-or-rejected");
            }

            var peerId = pending.RemoteHello.DeviceId;
            await secrets.SaveAsync(peerId, pending.Secret, cancellationToken).ConfigureAwait(false);
            return new PairingConfirmationResult(true, peerId, PairingState.Trusted, null);
        }
        finally
        {
            pending.LocalHandshake.Dispose();
            CryptographicOperations.ZeroMemory(pending.Secret);
        }
    }

    public static byte[] DeriveSecret(PairingHandshake local, PairingHello remote)
    {
        ArgumentNullException.ThrowIfNull(local);
        ValidateHello(remote);
        ValidateHello(local.Hello);
        using var remoteKey = ECDiffieHellman.Create();
        remoteKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(remote.PublicKeyBase64), out _);
        var ordered = string.CompareOrdinal(local.Hello.DeviceId.ToString("N"), remote.DeviceId.ToString("N")) <= 0
            ? (local.Hello, remote)
            : (remote, local.Hello);
        var salt = SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(ordered.Item1.NonceBase64, "|", ordered.Item2.NonceBase64)));
        var shared = local.Key.DeriveKeyMaterial(remoteKey.PublicKey);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, salt, Encoding.UTF8.GetBytes("DropLink pairing v1"));
    }

    public static int ComputeSas(ReadOnlySpan<byte> secret, PairingHello first, PairingHello second)
    {
        var transcript = CanonicalTranscript(first, second);
        var digest = HMACSHA256.HashData(secret, transcript);
        return (int)(BitConverter.ToUInt32(digest, 0) % 1_000_000);
    }

    public static string ComputeAuth(ReadOnlySpan<byte> secret, string method, string path, string nonce, string bodyHash)
    {
        var transcript = Encoding.UTF8.GetBytes(string.Concat("DropLink:v1\n", method.ToUpperInvariant(), "\n", path, "\n", nonce, "\n", bodyHash));
        return Convert.ToBase64String(HMACSHA256.HashData(secret, transcript));
    }

    public static bool FixedTimeEquals(string expectedBase64, string actualBase64)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(expectedBase64), Convert.FromBase64String(actualBase64));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] CanonicalTranscript(PairingHello first, PairingHello second)
    {
        if (string.CompareOrdinal(first.DeviceId.ToString("N"), second.DeviceId.ToString("N")) > 0)
        {
            (first, second) = (second, first);
        }

        var values = new[]
        {
            first.Protocol.ToString(), first.DeviceId.ToString("N"), first.IdentityFingerprint, first.PublicKeyBase64, first.NonceBase64,
            second.Protocol.ToString(), second.DeviceId.ToString("N"), second.IdentityFingerprint, second.PublicKeyBase64, second.NonceBase64,
        };
        return Encoding.UTF8.GetBytes(string.Join("\n", values));
    }

    private static void ValidateHello(PairingHello hello)
    {
        ArgumentNullException.ThrowIfNull(hello);
        if (!hello.Protocol.IsCompatibleWith(DropLinkProtocolVersion.V1) || hello.DeviceId == Guid.Empty ||
            string.IsNullOrWhiteSpace(hello.DisplayName) || hello.DisplayName.Length > 64 ||
            hello.DisplayName.Any(char.IsControl) ||
            !Enum.IsDefined(hello.Platform) || hello.Platform == DevicePlatform.Unknown ||
            string.IsNullOrWhiteSpace(hello.IdentityFingerprint) || hello.IdentityFingerprint.Length != 64 ||
            hello.IdentityFingerprint.Any(character => !Uri.IsHexDigit(character)) ||
            string.IsNullOrWhiteSpace(hello.PublicKeyBase64) || string.IsNullOrWhiteSpace(hello.NonceBase64))
        {
            throw new InvalidDataException("The pairing hello is invalid.");
        }

        byte[] publicKey;
        byte[] nonce;
        try
        {
            publicKey = Convert.FromBase64String(hello.PublicKeyBase64);
            nonce = Convert.FromBase64String(hello.NonceBase64);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The pairing hello key or nonce encoding is invalid.", exception);
        }
        if (publicKey.Length is < 64 or > 256 || nonce.Length != 32)
        {
            throw new InvalidDataException("The pairing hello key or nonce length is invalid.");
        }

        try
        {
            using var remoteKey = ECDiffieHellman.Create();
            remoteKey.ImportSubjectPublicKeyInfo(publicKey, out _);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException("The pairing hello public key is invalid.", exception);
        }
    }

    private async Task ExpirePendingAsync(Guid sessionId, DateTimeOffset expiresAtUtc)
    {
        var delay = expiresAtUtc - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(delay).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (_pending.TryRemove(sessionId, out var pending))
        {
            pending.LocalHandshake.Dispose();
            CryptographicOperations.ZeroMemory(pending.Secret);
        }
    }

    private sealed record PendingPairing(
        Guid SessionId,
        PairingHello RemoteHello,
        PairingHello LocalHello,
        byte[] Secret,
        int Sas,
        DateTimeOffset ExpiresAtUtc,
        PairingHandshake LocalHandshake);
}
