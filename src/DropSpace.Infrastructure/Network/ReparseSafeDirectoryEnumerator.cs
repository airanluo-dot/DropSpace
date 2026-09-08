using DropSpace.Core.Policies;
using DropSpace.Core.Transfer;

namespace DropSpace.Infrastructure.Network;

public sealed record ReparseSafeDirectoryEntry(string FullPath, string RelativePath, long Length);

/// <summary>
/// Enumerates a user-selected directory without delegating traversal to the
/// platform's recursive enumerator. Reparse points are skipped at every level;
/// the selected root itself is rejected when it is a reparse point.
/// </summary>
public static class ReparseSafeDirectoryEnumerator
{
    public static IReadOnlyList<ReparseSafeDirectoryEntry> Enumerate(
        string root,
        string rootName,
        int maximumItems,
        long maximumBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumItems);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);

        var fullRoot = Path.GetFullPath(root);
        EnsureExistsAndNotReparse(fullRoot);
        var pending = new Stack<string>();
        var visitedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ReparseSafeDirectoryEntry>();
        long totalBytes = 0;
        pending.Push(fullRoot);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            if (!visitedDirectories.Add(current))
            {
                continue;
            }

            EnsureExistsAndNotReparse(current);
            foreach (var entry in Directory.EnumerateFileSystemEntries(current, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    pending.Push(entry);
                    continue;
                }

                var length = new FileInfo(entry).Length;
                if (result.Count >= maximumItems || length > maximumBytes - totalBytes)
                {
                    throw new InvalidDataException("The transfer enumeration limit was exceeded.");
                }

                var relative = Path.GetRelativePath(fullRoot, entry).Replace(Path.DirectorySeparatorChar, '/');
                result.Add(new ReparseSafeDirectoryEntry(
                    entry,
                    TransferManifestPolicy.NormalizeRelativePath(string.Concat(rootName, "/", relative)),
                    length));
                totalBytes += length;
            }
        }

        return result;
    }

    private static void EnsureExistsAndNotReparse(string path)
    {
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("Transfer source directories must not be reparse points.");
        }

        if (!attributes.HasFlag(FileAttributes.Directory))
        {
            throw new InvalidDataException("Transfer source root must be a directory.");
        }
    }
}
