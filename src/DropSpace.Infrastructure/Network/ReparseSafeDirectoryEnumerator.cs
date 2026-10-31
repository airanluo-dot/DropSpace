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

        cancellationToken.ThrowIfCancellationRequested();
        var budget = TransferTraversalBudget.CreateForItemLimit(maximumItems);
        budget.VisitEntry();
        var fullRoot = Path.GetFullPath(root);
        var relativeRoot = NormalizeRelativePath(rootName, TransferLimits.DefaultMaxRelativePathLength);
        return EnumerateDirectoryCore(fullRoot, relativeRoot, maximumItems, maximumBytes,
            TransferLimits.DefaultMaxRelativePathLength, budget, cancellationToken);
    }

    internal static IReadOnlyList<ReparseSafeDirectoryEntry> EnumerateSources(
        IReadOnlyList<string> sourcePaths,
        TransferLimits limits,
        TransferTraversalBudget budget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(budget);
        limits.Validate();
        if (sourcePaths.Count == 0)
            throw new ArgumentException("At least one source path is required.", nameof(sourcePaths));

        var result = new List<ReparseSafeDirectoryEntry>();
        long totalBytes = 0;
        foreach (var source in sourcePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            budget.VisitEntry();
            var full = Path.GetFullPath(source);
            if (File.Exists(full))
            {
                if (File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException("Transfer source files must not be reparse points.");

                var relative = NormalizeRelativePath(
                    TransferManifestPolicy.SafeDisplayName(Path.GetFileName(full)), limits.MaxRelativePathLength);
                var length = new FileInfo(full).Length;
                if (result.Count >= limits.MaxItems || length > limits.MaxTotalBytes - totalBytes)
                    throw new InvalidDataException("The transfer enumeration limit was exceeded.");

                result.Add(new ReparseSafeDirectoryEntry(full, relative, length));
                totalBytes += length;
                continue;
            }

            if (!Directory.Exists(full)) throw new FileNotFoundException("Transfer source is unavailable.");
            var rootName = TransferManifestPolicy.SafeDisplayName(
                Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            var relativeRoot = NormalizeRelativePath(rootName, limits.MaxRelativePathLength);
            // A directory may contain zero-byte files even when prior roots fill
            // the byte budget exactly. Its children enforce the remaining budget.
            if (result.Count >= limits.MaxItems)
                throw new InvalidDataException("The transfer enumeration limit was exceeded.");

            var entries = EnumerateDirectoryCore(full, relativeRoot,
                limits.MaxItems - result.Count, limits.MaxTotalBytes - totalBytes,
                limits.MaxRelativePathLength, budget, cancellationToken);
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(entry);
                totalBytes += entry.Length;
            }
        }

        if (result.Count == 0) throw new InvalidDataException("The transfer contains no regular files.");
        return result;
    }

    private static IReadOnlyList<ReparseSafeDirectoryEntry> EnumerateDirectoryCore(
        string fullRoot,
        string relativeRoot,
        int maximumItems,
        long maximumBytes,
        int maximumRelativePathLength,
        TransferTraversalBudget budget,
        CancellationToken cancellationToken)
    {
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
                budget.VisitEntry();
                var attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(fullRoot, entry).Replace(Path.DirectorySeparatorChar, '/');
                var manifestPath = NormalizeRelativePath(
                    string.Concat(relativeRoot, "/", relative), maximumRelativePathLength);
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

                result.Add(new ReparseSafeDirectoryEntry(entry, manifestPath, length));
                totalBytes += length;
            }
        }

        return result;
    }

    private static string NormalizeRelativePath(string path, int maximumLength)
    {
        if (path.Length > maximumLength)
        {
            throw new TransferPreparationLimitException(
                TransferPreparationLimitReason.RelativePathLength, maximumLength);
        }

        return TransferManifestPolicy.NormalizeRelativePath(path, maximumLength);
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
