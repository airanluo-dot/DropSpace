using DropSpace.Core.Models;

namespace DropSpace.Core.Policies;

public static class FileReferencePolicy
{
    public static FileCandidate CreateCandidate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        var isFile = File.Exists(fullPath);
        var isDirectory = Directory.Exists(fullPath);
        var kind = isDirectory
            ? FileEntryKind.Folder
            : string.Equals(Path.GetExtension(fullPath), ".lnk", StringComparison.OrdinalIgnoreCase)
                ? FileEntryKind.Shortcut
                : FileEntryKind.File;
        var status = isFile || isDirectory ? ItemStatus.Available : ItemStatus.Missing;
        var title = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(title))
        {
            title = fullPath;
        }

        long? size = null;
        DateTimeOffset? modified = null;
        string? reason = null;

        try
        {
            if (isFile)
            {
                var info = new FileInfo(fullPath);
                size = info.Length;
                modified = info.LastWriteTimeUtc;
            }
            else if (isDirectory)
            {
                modified = new DirectoryInfo(fullPath).LastWriteTimeUtc;
            }
            else
            {
                reason = "File no longer exists";
            }
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

        return new FileCandidate(
            fullPath,
            NormalizeForComparison(fullPath),
            kind,
            title,
            isDirectory ? null : Path.GetExtension(fullPath),
            size,
            modified,
            status,
            reason);
    }

    public static string NormalizeForComparison(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
