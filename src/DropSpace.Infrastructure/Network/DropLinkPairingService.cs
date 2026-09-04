using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using DropSpace.Core.Transfer;
using Microsoft.Extensions.Logging;

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
public sealed class DropLinkPairingService(
    DeviceIdentityStore identities,
    DeviceSecretStore secrets,
    ILogger<DropLinkPairingService>? logger = null) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, PendingPairing> _pending = new();
    private readonly ConcurrentDictionary<Guid, Task> _expirationTasks = new();
    private readonly Dictionary<string, RateWindow> _rateWindows = new(StringComparer.Ordinal);
    private readonly object _admissionGate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private int _disposed;

    public async Task<PairingHandshake> CreateHelloAsync(
        PeerCapability capabilities,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
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
        CancellationToken cancellationToken = default,
        string? remoteAddress = null)
    {
        ThrowIfDisposed();
        var address = NormalizeRemoteAddress(remoteAddress);
        ReserveRate(address);
        ValidateHello(remote);
        var local = await CreateHelloAsync(localCapabilities, cancellationToken).ConfigureAwait(false);
        try
        {
            PairingOffer offer;
            Task expirationTask;
            lock (_admissionGate)
            {
                var now = DateTimeOffset.UtcNow;
                PruneExpiredPendingLocked(now);
                EnsureAdmissionAvailableLocked(remote, address);

                var secret = DeriveSecret(local, remote);
                var sessionId = Guid.NewGuid();
                var expires = now + DropLinkPairingPolicy.PendingLifetime;
                var sas = ComputeSas(secret, local.Hello, remote);
                var pending = new PendingPairing(
                    sessionId,
                    remote,
                    local.Hello,
                    secret,
                    sas,
                    expires,
                    address,
                    local);
                if (!_pending.TryAdd(sessionId, pending))
                {
                    CryptographicOperations.ZeroMemory(secret);
                    throw new PairingAdmissionException("pairing-capacity");
                }

                offer = new PairingOffer(
                    sessionId,
                    remote,
                    local.Hello,
                    sas,
                    expires,
                    PairingState.AwaitingLocalSasConfirmation);
                expirationTask = ExpirePendingAsync(sessionId, expires, _shutdown.Token);
                _expirationTasks[sessionId] = expirationTask;
            }

            ObserveExpirationTask(expirationTask, offer.SessionId);
            return offer;
        }
        catch
        {
            local.Dispose();
            throw;
        }
    }

    public int PendingCount => _pending.Count;

    public bool TryGetPendingOffer(Guid sessionId, out PairingOffer offer)
    {
        offer = default!;
        if (sessionId == Guid.Empty || !_pending.TryGetValue(sessionId, out var pending))
        {
            return false;
        }

        if (pending.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            if (_pending.TryRemove(sessionId, out var expired))
            {
                ReleasePending(expired);
            }

            return false;
        }

        offer = new PairingOffer(
            pending.SessionId,
            pending.RemoteHello,
            pending.LocalHello,
            pending.Sas,
            pending.ExpiresAtUtc,
            PairingState.AwaitingLocalSasConfirmation);
        return true;
    }

    public async Task<PairingConfirmationResult> ConfirmAsync(
        Guid sessionId,
        int sas,
        bool confirmed,
        PairingDecision decision = PairingDecision.Confirm,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
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

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            if (_disposeTask is null)
            {
                _disposeTask = DisposeCoreAsync();
            }

            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdown.Cancel();
        lock (_admissionGate)
        {
            _rateWindows.Clear();
        }

        foreach (var entry in _pending.ToArray())
        {
            if (_pending.TryRemove(entry.Key, out var pending))
            {
                ReleasePending(pending);
            }
        }

        var expirationTasks = _expirationTasks.Values.ToArray();
        if (expirationTasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(expirationTasks).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
            {
                // Shutdown races are expected after pending pairing state has been released.
            }
            catch (Exception exception)
            {
                logger?.LogError(exception, "A DropLink pairing expiration task failed during shutdown.");
            }
        }

        _shutdown.Dispose();
    }

    private async Task ExpirePendingAsync(
        Guid sessionId,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            var delay = expiresAtUtc - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            _expirationTasks.TryRemove(sessionId, out _);
        }

        if (_pending.TryRemove(sessionId, out var pending))
        {
            ReleasePending(pending);
        }
    }

    private void ObserveExpirationTask(Task task, Guid sessionId)
    {
        task.ContinueWith(
            completed =>
            {
                if (completed.IsFaulted)
                {
                    logger?.LogError(
                        completed.Exception?.GetBaseException(),
                        "DropLink pairing expiration failed for session {SessionId}.",
                        sessionId);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private void EnsureAdmissionAvailableLocked(PairingHello remote, string address)
    {
        if (_pending.Count >= DropLinkPairingPolicy.MaximumPending)
        {
            throw new PairingAdmissionException("pairing-capacity");
        }

        var pendingForAddress = _pending.Values.Count(candidate =>
            string.Equals(candidate.RemoteAddress, address, StringComparison.Ordinal));
        if (pendingForAddress >= DropLinkPairingPolicy.MaximumPendingPerAddress)
        {
            throw new PairingAdmissionException("pairing-address-capacity");
        }

        var pendingForDevice = _pending.Values.Count(candidate =>
            candidate.RemoteHello.DeviceId == remote.DeviceId ||
            string.Equals(candidate.RemoteHello.PublicKeyBase64, remote.PublicKeyBase64, StringComparison.Ordinal));
        if (pendingForDevice >= DropLinkPairingPolicy.MaximumPendingPerDevice)
        {
            throw new PairingAdmissionException("pairing-duplicate");
        }
    }

    private void ReserveRate(string address)
    {
        lock (_admissionGate)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var key in _rateWindows
                         .Where(entry => now - entry.Value.WindowStartedAtUtc > DropLinkPairingPolicy.RateWindow)
                         .Select(entry => entry.Key)
                         .ToArray())
            {
                _rateWindows.Remove(key);
            }

            if (!_rateWindows.TryGetValue(address, out var window))
            {
                if (_rateWindows.Count >= DropLinkPairingPolicy.MaximumRateEntries)
                {
                    throw new PairingAdmissionException("pairing-rate-capacity");
                }

                window = new RateWindow(now, 0);
            }

            if (window.Attempts >= DropLinkPairingPolicy.MaximumAttemptsPerWindow)
            {
                throw new PairingAdmissionException("pairing-rate-limited");
            }

            _rateWindows[address] = window with { Attempts = window.Attempts + 1 };
        }
    }

    private void PruneExpiredPendingLocked(DateTimeOffset now)
    {
        foreach (var entry in _pending.Where(entry => entry.Value.ExpiresAtUtc <= now).ToArray())
        {
            if (_pending.TryRemove(entry.Key, out var expired))
            {
                ReleasePending(expired);
            }
        }
    }

    private static string NormalizeRemoteAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return "unknown";
        }

        var normalized = address.Trim();
        return normalized.Length <= 128 ? normalized : normalized[..128];
    }

    private static void ReleasePending(PendingPairing pending)
    {
        pending.LocalHandshake.Dispose();
        CryptographicOperations.ZeroMemory(pending.Secret);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private sealed record RateWindow(DateTimeOffset WindowStartedAtUtc, int Attempts);

    private sealed record PendingPairing(
        Guid SessionId,
        PairingHello RemoteHello,
        PairingHello LocalHello,
        byte[] Secret,
        int Sas,
        DateTimeOffset ExpiresAtUtc,
        string RemoteAddress,
        PairingHandshake LocalHandshake);
}
