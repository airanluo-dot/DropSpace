using System.Security.Cryptography;
using System.Net.Http.Json;
using DropSpace.Core.Transfer;

namespace DropSpace.Infrastructure.Sharing;

public sealed record ShareUploadObject(string ObjectName, byte[] Ciphertext, string ContentType);

public sealed record ShareBackendUploadSession(
    Uri UploadBaseUrl,
    Uri DownloadBaseUrl,
    string UploadAuthorization);

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
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count is < 1 or > ShareLimits.DefaultInternetMaxItems || sources.Count > _limits.MaxItems) throw new ArgumentOutOfRangeException(nameof(sources));
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromDays(7)) throw new ArgumentOutOfRangeException(nameof(lifetime));
        if (sources.Any(source => source.Length < 0)) throw new InvalidDataException("An Internet share item length cannot be negative.");
        var totalBytes = sources.Sum(source => source.Length);
        if (totalBytes < 1 || totalBytes > ShareLimits.DefaultInternetMaxBytes) throw new InvalidDataException("The Internet share byte limit was exceeded.");

        var shareId = Guid.NewGuid();
        var masterKey = crypto.CreateMasterKey();
        try
        {
            var expires = DateTimeOffset.UtcNow.Add(lifetime);
            var manifestItems = new List<EncryptedShareManifestItem>(sources.Count);
            var encryptedFiles = new List<(ShareFileSource Source, EncryptedShareManifestItem Item)>();
            foreach (var source in sources)
            {
                ValidateSource(source);
                var fileId = Guid.NewGuid();
                var noncePrefix = RandomNumberGenerator.GetBytes(8);
                var chunkCount = source.Length == 0 ? 0 : checked((int)Math.Ceiling(source.Length / (double)ShareChunkBytes));
                var item = new EncryptedShareManifestItem(fileId, source.DisplayName, source.MimeType, source.Length, chunkCount, source.Sha256, noncePrefix);
                manifestItems.Add(item);
                encryptedFiles.Add((source, item));
            }
            var encryptedManifest = crypto.EncryptManifest(masterKey, shareId, manifestItems);
            var session = await backend.CreateAsync(shareId, expires, sources.Count, totalBytes, cancellationToken).ConfigureAwait(false);
            await backend.UploadAsync(session, "manifest.bin", Combine(encryptedManifest.Nonce, encryptedManifest.Tag, encryptedManifest.Ciphertext), "application/octet-stream", cancellationToken).ConfigureAwait(false);

            foreach (var (source, item) in encryptedFiles)
            {
                await using var stream = await source.OpenReadAsync(cancellationToken).ConfigureAwait(false);
                for (var index = 0; index < item.ChunkCount; index++)
                {
                    var length = (int)Math.Min(ShareChunkBytes, source.Length - (long)index * ShareChunkBytes);
                    var plain = new byte[length];
                    await ReadExactlyAsync(stream, plain, cancellationToken).ConfigureAwait(false);
                    var chunk = crypto.EncryptChunk(masterKey, shareId, item.FileId, index, plain, item.NoncePrefix);
                    await backend.UploadAsync(session, string.Concat(item.FileId.ToString("N"), ".", index, ".bin"), Combine(chunk.Ciphertext, chunk.Tag), "application/octet-stream", cancellationToken).ConfigureAwait(false);
                }
            }

            var url = new Uri(string.Concat(session.DownloadBaseUrl.ToString().TrimEnd('/'), "/s/", shareId.ToString("N"), "#k=", ShareCryptoService.ToUrlFragment(masterKey)));
            return new ShareDescriptor(shareId, url, expires, sources.Count, totalBytes, true, url.Fragment);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    public Task RevokeAsync(ShareBackendUploadSession session, Guid shareId, CancellationToken cancellationToken = default) => backend.RevokeAsync(session, shareId, cancellationToken);

    private static byte[] Combine(params byte[][] values)
    {
        var length = values.Sum(value => value.Length);
        var output = new byte[length];
        var offset = 0;
        foreach (var value in values) { value.CopyTo(output, offset); offset += value.Length; }
        return output;
    }

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
            source.Length < 0 || source.Sha256.Length != 64 || source.Sha256.Any(value => !Uri.IsHexDigit(value)))
        {
            throw new InvalidDataException("A secure share source is invalid.");
        }
        ArgumentNullException.ThrowIfNull(source.OpenReadAsync);
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
        using var response = await client.PostAsJsonAsync(new Uri(baseUri, "/v1/shares"), new { shareId, expiresAtUtc, itemCount, totalBytes }, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ShareBackendUploadSession>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Secure share backend returned an empty session.");
    }

    public async Task UploadAsync(ShareBackendUploadSession session, string objectName, ReadOnlyMemory<byte> ciphertext, string contentType, CancellationToken cancellationToken = default)
    {
        using var content = new ByteArrayContent(ciphertext.ToArray());
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(session.UploadBaseUrl, objectName)) { Content = content };
        request.Headers.Add("Authorization", session.UploadAuthorization);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task RevokeAsync(ShareBackendUploadSession session, Guid shareId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri(session.UploadBaseUrl, string.Concat("../", shareId.ToString("N"))));
        request.Headers.Add("Authorization", session.UploadAuthorization);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}
