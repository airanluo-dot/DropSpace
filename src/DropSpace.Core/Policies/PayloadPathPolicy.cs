namespace DropSpace.Core.Policies;

public static class PayloadPathPolicy
{
    public static string ResolveContainedPath(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        if (Path.IsPathRooted(relativePath) || relativePath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            throw new InvalidDataException("Payload paths must be valid relative paths.");
        }

        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        var prefix = string.Concat(fullRoot, Path.DirectorySeparatorChar);

        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Payload path escapes the controlled root.");
        }

        return fullPath;
    }

    public static string CreateRelativePath(string kind, Guid id, string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        var value = id.ToString("N");
        var safeExtension = extension.StartsWith('.') ? extension : string.Concat('.', extension);
        return Path.Combine(kind, value[..2], string.Concat(value, safeExtension));
    }
}
