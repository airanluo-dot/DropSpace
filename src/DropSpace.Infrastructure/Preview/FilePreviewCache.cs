using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DropSpace.Core.Preview;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Preview;

public sealed class FilePreviewCache(AppStoragePaths paths) : IPreviewCache
{
    internal const long MaximumEntryBytes = 16L * 1024 * 1024;
    internal const long MaximumCacheBytes = 64L * 1024 * 1024;
    internal const int MaximumEntries = 64;
    internal static readonly TimeSpan MaximumAge = TimeSpan.FromDays(1);
    private readonly SemaphoreSlim _gate = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public async Task<PreviewDescriptor?> TryGetAsync(
        Guid itemId,
        int revision,
        PreviewKind kind,
        int page,
        int targetPixelWidth,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var path = GetPath(itemId, revision, kind, page, targetPixelWidth);
        try
        {
            if (!File.Exists(path)) return null;
            var info = new FileInfo(path);
            if (info.Length > MaximumEntryBytes || DateTime.UtcNow - info.LastWriteTimeUtc > MaximumAge)
            {
                TryDelete(path);
                return null;
            }
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > MaximumEntryBytes) return null;
            var descriptor = await JsonSerializer.DeserializeAsync<PreviewDescriptor>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            return descriptor?.ItemId == itemId && descriptor.Kind == kind ? descriptor : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            TryDelete(path);
            return null;
        }
        finally { _gate.Release(); }
    }

    public Task PutAsync(PreviewRequest request, PreviewDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Item.HasExternalSource || descriptor.Bytes?.LongLength > MaximumEntryBytes / 2 ||
            descriptor.Text?.Length > MaximumEntryBytes / 6)
            return Task.CompletedTask;
        // Directory enumeration and metadata operations must not execute on the UI thread.
        return Task.Run(() => PutCoreAsync(request, descriptor, cancellationToken), cancellationToken);
    }

    private async Task PutCoreAsync(PreviewRequest request, PreviewDescriptor descriptor, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var path = GetPath(request.Item.Id, request.Item.Revision, descriptor.Kind, request.Page, request.TargetPixelWidth);
        var temporary = string.Concat(path, ".", Guid.NewGuid().ToString("N"), ".tmp");
        try
        {
            Directory.CreateDirectory(paths.Previews);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, descriptor, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (stream.Length > MaximumEntryBytes) return;
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
            Trim(cancellationToken);
        }
        finally
        {
            TryDelete(temporary);
            _gate.Release();
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Directory.Exists(paths.Previews)) Directory.Delete(paths.Previews, recursive: true);
        }
        finally { _gate.Release(); }
    }, cancellationToken);

    private void Trim(CancellationToken cancellationToken)
    {
        var entries = new List<FileInfo>();
        foreach (var path in Directory.EnumerateFiles(paths.Previews))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (info.Extension != ".json" || info.Length > MaximumEntryBytes || DateTime.UtcNow - info.LastWriteTimeUtc > MaximumAge)
                TryDelete(path);
            else entries.Add(info);
        }
        long retainedBytes = 0;
        var retainedCount = 0;
        foreach (var entry in entries.OrderByDescending(entry => entry.LastWriteTimeUtc))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++retainedCount > MaximumEntries || retainedBytes + entry.Length > MaximumCacheBytes)
                TryDelete(entry.FullName);
            else retainedBytes += entry.Length;
        }
    }

    private string GetPath(Guid itemId, int revision, PreviewKind kind, int page, int targetPixelWidth)
    {
        var key = string.Concat(itemId.ToString("N"), "|", revision, "|", (int)kind, "|", page, "|", targetPixelWidth);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Path.Combine(paths.Previews, string.Concat(hash, ".json"));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
