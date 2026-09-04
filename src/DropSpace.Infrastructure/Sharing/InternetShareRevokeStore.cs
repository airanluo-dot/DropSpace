using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Sharing;

[SupportedOSPlatform("windows")]
public sealed class InternetShareRevokeStore(AppStoragePaths paths)
{
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
        CancellationToken cancellationToken = default)
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
        var directory = GetDirectory();
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var result = new List<RestorableInternetShareSession>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.bin", SearchOption.TopDirectoryOnly).Take(MaximumPersistedRecords))
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
                // A transient read failure must not prevent the local workspace from starting.
            }
            catch (UnauthorizedAccessException)
            {
                // A transient access failure must not prevent the local workspace from starting.
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

public sealed record RestorableInternetShareSession(
    Guid ShareId,
    ShareBackendUploadSession Session,
    DateTimeOffset ExpiresAtUtc);
