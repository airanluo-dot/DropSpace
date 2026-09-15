namespace DropSpace.Core.Lyrics;

public static class LyricsDisplayPolicy
{
    public static string? Secondary(LyricsLine? line, string languageTag, bool enabled)
    {
        if (!enabled || string.IsNullOrWhiteSpace(line?.Secondary)) return null;
        // Provider translations are optional. English mode must not present a
        // Chinese translation as though it matched the selected display language.
        if (languageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase) &&
            line.Secondary.EnumerateRunes().Any(rune => rune.Value is >= 0x3400 and <= 0x9fff or >= 0x20000 and <= 0x323af)) return null;
        return line.Secondary;
    }
}
