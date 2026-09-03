using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Actions;

/// <summary>
/// Keeps action exports in a predictable user-writable location and reserves output names
/// atomically so an action never overwrites an existing file.
/// </summary>
public static class ActionOutputPolicy
{
    private const int MaximumCollisionAttempts = 1_000;
    private const int MaximumStemLength = 160;
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

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
        var sanitized = string.Concat(source.Where(character =>
            character >= ' ' &&
            character != '\\' &&
            !invalidCharacters.Contains(character)));
        sanitized = sanitized.TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = fallback.Trim().TrimEnd(' ', '.');
        }

        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "DropSpace export";
        }

        if (IsReservedDeviceName(sanitized))
        {
            sanitized = string.Concat('_', sanitized);
        }

        if (sanitized.Length > MaximumStemLength)
        {
            sanitized = sanitized[..MaximumStemLength].TrimEnd(' ', '.');
        }

        return string.IsNullOrWhiteSpace(sanitized) ? "DropSpace export" : sanitized;
    }

    public static void TryDeleteIncompleteOutput(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Preserve the original action failure; cleanup is best effort for our new output only.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the original action failure; cleanup is best effort for our new output only.
        }
    }

    private static bool IsReservedDeviceName(string value)
    {
        var name = value.TrimEnd(' ', '.');
        var separator = name.IndexOf('.');
        var baseName = separator >= 0 ? name[..separator] : name;
        return ReservedDeviceNames.Contains(baseName);
    }
}
