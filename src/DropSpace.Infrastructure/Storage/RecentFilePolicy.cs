namespace DropSpace.Infrastructure.Storage;

public static class RecentFilePolicy
{
    private static readonly HashSet<string> TemporaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tmp", ".temp", ".part", ".partial", ".crdownload", ".download", ".filepart",
        ".opdownload", ".aria2", ".lock", ".lck", ".swp", ".swo", ".dsdownload",
        ".dropspace-reservation",
    };

    public static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    public static bool IsWithin(string path, string root) =>
        string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static bool IsCandidateName(string path)
    {
        var name = Path.GetFileName(path);
        return !string.IsNullOrWhiteSpace(name) && !name.StartsWith('~') && !name.StartsWith('.') &&
            !name.EndsWith('~') && !TemporaryExtensions.Contains(Path.GetExtension(name)) &&
            !name.Contains(".tmp.", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) &&
            !name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsInternalPackage(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith("dlc-dropspace.", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("DropSpaceSetup", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("DropSpace-win-", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("DropSpace-CUDA-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("DropSpace.exe", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("DropSpace-x64", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".msix", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("DropSpace.Identity.msix", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("llama-server.exe", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("llama-cli.exe", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("pinned-ai-runtime.zip", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("lid.176.bin", StringComparison.OrdinalIgnoreCase);
    }

    public static void ValidateLocalDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal))
            throw new ArgumentException("Choose a local directory.", nameof(path));
        var root = Path.GetPathRoot(path);
        if (OperatingSystem.IsWindows() && !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network)
            throw new ArgumentException("Network directories are not supported.", nameof(path));
        // Junction aliases could bypass the internal-resource exclusion or leave the chosen scope.
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
        {
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Linked directories are not supported.", nameof(path));
        }
    }
}
