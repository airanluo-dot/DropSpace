// Ported from NovaClip Beta8, b798c571dad1cae03c16129e4b4c43b85b428d7a.
using System.Text;
using DropSpace.Core.Downloads;

namespace DropSpace.Infrastructure.Downloads;

public sealed class FileNameSanitizer : IFileNameSanitizer
{
    private static readonly char[] InvalidCharacters = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).Distinct().ToArray();
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public string Sanitize(string value, string fallback = "video")
    {
        var candidate = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        var builder = new StringBuilder(candidate.Length);
        foreach (var character in candidate)
        {
            builder.Append(InvalidCharacters.Contains(character) || char.IsControl(character) ? '_' : character);
        }

        candidate = builder.ToString().Trim().TrimEnd('.', ' ');
        if (candidate.Length == 0) candidate = fallback;
        if (ReservedNames.Contains(candidate.Split('.', 2)[0].TrimEnd())) candidate = "_" + candidate;
        return candidate.Length > 180 ? candidate[..180].TrimEnd('.', ' ') : candidate;
    }

    public string GetAvailablePath(string directory, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (!Path.IsPathRooted(directory) || Path.GetFileName(fileName) != fileName) throw new ArgumentException("A rooted directory and a single file name are required.");
        Directory.CreateDirectory(directory);
        // This method is retained for compatibility only. It deliberately does not
        // claim a path is free; callers must use IOutputReservationService.
        return Path.Combine(directory, fileName);
    }
}
