using DropSpace.Core.Models;

namespace DropSpace.Core.Lyrics;

public sealed record LyricsQuery(string Title, string Artist, string Album, TimeSpan Duration);
public sealed record LyricsWord(string Text, TimeSpan Start, TimeSpan End);
public sealed record LyricsLine(TimeSpan Start, TimeSpan End, string Text, string? Secondary, IReadOnlyList<LyricsWord> Words);
public sealed record LyricsDocument(IReadOnlyList<LyricsLine> Lines, LyricsProviderKind Provider)
{
    public static LyricsDocument Empty { get; } = new([], LyricsProviderKind.LocalLrc);
}
public sealed record LyricsHighlightFrame(LyricsLine? Line, int WordIndex, double WordProgress, long Revision)
{
    public static LyricsHighlightFrame Empty { get; } = new(null, -1, 0, 0);
}
public interface ILyricsProvider
{
    LyricsProviderKind Kind { get; }
    Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken cancellationToken);
}

public sealed class LyricsTimelineEngine
{
    private long _revision;
    public LyricsHighlightFrame GetFrame(LyricsDocument document, TimeSpan position, int delayMilliseconds)
    {
        var effective = position.TotalMilliseconds + Math.Clamp(delayMilliseconds, -30_000, 30_000);
        var lines = document.Lines;
        var low = 0;
        var high = lines.Count - 1;
        var found = -1;
        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            if (lines[mid].Start.TotalMilliseconds <= effective) { found = mid; low = mid + 1; }
            else high = mid - 1;
        }
        if (found < 0) return new(null, -1, 0, ++_revision);
        var line = lines[found];
        var wordIndex = -1;
        double progress = 0;
        for (var index = 0; index < line.Words.Count; index++)
        {
            var word = line.Words[index];
            if (word.Start.TotalMilliseconds > effective) break;
            wordIndex = index;
            progress = Math.Clamp((effective - word.Start.TotalMilliseconds) / Math.Max(1, (word.End - word.Start).TotalMilliseconds), 0, 1);
        }
        return new(line, wordIndex, progress, ++_revision);
    }
}
