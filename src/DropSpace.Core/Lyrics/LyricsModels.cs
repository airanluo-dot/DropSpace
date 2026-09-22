using DropSpace.Core.Models;

namespace DropSpace.Core.Lyrics;

public sealed record LyricsQuery(
    string Title,
    string Artist,
    string Album,
    TimeSpan Duration,
    string TrackIdentity = "",
    string AlbumArtist = "")
{
    public IReadOnlyList<string> ArtistCandidates => LyricsMatcher.ExpandArtistCandidates(Artist, AlbumArtist);

    public bool HasDisambiguatingMetadata => ArtistCandidates.Count > 0 ||
        !string.IsNullOrWhiteSpace(Album) || Duration > TimeSpan.Zero;
}
public enum LyricsQueryStatus
{
    Disabled,
    Loading,
    Found,
    NotFound,
    Failed,
}
public sealed record LyricsQueryResult(LyricsDocument Document, LyricsQueryStatus Status);
public sealed record LyricsMatchInfo(
    string Title,
    string Artist,
    string Album,
    double DurationSeconds,
    double Score,
    string? CandidateId = null,
    string TrackIdentity = "");
public sealed record LyricsWord(string Text, TimeSpan Start, TimeSpan End);
public sealed record LyricsLine(TimeSpan Start, TimeSpan End, string Text, string? Secondary, IReadOnlyList<LyricsWord> Words);
public sealed record LyricsDocument(IReadOnlyList<LyricsLine> Lines, LyricsProviderKind Provider, LyricsMatchInfo? Match = null)
{
    public static LyricsDocument Empty { get; } = new([], LyricsProviderKind.LocalLrc);

    public LyricsDocument Bind(LyricsQuery query, string title, string artist, string album, double durationSeconds,
        double score, string? candidateId = null)
    {
        var lines = Lines;
        // Plain-text providers are represented as one unbounded line because they carry no
        // timestamps. When the media session supplies a duration, cap that line to the current
        // track so it cannot remain visible forever after playback has ended.
        if (query.Duration > TimeSpan.Zero && lines.Count == 1 &&
            lines[0].Start == TimeSpan.Zero && lines[0].End >= TimeSpan.FromHours(24))
        {
            var end = query.Duration;
            lines = [lines[0] with
            {
                End = end,
                Words = lines[0].Words.Select(word => word with { End = end }).Where(word => end > word.Start).ToArray(),
            }];
        }

        return this with
        {
            Lines = lines,
            Match = new(title, artist, album, durationSeconds, score, candidateId, query.TrackIdentity),
        };
    }
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
        if (line.End > line.Start && effective >= line.End.TotalMilliseconds) return new(null, -1, 0, ++_revision);
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
