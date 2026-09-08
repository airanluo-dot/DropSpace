using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Collections.Concurrent;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Sharing;

[SupportedOSPlatform("windows")]
public sealed class InternetShareRevokeStore(AppStoragePaths paths)
{
    private static readonly SemaphoreSlim StoreGate = new(1, 1);
    private static readonly ConcurrentDictionary<string, int> PendingReservations = new(StringComparer.OrdinalIgnoreCase);
    private const int MaximumPersistedRecords = 128;
    private const int MaximumAuthorizationLength = 4096;
    private const int MaximumUrlLength = 2048;
    private const int MaximumPayloadBytes = 32 * 1024;
    private const int MaximumProtectedPayloadBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task SaveAsync(
        Guid shareId,
        ShareBackendUploadSession session,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken = default,
        ShareCapacityReservation? reservation = null)
    {
        await StoreGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            var ownsReservation = reservation?.IsActiveFor(ReservationKey) == true;
            var pendingReservations = PendingReservations.TryGetValue(ReservationKey, out var pending)
                ? pending
                : 0;
            if (!File.Exists(GetPath(GetDirectory(), shareId)) &&
                existing.Count + pendingReservations - (ownsReservation ? 1 : 0) >= MaximumPersistedRecords)
            {
                // A live authenticated handle is never evicted. The caller must revoke the
                // remote share or wait for an existing handle to expire before retrying.
                throw new InvalidOperationException("The secure share revoke-handle capacity is full.");
            }

            await SaveCoreAsync(shareId, session, expiresAtUtc, cancellationToken).ConfigureAwait(false);
            if (ownsReservation) reservation!.Commit();
        }
        finally { StoreGate.Release(); }
    }

    /// <summary>
    /// Checks capacity before a backend share is created. Expired and malformed local
    /// handles are pruned; live handles remain durable and are never evicted to make room.
    /// </summary>
    public async Task EnsureCapacityAsync(CancellationToken cancellationToken = default)
    {
        await StoreGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            var pendingReservations = PendingReservations.TryGetValue(ReservationKey, out var pending)
                ? pending
                : 0;
            if (existing.Count + pendingReservations >= MaximumPersistedRecords)
            {
                throw new InvalidOperationException("The secure share revoke-handle capacity is full.");
            }
        }
        finally { StoreGate.Release(); }
    }

    /// <summary>
    /// Reserves one durable revoke-handle slot while the remote backend share is being
    /// created. Without this reservation, concurrent creates could all pass the preflight
    /// check and the later SaveAsync call would leave an already-created remote share
    /// without a local capability.
    /// </summary>
    public async Task<ShareCapacityReservation> ReserveCapacityAsync(
        CancellationToken cancellationToken = default)
    {
        await StoreGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            var pendingReservations = PendingReservations.TryGetValue(ReservationKey, out var pending)
                ? pending
                : 0;
            if (existing.Count + pendingReservations >= MaximumPersistedRecords)
            {
                throw new InvalidOperationException("The secure share revoke-handle capacity is full.");
            }

            PendingReservations[ReservationKey] = pendingReservations + 1;
            return new ShareCapacityReservation(ReservationKey, ReleaseReservation);
        }
        finally { StoreGate.Release(); }
    }

    private async Task SaveCoreAsync(Guid shareId, ShareBackendUploadSession session, DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken)
    {
        Validate(shareId, session, expiresAtUtc);
        paths.EnsureCreated();
        var directory = GetDirectory();
        Directory.CreateDirectory(directory);

        var path = GetPath(directory, shareId);
        var payload = JsonSerializer.SerializeToUtf8Bytes(
            new PersistedHandle(
                session.UploadBaseUrl.ToString(),
                session.DownloadBaseUrl.ToString(),
                session.UploadAuthorization,
                session.RevokeUrl.ToString(),
                expiresAtUtc),
            JsonOptions);
        if (payload.Length > MaximumPayloadBytes)
        {
            CryptographicOperations.ZeroMemory(payload);
            throw new InvalidDataException("The persisted secure share revoke handle is too large.");
        }

        byte[]? protectedPayload = null;
        var temporary = string.Concat(path, ".", Guid.NewGuid().ToString("N"), ".tmp");
        try
        {
            protectedPayload = ProtectedData.Protect(payload, null, DataProtectionScope.CurrentUser);
            if (protectedPayload.Length > MaximumProtectedPayloadBytes)
            {
                throw new InvalidDataException("The protected secure share revoke handle is too large.");
            }

            await File.WriteAllBytesAsync(temporary, protectedPayload, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            if (protectedPayload is not null)
            {
                CryptographicOperations.ZeroMemory(protectedPayload);
            }

            TryDelete(temporary);
        }
    }

    public async Task<IReadOnlyList<RestorableInternetShareSession>> LoadAllAsync(
        CancellationToken cancellationToken = default)
    {
        await StoreGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await LoadCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { StoreGate.Release(); }
    }

    private async Task<IReadOnlyList<RestorableInternetShareSession>> LoadCoreAsync(
        CancellationToken cancellationToken)
    {
        var directory = GetDirectory();
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var result = new List<RestorableInternetShareSession>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.bin", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var shareId))
            {
                TryDelete(path);
                continue;
            }

            try
            {
                var info = new FileInfo(path);
                if (info.Length is <= 0 or > MaximumProtectedPayloadBytes)
                {
                    TryDelete(path);
                    continue;
                }

                var protectedPayload = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                byte[]? payload = null;
                try
                {
                    if (protectedPayload.Length is <= 0 or > MaximumProtectedPayloadBytes)
                    {
                        TryDelete(path);
                        continue;
                    }

                    payload = ProtectedData.Unprotect(protectedPayload, null, DataProtectionScope.CurrentUser);
                    if (payload.Length is <= 0 or > MaximumPayloadBytes)
                    {
                        TryDelete(path);
                        continue;
                    }

                    var handle = JsonSerializer.Deserialize<PersistedHandle>(payload, JsonOptions);
                    if (handle is null ||
                        !Uri.TryCreate(handle.UploadBaseUrl, UriKind.Absolute, out var uploadBaseUrl) ||
                        !Uri.TryCreate(handle.DownloadBaseUrl, UriKind.Absolute, out var downloadBaseUrl) ||
                        !Uri.TryCreate(handle.RevokeUrl, UriKind.Absolute, out var revokeUrl))
                    {
                        TryDelete(path);
                        continue;
                    }

                    if (handle.ExpiresAtUtc <= DateTimeOffset.UtcNow)
                    {
                        TryDelete(path);
                        continue;
                    }

                    var session = new ShareBackendUploadSession(
                        uploadBaseUrl,
                        downloadBaseUrl,
                        handle.UploadAuthorization,
                        revokeUrl);
                    Validate(shareId, session, handle.ExpiresAtUtc);
                    result.Add(new RestorableInternetShareSession(shareId, session, handle.ExpiresAtUtc));
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(protectedPayload);
                    if (payload is not null)
                    {
                        CryptographicOperations.ZeroMemory(payload);
                    }
                }
            }
            catch (CryptographicException)
            {
                TryDelete(path);
            }
            catch (JsonException)
            {
                TryDelete(path);
            }
            catch (InvalidDataException)
            {
                TryDelete(path);
            }
            catch (IOException)
            {
                throw; // Recovery must remain retryable after transient I/O failures.
            }
            catch (UnauthorizedAccessException)
            {
                throw; // Do not permanently mark partial recovery as initialized.
            }
        }

        return result;
    }

    public Task DeleteAsync(Guid shareId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (shareId == Guid.Empty)
        {
            return Task.CompletedTask;
        }

        var path = GetPath(GetDirectory(), shareId);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (FileNotFoundException)
        {
            // Deletion is idempotent if another cleanup already removed the handle.
        }

        return Task.CompletedTask;
    }

    private string GetDirectory() => Path.Combine(paths.Data, "share-revokes");

    private string ReservationKey => Path.GetFullPath(GetDirectory());

    private static void ReleaseReservation(string key)
    {
        while (PendingReservations.TryGetValue(key, out var current))
        {
            if (current <= 1)
            {
                if (PendingReservations.TryRemove(new KeyValuePair<string, int>(key, current))) return;
            }
            else if (PendingReservations.TryUpdate(key, current - 1, current))
            {
                return;
            }
        }
    }

    private static string GetPath(string directory, Guid shareId) =>
        Path.Combine(directory, string.Concat(shareId.ToString("N"), ".bin"));

    private static void Validate(Guid shareId, ShareBackendUploadSession? session, DateTimeOffset expiresAtUtc)
    {
        if (shareId == Guid.Empty ||
            session is null ||
            !IsSafeHttpsUrl(session.UploadBaseUrl) ||
            !IsSafeHttpsUrl(session.DownloadBaseUrl) ||
            !IsSafeHttpsUrl(session.RevokeUrl) ||
            string.IsNullOrWhiteSpace(session.UploadAuthorization) ||
            session.UploadAuthorization.Length > MaximumAuthorizationLength ||
            session.UploadAuthorization.Any(static character => character is '\r' or '\n') ||
            session.UploadBaseUrl.ToString().Length > MaximumUrlLength ||
            session.DownloadBaseUrl.ToString().Length > MaximumUrlLength ||
            session.RevokeUrl.ToString().Length > MaximumUrlLength ||
            expiresAtUtc <= DateTimeOffset.UtcNow ||
            expiresAtUtc > DateTimeOffset.UtcNow.AddDays(7) ||
            !session.UploadAuthorization.StartsWith("Bearer ", StringComparison.Ordinal) ||
            session.UploadAuthorization.Length <= "Bearer ".Length)
        {
            throw new InvalidDataException("The persisted secure share revoke handle is invalid.");
        }
    }

    private static bool IsSafeHttpsUrl(Uri? uri) =>
        uri is not null &&
        uri.IsAbsoluteUri &&
        uri.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(uri.UserInfo) &&
        string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record PersistedHandle(
        string UploadBaseUrl,
        string DownloadBaseUrl,
        string UploadAuthorization,
        string RevokeUrl,
        DateTimeOffset ExpiresAtUtc);
}

public sealed class ShareCapacityReservation : IDisposable
{
    private readonly string _key;
    private readonly Action<string> _release;
    private int _completed;

    internal ShareCapacityReservation(string key, Action<string> release)
    {
        _key = key;
        _release = release;
    }

    internal bool IsActiveFor(string key) =>
        Volatile.Read(ref _completed) == 0 &&
        string.Equals(_key, key, StringComparison.OrdinalIgnoreCase);

    internal void Commit() => Complete();

    public void Dispose() => Complete();

    private void Complete()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
        {
            _release(_key);
        }
    }
}

public sealed record RestorableInternetShareSession(
    Guid ShareId,
    ShareBackendUploadSession Session,
    DateTimeOffset ExpiresAtUtc);
