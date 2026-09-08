using System.Collections.Concurrent;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;

namespace DropSpace.Infrastructure.Storage;

public sealed class LocalFileReferenceService : IFileReferenceService
{
    private static readonly TimeSpan RemoteMetadataTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AvailabilityCacheLifetime = TimeSpan.FromSeconds(2);
    private readonly SemaphoreSlim _localGate = new(8, 8);
    private readonly SemaphoreSlim _remoteGate = new(2, 2);
    private readonly ConcurrentDictionary<string, CachedAvailability> _availabilityCache = new(StringComparer.OrdinalIgnoreCase);

    public async Task<FileCandidate> InspectAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var remote = IsRemotePath(path);
        var gate = remote ? _remoteGate : _localGate;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var result = await ExecuteMetadataAsync(
                () => Inspect(path),
                remote,
                gate,
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    public async Task<FileAvailabilityCheck> CheckAvailabilityAsync(
        FileReference reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var key = NormalizeForComparison(reference.OriginalPath);
        if (_availabilityCache.TryGetValue(key, out var cached) &&
            DateTimeOffset.UtcNow - cached.CreatedAtUtc <= AvailabilityCacheLifetime)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return cached.Result;
        }

        var remote = IsRemotePath(reference.OriginalPath);
        var gate = remote ? _remoteGate : _localGate;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var result = await ExecuteMetadataAsync(
                () => CheckAvailability(reference),
                remote,
                gate,
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        _availabilityCache[key] = new CachedAvailability(DateTimeOffset.UtcNow, result);
        return result;
    }

    private static async Task<T> ExecuteMetadataAsync<T>(
        Func<T> operation,
        bool remote,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        if (!remote)
        {
            try
            {
                return await Task.Run(operation, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        // Cancellation cannot interrupt a synchronous Win32/SMB metadata call. Keep the
        // bounded remote gate occupied until the underlying operation exits, even when the
        // caller's five-second wait has already returned.
        var work = Task.Run(operation, CancellationToken.None);
        try
        {
            return await work.WaitAsync(RemoteMetadataTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new IOException("Remote file metadata inspection timed out.", exception);
        }
        finally
        {
            if (work.IsCompleted)
            {
                gate.Release();
            }
            else
            {
                _ = ReleaseGateAfterAsync(work, gate);
            }
        }
    }

    private static async Task ReleaseGateAfterAsync(Task work, SemaphoreSlim gate)
    {
        try { await work.ConfigureAwait(false); }
        catch (Exception) { }
        finally { gate.Release(); }
    }

    private static FileCandidate Inspect(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var title = GetTitle(fullPath);
        var entryKind = string.Equals(Path.GetExtension(fullPath), ".lnk", StringComparison.OrdinalIgnoreCase)
            ? FileEntryKind.Shortcut
            : FileEntryKind.File;
        var status = ItemStatus.Missing;
        string? reason = "File no longer exists";
        long? size = null;
        DateTimeOffset? modified = null;

        try
        {
            var attributes = File.GetAttributes(fullPath);
            var isDirectory = attributes.HasFlag(FileAttributes.Directory);
            entryKind = isDirectory ? FileEntryKind.Folder : entryKind;
            if (isDirectory)
            {
                modified = new DirectoryInfo(fullPath).LastWriteTimeUtc;
            }
            else
            {
                var info = new FileInfo(fullPath);
                size = info.Length;
                modified = info.LastWriteTimeUtc;
            }

            status = ItemStatus.Available;
            reason = null;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
        }
        catch (UnauthorizedAccessException)
        {
            status = ItemStatus.Unavailable;
            reason = "Access denied";
        }
        catch (IOException)
        {
            status = ItemStatus.Unavailable;
            reason = "Storage unavailable";
        }
        catch (NotSupportedException)
        {
            status = ItemStatus.Unavailable;
            reason = "Unsupported path";
        }

        return new FileCandidate(
            fullPath,
            NormalizeForComparison(fullPath),
            entryKind,
            title,
            entryKind == FileEntryKind.Folder ? null : Path.GetExtension(fullPath),
            size,
            modified,
            status,
            reason);
    }

    private static FileAvailabilityCheck CheckAvailability(FileReference reference)
    {
        try
        {
            var attributes = File.GetAttributes(reference.OriginalPath);
            var isDirectory = attributes.HasFlag(FileAttributes.Directory);
            var expectedDirectory = reference.EntryKind == FileEntryKind.Folder;
            return isDirectory == expectedDirectory
                ? new FileAvailabilityCheck(ItemStatus.Available, null)
                : new FileAvailabilityCheck(ItemStatus.Unavailable, "Entry type changed");
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new FileAvailabilityCheck(ItemStatus.Missing, "File no longer exists");
        }
        catch (UnauthorizedAccessException)
        {
            return new FileAvailabilityCheck(ItemStatus.Unavailable, "Access denied");
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException)
        {
            return new FileAvailabilityCheck(ItemStatus.Unavailable, "Storage unavailable");
        }
    }

    private static string NormalizeForComparison(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string GetTitle(string fullPath)
    {
        var title = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(title) ? fullPath : title;
    }

    private static bool IsRemotePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith("\\\\", StringComparison.Ordinal))
        {
            return true;
        }

        var root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root)) return false;
        try
        {
            return new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (ArgumentException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private sealed record CachedAvailability(
        DateTimeOffset CreatedAtUtc,
        FileAvailabilityCheck Result);
}
