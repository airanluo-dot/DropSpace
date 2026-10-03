namespace DropSpace.Core.Lyrics;

/// <summary>Read-only next-line selection for the expanded island's lyric preview.</summary>
public static class LyricsPreviewPolicy
{
    public static LyricsLine? NextLine(IReadOnlyList<LyricsLine> lines,
        LyricsHighlightFrame frame, TimeSpan position, int delayMilliseconds)
    {
        if (lines.Count == 0) return null;
        var current = LyricsDisplayPolicy.Presentation(lines, frame, position, delayMilliseconds).Line;
        var timed = LyricsDisplayPolicy.Presentation(lines, LyricsHighlightFrame.Empty, position, delayMilliseconds).Line;
        if (current is null || timed is null || current.Start != timed.Start) return null;

        // A seek can publish Position before its new highlight frame. Do not show an
        // already-playing/previous line while that frame catches up. Translation-only
        // document replacement is safe because the original text and timing are stable.
        var low = 0;
        var high = lines.Count;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (lines[middle].Start <= current.Start) low = middle + 1;
            else high = middle;
        }

        var belongsToDocument = false;
        for (var index = low - 1; index >= 0 && lines[index].Start == current.Start; index--)
            belongsToDocument |= string.Equals(lines[index].Text, current.Text, StringComparison.Ordinal);
        if (!belongsToDocument) return null;

        // Skip the entire current timestamp group (including word-synced variants).
        // For the next timestamp group choose its last nonempty original, matching
        // the timeline's last-at-a-timestamp rule without previewing blank LRC cues.
        while (low < lines.Count)
        {
            var start = lines[low].Start;
            LyricsLine? next = null;
            do
            {
                if (!string.IsNullOrWhiteSpace(lines[low].Text)) next = lines[low];
                low++;
            }
            while (low < lines.Count && lines[low].Start == start);
            if (next is not null) return next;
        }
        return null;
    }
}
