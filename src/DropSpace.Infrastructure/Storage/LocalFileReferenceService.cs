using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;

namespace DropSpace.Infrastructure.Storage;

public sealed class LocalFileReferenceService : IFileReferenceService
{
    public Task<FileCandidate> InspectAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Task.Run(() => Inspect(path), cancellationToken);
    }

    public Task<FileAvailabilityCheck> CheckAvailabilityAsync(
        FileReference reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return Task.Run(() => CheckAvailability(reference), cancellationToken);
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
}
