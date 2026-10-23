using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using DropSpace.Core.Agent;

namespace DropSpace.Infrastructure.Agent;

internal sealed class AgentAuthorizationStore(string directory, TimeProvider clock) : IAsyncDisposable
{
    private const int MaximumBindings = 32;
    private const int MaximumStoreBytes = 131_072;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, Grant> _grants = [];
    private readonly HashSet<Guid> _revokedIds = [];
    private AppAgentBinding[] _snapshot = [];
    private bool _loaded, _faulted;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8 };
    public string StorePath { get; } = Path.Combine(directory, "bindings.json");
    internal string RevocationsPath { get; } = Path.Combine(directory, "revoked-bindings.json");
    public IReadOnlyList<AppAgentBinding> Bindings => Volatile.Read(ref _snapshot);

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfFaulted();
            if (_loaded) return;
            if (File.Exists(RevocationsPath))
            {
                RejectLinkedStore();
                await using var journal = new FileStream(RevocationsPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
                if (journal.Length > MaximumStoreBytes) throw new InvalidDataException("Agent revocations exceed their limit.");
                var revoked = await JsonSerializer.DeserializeAsync<RevocationDocument>(journal, Json, cancellationToken).ConfigureAwait(false);
                if (revoked is not { Version: 1, Ids: not null } || revoked.Ids.Length > 1024 ||
                    revoked.Ids.Any(id => id == Guid.Empty) || revoked.Ids.Distinct().Count() != revoked.Ids.Length)
                    throw new InvalidDataException("Invalid agent revocation journal.");
                _revokedIds.UnionWith(revoked.Ids);
            }
            if (File.Exists(StorePath))
            {
                RejectLinkedStore();
                await using var stream = new FileStream(StorePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
                if (stream.Length > MaximumStoreBytes) throw new InvalidDataException("Agent authorization store exceeds its limit.");
                var document = await JsonSerializer.DeserializeAsync<StoreDocument>(stream, Json, cancellationToken).ConfigureAwait(false);
                if (document is not { Version: 1, Grants: not null } || document.Grants.Length > MaximumBindings)
                    throw new InvalidDataException("Invalid agent authorization store.");
                var ids = new HashSet<Guid>();
                foreach (var record in document.Grants)
                {
                    Validate(record);
                    if (!ids.Add(record.Id)) throw new InvalidDataException("Duplicate agent binding.");
                }
                foreach (var record in document.Grants)
                    if (!_revokedIds.Contains(record.Id) && (record.BearerHash is not null || record.ExpiresAtUtc > clock.GetUtcNow()))
                        _grants[record.Id] = new Grant(record);
            }
            _loaded = true;
            Publish();
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _faulted = true;
            throw new InvalidDataException("Agent authorization store cannot be trusted. It was not replaced.", error);
        }
        finally { _gate.Release(); }
    }

    public async Task<(AppAgentBinding Binding, string Key)> CreateAsync(string name, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        name = name.Trim();
        if (name.Length > 80 || name.Any(char.IsControl)) throw new ArgumentException("Invalid binding name.", nameof(name));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfFaulted();
            if (!_loaded) throw new InvalidOperationException("Authorization store is not loaded.");
            PruneExpired();
            if (_grants.Count >= MaximumBindings) throw new InvalidOperationException("Agent binding limit reached.");
            var key = Secret();
            var now = clock.GetUtcNow();
            var record = new GrantRecord(Guid.NewGuid(), name, now, now.AddMinutes(30), null, null, Hash(key), null);
            await SaveAsync(_grants.Values.Select(value => value.Record).Append(record), cancellationToken).ConfigureAwait(false);
            _grants[record.Id] = new Grant(record);
            Publish();
            return (Project(record), key);
        }
        finally { _gate.Release(); }
    }

    public async Task<string?> PairAsync(Guid id, string key, CancellationToken cancellationToken)
    {
        if (!IsSecret(key)) return null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfFaulted();
            _grants.TryGetValue(id, out var grant);
            var expected = grant?.Record.PairingHash is { } stored ? Convert.FromHexString(stored) : new byte[32];
            var matches = CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(key)), expected);
            if (!matches || grant is null || grant.Record.BearerHash is not null || grant.Record.ExpiresAtUtc <= clock.GetUtcNow()) return null;
            var token = string.Concat("ds1.", id.ToString("N"), ".", Secret());
            var paired = grant.Record with { PairingHash = null, ExpiresAtUtc = null, BearerHash = Hash(token), PairedAtUtc = clock.GetUtcNow() };
            await SaveAsync(_grants.Values.Select(value => value.Record.Id == id ? paired : value.Record), cancellationToken).ConfigureAwait(false);
            grant.Record = paired;
            Publish();
            return token;
        }
        finally { _gate.Release(); }
    }

    public async Task<AuthorizationLease?> AuthorizeAsync(string token, CancellationToken requestToken)
    {
        if (token.Length != 80 || !token.StartsWith("ds1.", StringComparison.Ordinal) || token[36] != '.' ||
            !Guid.TryParseExact(token.AsSpan(4, 32), "N", out var id) || !IsSecret(token[37..])) return null;
        await _gate.WaitAsync(requestToken).ConfigureAwait(false);
        try
        {
            ThrowIfFaulted();
            _grants.TryGetValue(id, out var grant);
            var expected = grant?.Record.BearerHash is { } stored ? Convert.FromHexString(stored) : new byte[32];
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(token)), expected) || grant is null || grant.Revoked.IsCancellationRequested)
                return null;
            var now = clock.GetUtcNow();
            if (grant.Record.LastUsedAtUtc is not { } last || now - last >= TimeSpan.FromMinutes(1))
            {
                var updated = grant.Record with { LastUsedAtUtc = now };
                await SaveAsync(_grants.Values.Select(value => value.Record.Id == id ? updated : value.Record), requestToken).ConfigureAwait(false);
                grant.Record = updated;
                Publish();
            }
            return new AuthorizationLease(id, CancellationTokenSource.CreateLinkedTokenSource(grant.Revoked.Token, requestToken));
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> RevokeAsync(Guid id, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfFaulted();
            if (!_grants.Remove(id, out var removed)) return false;
            // Retire admission before persistence. A write failure fails the whole store closed;
            // the UI receives that failure rather than claiming a durable revocation.
            Retire(removed.Revoked);
            Publish();
            _revokedIds.Add(id);
            // This journal is committed first so a subsequent bindings-file failure or
            // crash cannot resurrect the authorization when the process restarts.
            await SaveRevocationsAsync(CancellationToken.None).ConfigureAwait(false);
            await SaveAsync(_grants.Values.Select(value => value.Record), CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        finally { _gate.Release(); }
    }

    private async Task SaveAsync(IEnumerable<GrantRecord> grants, CancellationToken cancellationToken)
    {
        await WriteAtomicAsync(StorePath, new StoreDocument(1, grants.ToArray()), cancellationToken).ConfigureAwait(false);
        // The new snapshot no longer contains retired grants. Journal compaction is safe
        // only after that durable replace; keeping extra tombstones after failure is safe.
        if (_revokedIds.Count > 0)
        {
            _revokedIds.Clear();
            await SaveRevocationsAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private Task SaveRevocationsAsync(CancellationToken cancellationToken) =>
        WriteAtomicAsync(RevocationsPath, new RevocationDocument(1, _revokedIds.ToArray()), cancellationToken);

    private async Task WriteAtomicAsync<T>(string destination, T document, CancellationToken cancellationToken)
    {
        var temporary = Path.Combine(directory, string.Concat(".bindings-", Guid.NewGuid().ToString("N"), ".tmp"));
        try
        {
            PrepareDirectory();
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
            {
                await JsonSerializer.SerializeAsync(output, document, Json, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(true);
            }
            // The atomic replace is the non-cancellable durable tail; never expose a secret
            // whose hash has not actually been persisted.
            File.Move(temporary, destination, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _faulted = true;
            throw;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private void RejectLinkedStore()
    {
        for (var item = new DirectoryInfo(directory); item is not null; item = item.Parent)
            if (item.Exists && (item.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked agent storage is not supported.");
        if (new FileInfo(StorePath).LinkTarget is not null || File.Exists(StorePath) && (File.GetAttributes(StorePath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Linked agent authorization files are not supported.");
        if (new FileInfo(RevocationsPath).LinkTarget is not null || File.Exists(RevocationsPath) && (File.GetAttributes(RevocationsPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Linked agent revocation files are not supported.");
    }

    internal void VerifyStorageLocation() => RejectLinkedStore();

    internal void PrepareDirectory()
    {
        RejectLinkedStore();
        Directory.CreateDirectory(directory);
        RejectLinkedStore();
        if (OperatingSystem.IsWindows()) MakeDirectoryPrivate();
    }

    [SupportedOSPlatform("windows")]
    private void MakeDirectoryPrivate()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new UnauthorizedAccessException("The current Windows user is unavailable.");
        var permissions = new DirectorySecurity();
        permissions.SetOwner(user);
        permissions.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        permissions.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        permissions.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            FileSystemRights.FullControl, inheritance, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(permissions);
    }

    public async Task<bool> ExpirePendingAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfFaulted();
            var before = _grants.Count;
            PruneExpired();
            if (before == _grants.Count) return false;
            Publish(); return true;
        }
        finally { _gate.Release(); }
    }

    private void PruneExpired()
    {
        foreach (var pair in _grants.Where(value => value.Value.Record.ExpiresAtUtc <= clock.GetUtcNow()).ToArray())
        { _grants.Remove(pair.Key); pair.Value.Revoked.Dispose(); }
    }
    private void ThrowIfFaulted()
    {
        if (_faulted) throw new InvalidDataException("Agent authorization is unavailable until its store is repaired.");
    }
    private static void Retire(CancellationTokenSource source)
    {
        // CancelAsync marks cancellation immediately; never hold the store/UI lifecycle
        // gate while an application cancellation callback is still unwinding.
        _ = source.CancelAsync().ContinueWith(completed =>
        { _ = completed.Exception; source.Dispose(); }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    private void Publish() => Volatile.Write(ref _snapshot, _grants.Values.Select(value => Project(value.Record)).ToArray());
    private static AppAgentBinding Project(GrantRecord record) => new(record.Id, record.Name,
        record.BearerHash is null ? AppAgentBindingState.Pending : AppAgentBindingState.Paired,
        record.CreatedAtUtc, record.ExpiresAtUtc, record.PairedAtUtc, record.LastUsedAtUtc);
    private static string Secret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static bool IsSecret(string value) => value.Length == 43 && value.All(character =>
        char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool IsHash(string? value) => value?.Length == 64 && value.All(char.IsAsciiHexDigit);
    private static void Validate(GrantRecord record)
    {
        if (record is null) throw new InvalidDataException("Null agent binding.");
        if (record.Id == Guid.Empty || string.IsNullOrWhiteSpace(record.Name) || record.Name.Length > 80 || record.Name.Any(char.IsControl) ||
            record.CreatedAtUtc.Offset != TimeSpan.Zero || record.CreatedAtUtc == default ||
            record.ExpiresAtUtc?.Offset is { } expiresOffset && expiresOffset != TimeSpan.Zero ||
            record.PairedAtUtc?.Offset is { } pairedOffset && pairedOffset != TimeSpan.Zero ||
            record.LastUsedAtUtc?.Offset is { } usedOffset && usedOffset != TimeSpan.Zero)
            throw new InvalidDataException("Invalid agent binding metadata.");
        if (record.BearerHash is null
            ? !IsHash(record.PairingHash) || record.PairedAtUtc is not null || record.ExpiresAtUtc is null ||
                record.ExpiresAtUtc <= record.CreatedAtUtc || record.ExpiresAtUtc > record.CreatedAtUtc.AddMinutes(30)
            : !IsHash(record.BearerHash) || record.PairingHash is not null || record.ExpiresAtUtc is not null || record.PairedAtUtc is null)
            throw new InvalidDataException("Invalid agent credential state.");
    }
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var grant in _grants.Values) Retire(grant.Revoked);
            _grants.Clear(); Publish();
        }
        finally { _gate.Release(); }
    }
    internal sealed record GrantRecord(Guid Id, string Name, DateTimeOffset CreatedAtUtc, DateTimeOffset? ExpiresAtUtc,
        DateTimeOffset? PairedAtUtc, DateTimeOffset? LastUsedAtUtc, string? PairingHash, string? BearerHash);
    private sealed record StoreDocument(int Version, GrantRecord[] Grants);
    private sealed record RevocationDocument(int Version, Guid[] Ids);
    private sealed class Grant(GrantRecord record)
    {
        public GrantRecord Record { get; set; } = record;
        public CancellationTokenSource Revoked { get; } = new();
    }
    internal sealed class AuthorizationLease(Guid bindingId, CancellationTokenSource source) : IDisposable
    {
        public Guid BindingId { get; } = bindingId;
        public CancellationToken Token => source.Token;
        public void Dispose() => source.Dispose();
    }
}
