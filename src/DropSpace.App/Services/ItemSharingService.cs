using System.Security.Cryptography;
using System.Text;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using DropSpace.Core.Transfer;
using DropSpace.Infrastructure.Sharing;
using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services;

/// <summary>
/// Builds bounded, read-only share sources from the existing item pipeline. It never fetches URL
/// content and it never moves or overwrites an original file.
/// </summary>
public sealed class ItemSharingService(
    IPayloadStore payloads,
    NearbyShareServer nearby,
    SecureInternetShareService internet,
    ILogger<ItemSharingService> logger)
{
    public async Task<ShareDescriptor> CreateNearbyAsync(
        IReadOnlyList<DropItem> items,
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.EnableNearbySharing) throw new InvalidOperationException("Nearby Browser Share is disabled in DropSpace settings.");
        var sources = await BuildSourcesAsync(items, cancellationToken).ConfigureAwait(false);
        var shareItems = sources.Select(source => new NearbyShareItem(
            Guid.NewGuid(),
            source.DisplayName,
            source.MimeType,
            source.Length,
            source.OpenReadAsync)).ToArray();
        logger.LogInformation("Creating a Nearby Share for {ItemCount} item(s) and {ByteCount} bytes.", shareItems.Length, shareItems.Sum(item => item.Length));
        return await nearby.CreateShareAsync(shareItems, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<ShareDescriptor> CreateInternetAsync(
        IReadOnlyList<DropItem> items,
        AppSettings settings,
        TimeSpan? lifetime = null,
        CancellationToken cancellationToken = default)
    {
        var sources = await BuildSourcesAsync(items, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Creating an encrypted Internet Share for {ItemCount} item(s) and {ByteCount} bytes.", sources.Count, sources.Sum(item => item.Length));
        return await internet.CreateAsync(
            sources.Select(source => new ShareFileSource(source.DisplayName, source.MimeType, source.Length, source.Sha256, source.OpenReadAsync)).ToArray(),
            lifetime ?? TimeSpan.FromDays(1),
            settings,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ShareSource>> BuildSourcesAsync(
        IReadOnlyList<DropItem> items,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(items));
        var result = new List<ShareSource>();
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.File is { } file)
            {
                if (file.EntryKind == FileEntryKind.Folder)
                {
                    await AddFolderAsync(result, file.OriginalPath, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await AddFileAsync(result, file.OriginalPath, Path.GetFileName(file.OriginalPath), cancellationToken).ConfigureAwait(false);
                }
                continue;
            }

            if (item.Payload is { } payload)
            {
                result.Add(new ShareSource(
                    SafeName(item.Title),
                    item.Image?.MimeType ?? "application/octet-stream",
                    payload.ByteLength,
                    payload.ContentHash,
                    token => payloads.OpenReadAsync(payload.RelativePath, token)));
                continue;
            }

            var text = item.Url?.NormalizedUrl ?? item.Text?.InlineText;
            if (!string.IsNullOrWhiteSpace(text))
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                var copy = bytes.ToArray();
                result.Add(new ShareSource(
                    SafeName(string.Concat(item.Title, ".txt")),
                    item.Url is null ? "text/plain" : "text/uri-list",
                    bytes.LongLength,
                    Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                    token => OpenBytesAsync(copy, token)));
            }
        }

        if (result.Count == 0) throw new InvalidDataException("The selected items have no shareable local payload.");
        if (result.Count > 100) throw new InvalidDataException("The share item limit was exceeded.");
        return result;
    }

    private static async Task AddFolderAsync(List<ShareSource> result, string root, CancellationToken cancellationToken)
    {
        var fullRoot = Path.GetFullPath(root);
        if (!Directory.Exists(fullRoot)) throw new FileNotFoundException("The selected folder is unavailable.");
        var rootName = SafeName(Path.GetFileName(fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        var pending = new Queue<string>();
        pending.Enqueue(fullRoot);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Dequeue();
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    pending.Enqueue(entry);
                    continue;
                }

                var relative = Path.GetRelativePath(fullRoot, entry).Replace(Path.DirectorySeparatorChar, '/');
                await AddFileAsync(result, entry, SafeName(string.Concat(rootName, " - ", relative.Replace('/', '_'))), cancellationToken).ConfigureAwait(false);
                if (result.Count > 100) throw new InvalidDataException("The share item limit was exceeded.");
            }
        }
    }

    private static async Task AddFileAsync(List<ShareSource> result, string path, string displayName, CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists) throw new FileNotFoundException("The selected file is unavailable.");
        var hash = await HashFileAsync(fullPath, cancellationToken).ConfigureAwait(false);
        result.Add(new ShareSource(
            SafeName(displayName),
            GuessMime(fullPath),
            info.Length,
            hash,
            token => OpenFileAsync(fullPath, token)));
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
    }

    private static Task<Stream> OpenFileAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81_920, FileOptions.Asynchronous | FileOptions.SequentialScan));
    }

    private static Task<Stream> OpenBytesAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    private static string SafeName(string value)
    {
        var name = string.IsNullOrWhiteSpace(value) ? "DropSpace item" : value.Replace('\0', '_').Trim();
        name = string.Concat(name.Where(character => !Path.GetInvalidFileNameChars().Contains(character)));
        return name.Length > 512 ? name[..512] : name;
    }

    private static string GuessMime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".txt" or ".md" or ".json" or ".csv" => "text/plain",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".pdf" => "application/pdf",
        _ => "application/octet-stream",
    };

    private sealed record ShareSource(
        string DisplayName,
        string MimeType,
        long Length,
        string Sha256,
        Func<CancellationToken, Task<Stream>> OpenReadAsync);
}
