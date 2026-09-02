using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Actions;

/// <summary>
/// Keeps action exports in a predictable user-writable location and reserves output names
/// atomically so an action never overwrites an existing file.
/// </summary>
public static class ActionOutputPolicy
{
    private const int MaximumCollisionAttempts = 1_000;

    public static string ResolveDirectory(AppStoragePaths paths, string? requestedDirectory)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return string.IsNullOrWhiteSpace(requestedDirectory)
            ? paths.Exports
            : Path.GetFullPath(requestedDirectory.Trim());
    }

    public static FileStream CreateNewFile(
        string directory,
        string stem,
        string extension,
        out string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        Directory.CreateDirectory(directory);

        var safeStem = SanitizeFileName(stem, "DropSpace export");
        var normalizedExtension = extension.StartsWith(".", StringComparison.Ordinal)
            ? extension
            : string.Concat('.', extension);
        path = string.Empty;
        for (var index = 0; index < MaximumCollisionAttempts; index++)
        {
            var suffix = index == 0 ? string.Empty : string.Concat(" (", index, ")");
            var candidate = Path.Combine(directory, string.Concat(safeStem, suffix, normalizedExtension));
            try
            {
                var output = new FileStream(
                    candidate,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81_920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                path = candidate;
                return output;
            }
            catch (IOException) when (File.Exists(candidate) && index < MaximumCollisionAttempts - 1)
            {
                // Another process won this name between the existence check and CreateNew.
            }
        }

        throw new IOException("A unique DropSpace export name could not be reserved.");
    }

    public static string SanitizeFileName(string? value, string fallback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fallback);
        var source = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitized = string.Concat(source.Where(character => !invalidCharacters.Contains(character)));
        return string.IsNullOrWhiteSpace(sanitized) ? fallback : sanitized;
    }
}
