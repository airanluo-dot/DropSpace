namespace DropSpace.Core.Lyrics;

/// <summary>Read-only next-line selection for the expanded island's lyric preview.</summary>
public static class LyricsPreviewPolicy
{
    public static LyricsLine? NextLine(IReadOnlyList<LyricsLine> lines,
        LyricsHighlightFrame frame, TimeSpan position, int delayMilliseconds)
    {
        if (lines.Count == 0) return null;
        var time = position + TimeSpan.FromMilliseconds(Math.Clamp(delayMilliseconds, -30_000, 30_000));
        var cue = LyricsPlaybackTimeline.For(lines).At(time);
        if (cue.Anchor < 0 || cue.Next < 0) return null;
        // Position can arrive before its new frame during a seek. A translation-only
        // replacement keeps the original cue, so its preview may continue immediately.
        var anchor = lines[cue.Anchor];
        if (frame.Line is { } current && (current.Start != anchor.Start || current.IsBackground != anchor.IsBackground ||
            !string.Equals(current.Text, anchor.Text, StringComparison.Ordinal))) return null;
        return lines[cue.Next];
    }
}
