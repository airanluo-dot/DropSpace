namespace DropSpace.Core.Models;

public static class SettingsValidationPolicy
{
    public const int CurrentVersion = 11;
    public const int MinimumVersion = 1;

    public const int MinimumRetentionDays = 1;
    public const int MaximumRetentionDays = 3_650;
    public const int MinimumRetentionItemCount = 10;
    public const int MaximumRetentionItemCount = 100_000;
    public const long MinimumMaxImageBytes = 1_048_576;
    public const long MaximumMaxImageBytes = 268_435_456;
    public const long MinimumMaxImagePixels = 1_000_000;
    public const long MaximumMaxImagePixels = 200_000_000;
    public const long MinimumClipboardFileBytes = 1_048_576;
    public const long MaximumClipboardFileBytes = 1_099_511_627_776;
    public const long MinimumClipboardFileTotalBytes = 1_048_576;
    public const long MaximumClipboardFileTotalBytes = 4_398_046_511_104;
    public const int MinimumClipboardFileItems = 1;
    public const int MaximumClipboardFileItems = 1_000;
    public const int MinimumTextCharacters = 1_024;
    public const int MaximumTextCharacters = 16_777_216;
    public const int MaximumCustomPlacements = 64;
    public const int MaximumPlacementKeyLength = 256;
    public const double MaximumPlacementCoordinate = 100_000;
    public const int MaximumQuickPanelHotkeyLength = 128;
    public const int MaximumSmartDragExcludedProcesses = 128;
    public const int MaximumSmartDragProcessLength = 260;

    private static readonly string[] CanonicalModifierOrder = ["Win", "Ctrl", "Alt", "Shift"];

    public static string? CanonicalizeHotkey(string? gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture) || gesture.Length > MaximumQuickPanelHotkeyLength)
        {
            return null;
        }

        var parts = gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        var modifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? key = null;
        foreach (var part in parts)
        {
            var modifier = part switch
            {
                _ when part.Equals("Win", StringComparison.OrdinalIgnoreCase) => "Win",
                _ when part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) => "Ctrl",
                _ when part.Equals("Alt", StringComparison.OrdinalIgnoreCase) => "Alt",
                _ when part.Equals("Shift", StringComparison.OrdinalIgnoreCase) => "Shift",
                _ => null,
            };
            if (modifier is not null)
            {
                if (!modifiers.Add(modifier))
                {
                    return null;
                }

                continue;
            }

            if (key is not null ||
                !(part.Equals("Space", StringComparison.OrdinalIgnoreCase) ||
                  part.Length == 1 && char.IsAsciiLetterOrDigit(part[0])))
            {
                return null;
            }

            key = part.Equals("Space", StringComparison.OrdinalIgnoreCase)
                ? "Space"
                : char.ToUpperInvariant(part[0]).ToString();
        }

        if (modifiers.Count == 0 || key is null)
        {
            return null;
        }

        var orderedModifiers = CanonicalModifierOrder
            .Where(modifiers.Contains);
        return string.Join('+', orderedModifiers.Append(key));
    }
}
