using System.Security.Cryptography;
using System.Net.Http.Json;
using DropSpace.Core.Transfer;

namespace DropSpace.Infrastructure.Sharing;

public sealed record ShareUploadObject(string ObjectName, byte[] Ciphertext, string ContentType);

public sealed record ShareBackendUploadSession(
    Uri UploadBaseUrl,
    Uri DownloadBaseUrl,
    string UploadAuthorization,
    Uri RevokeUrl);

public interface IShareBackendClient
{
    Task<ShareBackendUploadSession> CreateAsync(Guid shareId, DateTimeOffset expiresAtUtc, int itemCount, long totalBytes, CancellationToken cancellationToken = default);

    Task UploadAsync(ShareBackendUploadSession session, string objectName, ReadOnlyMemory<byte> ciphertext, string contentType, CancellationToken cancellationToken = default);

    Task RevokeAsync(ShareBackendUploadSession session, Guid shareId, CancellationToken cancellationToken = default);
}

public sealed class InternetShareClient(
    ShareCryptoService crypto,
    IShareBackendClient backend,
    TransferLimits? transferLimits = null)
{
    private readonly TransferLimits _limits = (transferLimits ?? new TransferLimits()).Validate();
    private const int ShareChunkBytes = 5 * 1024 * 1024;

    public async Task<ShareDescriptor> CreateAsync(
        IReadOnlyList<ShareFileSource> sources,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        var result = await CreateWithSessionAsync(sources, lifetime, cancellationToken).ConfigureAwait(false);
        return result.Descriptor;
    }

    public async Task<(ShareDescriptor Descriptor, ShareBackendUploadSession Session)> CreateWithSessionAsync(
        IReadOnlyList<ShareFileSource> sources,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count is < 1 or > ShareLimits.DefaultInternetMaxItems || sources.Count > _limits.MaxItems) throw new ArgumentOutOfRangeException(nameof(sources));
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromDays(7)) throw new ArgumentOutOfRangeException(nameof(lifetime));
        if (sources.Any(source => source is null || source.Length < 0)) throw new InvalidDataException("An Internet share item length cannot be negative.");
        var totalBytes = 0L;
        foreach (var source in sources)
        {
            ValidateSource(source);
            if (source.Length > ShareLimits.DefaultInternetMaxBytes - totalBytes)
            {
                throw new InvalidDataException("The Internet share byte limit was exceeded.");
            }
            totalBytes += source.Length;
        }
        if (totalBytes < 1 || totalBytes > ShareLimits.DefaultInternetMaxBytes) throw new InvalidDataException("The Internet share byte limit was exceeded.");

        var shareId = Guid.NewGuid();
        var masterKey = crypto.CreateMasterKey();
        ShareBackendUploadSession? createdSession = null;
        try
        {
            var expires = DateTimeOffset.UtcNow.Add(lifetime);
            var manifestItems = new List<EncryptedShareManifestItem>(sources.Count);
            var encryptedFiles = new List<(ShareFileSource Source, EncryptedShareManifestItem Item)>();
            foreach (var source in sources)
            {
                var fileId = Guid.NewGuid();
                var noncePrefix = RandomNumberGenerator.GetBytes(8);
                var chunkCount = source.Length == 0 ? 0 : checked((int)Math.Ceiling(source.Length / (double)ShareChunkBytes));
                var item = new EncryptedShareManifestItem(fileId, source.DisplayName, source.MimeType, source.Length, chunkCount, source.Sha256, noncePrefix);
                manifestItems.Add(item);
                encryptedFiles.Add((source, item));
            }
            var encryptedManifest = crypto.EncryptManifest(masterKey, shareId, manifestItems);
            var session = await backend.CreateAsync(shareId, expires, sources.Count, totalBytes, cancellationToken).ConfigureAwait(false);
            ValidateSession(session);
            createdSession = session;
            await backend.UploadAsync(session, "manifest.bin", ShareCryptoService.PackManifestWire(encryptedManifest.Nonce, encryptedManifest.Ciphertext, encryptedManifest.Tag), "application/octet-stream", cancellationToken).ConfigureAwait(false);

            foreach (var (source, item) in encryptedFiles)
            {
                await using var stream = await source.OpenReadAsync(cancellationToken).ConfigureAwait(false);
                for (var index = 0; index < item.ChunkCount; index++)
                {
                    var length = (int)Math.Min(ShareChunkBytes, source.Length - (long)index * ShareChunkBytes);
                    var plain = new byte[length];
                    await ReadExactlyAsync(stream, plain, cancellationToken).ConfigureAwait(false);
                    var chunk = crypto.EncryptChunk(masterKey, shareId, item.FileId, index, plain, item.NoncePrefix);
                    await backend.UploadAsync(session, string.Concat(item.FileId.ToString("N"), ".", index, ".bin"), ShareCryptoService.PackChunkWire(chunk.Ciphertext, chunk.Tag), "application/octet-stream", cancellationToken).ConfigureAwait(false);
                }
            }

            var url = new Uri(string.Concat(session.DownloadBaseUrl.ToString().TrimEnd('/'), "/s/", shareId.ToString("N"), "#k=", ShareCryptoService.ToUrlFragment(masterKey)));
            return (new ShareDescriptor(shareId, url, expires, sources.Count, totalBytes, true, url.Fragment), session);
        }
        catch
        {
            if (createdSession is not null)
            {
                try
                {
                    await backend.RevokeAsync(createdSession, shareId, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception cleanupException) when (cleanupException is HttpRequestException or IOException or InvalidOperationException or UnauthorizedAccessException)
                {
                    // Preserve the original upload failure. The backend's explicit revoke
                    // endpoint remains available for an operator retry if cleanup failed.
                    _ = cleanupException;
                }
            }

            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    public Task RevokeAsync(ShareBackendUploadSession session, Guid shareId, CancellationToken cancellationToken = default) => backend.RevokeAsync(session, shareId, cancellationToken);

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("The source changed during secure share upload.");
            offset += read;
        }
    }

    private static void ValidateSource(ShareFileSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(source.DisplayName) || source.DisplayName.Length > 512 ||
            string.IsNullOrWhiteSpace(source.MimeType) || source.MimeType.Length > 128 ||
            source.Length < 0 || string.IsNullOrWhiteSpace(source.Sha256) || source.Sha256.Length != 64 || source.Sha256.Any(value => !Uri.IsHexDigit(value)))
        {
            throw new InvalidDataException("A secure share source is invalid.");
        }
        ArgumentNullException.ThrowIfNull(source.OpenReadAsync);
    }

    private static void ValidateSession(ShareBackendUploadSession session)
    {
        if (session is null || session.UploadBaseUrl is null || session.DownloadBaseUrl is null || session.RevokeUrl is null ||
            !IsSafeHttpsUrl(session.UploadBaseUrl) || !IsSafeHttpsUrl(session.DownloadBaseUrl) || !IsSafeHttpsUrl(session.RevokeUrl) ||
            string.IsNullOrWhiteSpace(session.UploadAuthorization) ||
            !session.UploadAuthorization.StartsWith("Bearer ", StringComparison.Ordinal) ||
            session.UploadAuthorization.Length <= "Bearer ".Length ||
            session.UploadAuthorization.Any(character => character is '\r' or '\n'))
        {
            throw new InvalidDataException("The secure share backend returned an unsafe upload session.");
        }
    }

    private static bool IsSafeHttpsUrl(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    private static void ValidateObjectName(string objectName)
    {
        if (string.IsNullOrWhiteSpace(objectName) || objectName.Length > 180 ||
            objectName is "." or ".." || objectName.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')))
        {
            throw new InvalidDataException("The secure share object name is invalid.");
        }
    }
}

public sealed record ShareFileSource(
    string DisplayName,
    string MimeType,
    long Length,
    string Sha256,
    Func<CancellationToken, Task<Stream>> OpenReadAsync);

public sealed class CloudflareWorkerShareBackend(HttpClient client, Uri baseUri) : IShareBackendClient
{
    public async Task<ShareBackendUploadSession> CreateAsync(Guid shareId, DateTimeOffset expiresAtUtc, int itemCount, long totalBytes, CancellationToken cancellationToken = default)
    {
        ValidateBaseUri(baseUri);
        using var response = await client.PostAsJsonAsync(new Uri(baseUri, "/v1/shares"), new { shareId, expiresAtUtc, itemCount, totalBytes }, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var session = await response.Content.ReadFromJsonAsync<ShareBackendUploadSession>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Secure share backend returned an empty session.");
        ValidateSession(session);
        if (!SameOrigin(session.UploadBaseUrl, baseUri) || !SameOrigin(session.DownloadBaseUrl, baseUri) || !SameOrigin(session.RevokeUrl, baseUri))
        {
            throw new InvalidDataException("The secure share backend returned URLs outside the configured origin.");
        }
        return session;
    }

    public async Task UploadAsync(ShareBackendUploadSession session, string objectName, ReadOnlyMemory<byte> ciphertext, string contentType, CancellationToken cancellationToken = default)
    {
        ValidateSession(session);
        ValidateObjectName(objectName);
        using var content = new ByteArrayContent(ciphertext.ToArray());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(session.UploadBaseUrl, objectName)) { Content = content };
        request.Headers.Add("Authorization", session.UploadAuthorization);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task RevokeAsync(ShareBackendUploadSession session, Guid shareId, CancellationToken cancellationToken = default)
    {
        if (shareId == Guid.Empty) throw new ArgumentOutOfRangeException(nameof(shareId));
        ValidateBaseUri(baseUri);
        ValidateSession(session);
        var revokeUri = new Uri(baseUri, string.Concat("/v1/shares/", shareId.ToString("N")));
        if (session.RevokeUrl.Scheme != Uri.UriSchemeHttps || !string.Equals(session.RevokeUrl.Host, revokeUri.Host, StringComparison.OrdinalIgnoreCase) || session.RevokeUrl.Port != revokeUri.Port)
        {
            throw new InvalidDataException("The secure share revoke endpoint does not match the configured backend.");
        }
        using var request = new HttpRequestMessage(HttpMethod.Delete, revokeUri);
        request.Headers.Add("Authorization", session.UploadAuthorization);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode &&
            response.StatusCode is not System.Net.HttpStatusCode.NotFound and not System.Net.HttpStatusCode.Gone)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    private static void ValidateBaseUri(Uri uri)
    {
        if (uri is null || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidDataException("The secure share backend URI must be an HTTPS origin.");
        }
    }

    private static void ValidateSession(ShareBackendUploadSession session)
    {
        if (session is null || session.UploadBaseUrl is null || session.DownloadBaseUrl is null || session.RevokeUrl is null ||
            !IsSafeHttpsUrl(session.UploadBaseUrl) || !IsSafeHttpsUrl(session.DownloadBaseUrl) || !IsSafeHttpsUrl(session.RevokeUrl) ||
            string.IsNullOrWhiteSpace(session.UploadAuthorization) ||
            !session.UploadAuthorization.StartsWith("Bearer ", StringComparison.Ordinal) ||
            session.UploadAuthorization.Length <= "Bearer ".Length ||
            session.UploadAuthorization.Any(character => character is '\r' or '\n'))
        {
            throw new InvalidDataException("The secure share backend returned an unsafe upload session.");
        }
    }

    private static bool IsSafeHttpsUrl(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    private static void ValidateObjectName(string objectName)
    {
        if (string.IsNullOrWhiteSpace(objectName) || objectName.Length > 180 ||
            objectName is "." or ".." || objectName.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_')))
        {
            throw new InvalidDataException("The secure share object name is invalid.");
        }
    }

    private static bool SameOrigin(Uri candidate, Uri expected) =>
        candidate.Scheme == expected.Scheme &&
        string.Equals(candidate.Host, expected.Host, StringComparison.OrdinalIgnoreCase) &&
        candidate.Port == expected.Port;
}
