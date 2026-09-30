namespace DropSpace.Core.Lyrics;

public static class LyricsDisplayPolicy
{
    public static LyricsPresentation Presentation(IReadOnlyList<LyricsLine> lines,
        LyricsHighlightFrame frame, TimeSpan position, int delayMilliseconds)
    {
        if (lines.Count == 0) return new(false, null, false, false);
        if (frame.Line is { } active) return new(true, active, false, false);
        var effective = position.TotalMilliseconds + Math.Clamp(delayMilliseconds, -30_000, 30_000);
        var low = 0;
        var high = lines.Count - 1;
        var previous = -1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            if (lines[middle].Start.TotalMilliseconds <= effective) { previous = middle; low = middle + 1; }
            else high = middle - 1;
        }
        // Retain the preceding lyric (and its layout) while waiting. Before the first
        // line, preview it without highlighting. Never fall back to the song title.
        var anchor = lines[Math.Max(0, previous)];
        var gapStart = previous < 0 ? 0 : Math.Max(anchor.Start.TotalMilliseconds, anchor.End.TotalMilliseconds);
        return new(true, anchor, true, effective - gapStart >= 3_000);
    }

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

public sealed record LyricsPresentation(bool HasLyrics, LyricsLine? Line, bool IsWaiting, bool IsInterlude);
