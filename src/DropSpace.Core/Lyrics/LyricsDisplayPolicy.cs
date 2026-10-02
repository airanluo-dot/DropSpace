namespace DropSpace.Core.Lyrics;

public static class LyricsDisplayPolicy
{
    // Compact owns two single-line marquees. Untimed provider blocks retain their
    // full document text/IDs elsewhere; only these display strings lose line breaks.
    public static string CompactText(string? text) => (text ?? string.Empty)
        .Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ')
        .Replace('\u0085', ' ').Replace('\u2028', ' ').Replace('\u2029', ' ');

    public static bool IntersectsViewport(double left, double top, double width, double height,
        double viewportWidth, double viewportHeight) =>
        double.IsFinite(left) && double.IsFinite(top) && double.IsFinite(width) && double.IsFinite(height) &&
        double.IsFinite(viewportWidth) && double.IsFinite(viewportHeight) &&
        width > 0 && height > 0 && viewportWidth > 0 && viewportHeight > 0 &&
        left + width > 0 && top + height > 0 && left < viewportWidth && top < viewportHeight;

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

    public static string? SecondaryPresentation(LyricsLine? line, string languageTag, bool enabled, bool showAiLabel = true)
    {
        var text = Secondary(line, languageTag, enabled);
        return showAiLabel && text is not null && line?.TranslationOrigin == LyricsTranslationOrigin.LocalAi ? "AI · " + text : text;
    }

    public static string? Secondary(LyricsLine? line, string languageTag, bool enabled)
    {
        if (!enabled || string.IsNullOrWhiteSpace(line?.Secondary)) return null;
        if (!string.IsNullOrWhiteSpace(line.TranslationLanguage))
            return LyricsTranslationPolicy.NormalizeLanguage(line.TranslationLanguage) ==
                LyricsTranslationPolicy.NormalizeLanguage(languageTag) ? line.Secondary : null;
        // An AI result without its target identity must never survive a language switch.
        if (line.TranslationOrigin == LyricsTranslationOrigin.LocalAi) return null;
        // An untimed provider block can contain several positively identified
        // translation segments. Use the same whole-block match as AI admission.
        if (line.TranslationOrigin == LyricsTranslationOrigin.Provider &&
            LyricsLanguagePolicy.ProviderTranslationMatches(line, LyricsTranslationPolicy.NormalizeLanguage(languageTag)))
            return line.Secondary;
        // Provider translations are optional. English mode must not present a
        // Chinese translation as though it matched the selected display language.
        if (languageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase) &&
            line.Secondary.EnumerateRunes().Any(rune => rune.Value is >= 0x3400 and <= 0x9fff or >= 0x20000 and <= 0x323af)) return null;
        return line.Secondary;
    }
}

public sealed record LyricsPresentation(bool HasLyrics, LyricsLine? Line, bool IsWaiting, bool IsInterlude);
