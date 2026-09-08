using DropSpace.Core.Policies;

namespace DropSpace.Infrastructure.Storage;

public static class ReparseSafePathPolicy
{
    public static string ResolveOwnedFilePathForDeletion(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var fullRoot = Path.GetFullPath(root);
        var candidate = PayloadPathPolicy.ResolveContainedPath(fullRoot, relativePath);
        EnsureExistingParentsDoNotTraverseReparsePoints(fullRoot, candidate);
        return candidate;
    }

    public static string ResolveExistingContainedPath(string root, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullRoot = Path.GetFullPath(root);
        var relative = Path.GetRelativePath(fullRoot, Path.GetFullPath(path));
        var candidate = PayloadPathPolicy.ResolveContainedPath(fullRoot, relative);
        EnsureExistingPathDoesNotTraverseReparsePoints(fullRoot, candidate);
        return candidate;
    }

    public static string PrepareContainedFileDestination(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        var fullRoot = Path.GetFullPath(root);
        Directory.CreateDirectory(fullRoot);
        EnsureNotReparsePoint(fullRoot);

        var candidate = PayloadPathPolicy.ResolveContainedPath(fullRoot, relativePath);
        var parent = Path.GetDirectoryName(candidate)
            ?? throw new InvalidDataException("The destination has no parent directory.");
        var parentRelative = Path.GetRelativePath(fullRoot, parent);
        if (!string.Equals(parentRelative, ".", StringComparison.Ordinal))
        {
            var current = fullRoot;
            foreach (var segment in parentRelative.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (Directory.Exists(current))
                {
                    EnsureNotReparsePoint(current);
                    continue;
                }

                Directory.CreateDirectory(current);
                EnsureNotReparsePoint(current);
            }
        }

        EnsureExistingPathDoesNotTraverseReparsePoints(fullRoot, parent);
        if (File.Exists(candidate) || Directory.Exists(candidate))
        {
            EnsureNotReparsePoint(candidate);
        }
        return candidate;
    }

    public static void RevalidatePreparedDestination(string root, string destination)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullDestination = Path.GetFullPath(destination);
        _ = PayloadPathPolicy.ResolveContainedPath(fullRoot, Path.GetRelativePath(fullRoot, fullDestination));
        var parent = Path.GetDirectoryName(fullDestination)
            ?? throw new InvalidDataException("The destination has no parent directory.");
        EnsureExistingPathDoesNotTraverseReparsePoints(fullRoot, parent);
        if (File.Exists(fullDestination) || Directory.Exists(fullDestination))
        {
            EnsureNotReparsePoint(fullDestination);
        }
    }

    private static void EnsureExistingPathDoesNotTraverseReparsePoints(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root);
        var current = Path.GetFullPath(path);
        while (true)
        {
            EnsureNotReparsePoint(current);
            if (string.Equals(current, fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var parent = Path.GetDirectoryName(current);
            if (parent is null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The path escaped its trusted root.");
            }
            current = parent;
        }
    }

    private static void EnsureExistingParentsDoNotTraverseReparsePoints(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root);
        var current = Path.GetFullPath(path);
        while (true)
        {
            try
            {
                EnsureNotReparsePoint(current);
            }
            catch (FileNotFoundException)
            {
                // A missing leaf is an idempotent cleanup success. Continue with
                // existing parents so a missing path cannot hide a reparse point.
            }
            catch (DirectoryNotFoundException)
            {
                // The parent chain is checked on the next iteration.
            }

            if (string.Equals(current, fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var parent = Path.GetDirectoryName(current);
            if (parent is null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The path escaped its trusted root.");
            }

            current = parent;
        }
    }

    private static void EnsureNotReparsePoint(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException("Trusted file paths must not traverse reparse points.");
        }
    }
}
