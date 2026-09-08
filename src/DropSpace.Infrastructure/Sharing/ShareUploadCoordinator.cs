using System.Security.Cryptography;
using System.Net.Http.Json;
using DropSpace.Core.Transfer;
using DropSpace.Infrastructure.Storage;

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
    TransferLimits? transferLimits = null,
    AppStoragePaths? storagePaths = null,
    StagingLeaseStore? stagingLeases = null)
{
    private readonly TransferLimits _limits = (transferLimits ?? new TransferLimits()).Validate();
    private readonly AppStoragePaths? _storagePaths = storagePaths;
    private readonly StagingLeaseStore? _stagingLeases = stagingLeases;
    private const int ShareChunkBytes = ShareLimits.InternetChunkPlainBytes;

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
        StagingLease? stagingLease = null;
        string? stagedSourcesRoot = null;
        try
        {
            var stagedSources = await StageSourcesAsync(sources, shareId, cancellationToken).ConfigureAwait(false);
            stagingLease = stagedSources.Lease;
            stagedSourcesRoot = stagedSources.RootPath;
            var expires = DateTimeOffset.UtcNow.Add(lifetime);
            var manifestItems = new List<EncryptedShareManifestItem>(sources.Count);
            var encryptedFiles = new List<(ShareFileSource Source, EncryptedShareManifestItem Item)>();
            foreach (var source in stagedSources.Sources)
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
            if (stagingLease is not null && _stagingLeases is not null)
            {
                await _stagingLeases.CompleteAsync(stagingLease, CancellationToken.None).ConfigureAwait(false);
            }
            else if (stagedSourcesRoot is not null)
            {
                TryDeleteDirectory(stagedSourcesRoot);
            }

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

    private async Task<StagedShareSources> StageSourcesAsync(
        IReadOnlyList<ShareFileSource> sources,
        Guid shareId,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(
            _storagePaths?.Staging ?? Path.Combine(Path.GetTempPath(), "DropSpace", "staging"),
            "shares",
            shareId.ToString("N"));
        StagingLease? lease = null;
        if (_stagingLeases is not null && _storagePaths is not null)
        {
            lease = await _stagingLeases.AcquireAsync(
                    "internet-share",
                    Path.GetRelativePath(_storagePaths.Staging, root),
                    sensitivePlaintext: true,
                    allowExistingRoot: false,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            root = lease.RootPath;
        }
        else
        {
            Directory.CreateDirectory(root);
        }

        var staged = new List<ShareFileSource>(sources.Count);
        try
        {
            for (var index = 0; index < sources.Count; index++)
            {
                var source = sources[index];
                var path = Path.Combine(root, string.Concat(index.ToString("D4", System.Globalization.CultureInfo.InvariantCulture), ".payload"));
                var temporary = string.Concat(path, ".tmp");
                try
                {
                    long length = 0;
                    string hash;
                    await using (var input = await source.OpenReadAsync(cancellationToken).ConfigureAwait(false))
                    await using (var output = new FileStream(
                                     temporary,
                                     FileMode.CreateNew,
                                     FileAccess.Write,
                                     FileShare.None,
                                     81_920,
                                     FileOptions.Asynchronous | FileOptions.WriteThrough))
                    {
                        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        var buffer = new byte[81_920];
                        while (true)
                        {
                            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                            if (read == 0) break;
                            length += read;
                            if (length > source.Length) throw new InvalidDataException("A share source grew after metadata was captured.");
                            hasher.AppendData(buffer, 0, read);
                            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        }

                        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                        hash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
                    }

                    if (length != source.Length || !string.Equals(hash, source.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("A share source changed while it was being staged.");
                    }

                    File.Move(temporary, path, overwrite: false);
                    staged.Add(source with
                    {
                        OpenReadAsync = token => OpenStagedFileAsync(path, token),
                    });
                }
                finally
                {
                    TryDeleteFile(temporary);
                }
            }

            return new StagedShareSources(root, staged, lease);
        }
        catch
        {
            if (lease is not null && _stagingLeases is not null)
            {
                await _stagingLeases.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                TryDeleteDirectory(root);
            }
            throw;
        }
    }

    private static Task<Stream> OpenStagedFileAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<Stream>(new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81_920,
            FileOptions.Asynchronous | FileOptions.SequentialScan));
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
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
        if (string.IsNullOrWhiteSpace(objectName) || objectName.Length > ShareLimits.InternetMaxObjectNameLength ||
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

internal sealed record StagedShareSources(
    string RootPath,
    IReadOnlyList<ShareFileSource> Sources,
    StagingLease? Lease);

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
