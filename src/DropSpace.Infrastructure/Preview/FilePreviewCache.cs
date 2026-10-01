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
    private long _generation;
    private bool _clearPending;

    public long Generation => Interlocked.Read(ref _generation);

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
            if (_clearPending || !File.Exists(path)) return null;
            ReparseSafePathPolicy.ResolveExistingContainedPath(paths.Root, path);
            var info = new FileInfo(path);
            if (info.Length > MaximumEntryBytes || DateTime.UtcNow - info.LastWriteTimeUtc > MaximumAge)
            {
                TryDelete(path);
                return null;
            }
            await using var stream = ReparseSafeFileOpen.OpenRead(path);
            if (stream.Length > MaximumEntryBytes) return null;
            var descriptor = await JsonSerializer.DeserializeAsync<PreviewDescriptor>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            return descriptor?.ItemId == itemId && descriptor.Kind == kind ? descriptor : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
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
            if (descriptor.CacheGeneration != Generation) return;
            if (_clearPending)
            {
                ClearOwnedFiles(cancellationToken);
                _clearPending = false;
            }
            ReparseSafePathPolicy.PrepareContainedFileDestination(paths.Root, Path.GetRelativePath(paths.Root, path));
            ReparseSafePathPolicy.PrepareContainedFileDestination(paths.Root, Path.GetRelativePath(paths.Root, temporary));
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, descriptor, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (stream.Length > MaximumEntryBytes) return;
            }
            cancellationToken.ThrowIfCancellationRequested();
            ReparseSafePathPolicy.RevalidatePreparedDestination(paths.Root, temporary);
            ReparseSafePathPolicy.RevalidatePreparedDestination(paths.Root, path);
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
            Interlocked.Increment(ref _generation);
            _clearPending = true;
            ClearOwnedFiles(cancellationToken);
            _clearPending = false;
        }
        finally { _gate.Release(); }
    }, cancellationToken);

    private void Trim(CancellationToken cancellationToken)
    {
        var entries = new List<FileInfo>();
        ReparseSafePathPolicy.ResolveExistingContainedPath(paths.Root, paths.Previews);
        foreach (var path in Directory.EnumerateFiles(paths.Previews))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsOwnedFileName(Path.GetFileName(path))) continue;
            ReparseSafePathPolicy.ResolveExistingContainedPath(paths.Root, path);
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

    private void ClearOwnedFiles(CancellationToken token)
    {
        if (!Directory.Exists(paths.Previews)) return;
        ReparseSafePathPolicy.ResolveExistingContainedPath(paths.Root, paths.Previews);
        foreach (var path in Directory.EnumerateFiles(paths.Previews))
        {
            token.ThrowIfCancellationRequested();
            if (!IsOwnedFileName(Path.GetFileName(path))) continue;
            // A failed clear must keep the cache invalidated. Best-effort eviction is
            // appropriate for Trim, but swallowing errors here can revive stale entries.
            ReparseSafePathPolicy.ResolveExistingContainedPath(paths.Root, path);
            File.Delete(path);
        }
        ReparseSafePathPolicy.ResolveExistingContainedPath(paths.Root, paths.Previews);
        if (!Directory.EnumerateFileSystemEntries(paths.Previews).Any()) Directory.Delete(paths.Previews);
    }

    private static bool IsOwnedFileName(string name)
    {
        var parts = name.Split('.');
        return parts.Length is 2 or 4 && parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit) &&
            parts[1] == "json" && (parts.Length == 2 || (parts[2].Length == 32 && parts[2].All(Uri.IsHexDigit) && parts[3] == "tmp"));
    }

    private void TryDelete(string path)
    {
        try
        {
            if (!IsOwnedFileName(Path.GetFileName(path)) || !File.Exists(path)) return;
            ReparseSafePathPolicy.ResolveExistingContainedPath(paths.Root, path);
            File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (InvalidDataException) { }
    }
}
