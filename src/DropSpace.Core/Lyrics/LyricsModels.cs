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
    public string? PreferredTranslationLanguage { get; init; }
    public bool BypassProviderResponseCache { get; init; }
    public bool CollectSelectionCandidates { get; init; }

    // AlbumArtist often describes a compilation or the lead performer, rather
    // than this recording's complete vocal credits. It supplies missing SMTC
    // metadata; it cannot replace credits that are already present.
    public IReadOnlyList<string> ArtistCandidates => LyricsMatcher.ExpandRecordingArtistCandidates(Title,
        string.IsNullOrWhiteSpace(Artist) ? AlbumArtist : Artist);

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
    Ambiguous,
}
public sealed record LyricsQueryResult(LyricsDocument Document, LyricsQueryStatus Status,
    bool TranslationLookupIncomplete = false)
{
    public LyricsCandidateSnapshot SelectionCandidates { get; init; } = LyricsCandidateSnapshot.Empty;
    public IReadOnlyList<LyricsProviderKind> AmbiguitySources { get; init; } = [];
}
public sealed record LyricsMatchInfo(
    string Title,
    string Artist,
    string Album,
    double DurationSeconds,
    double Score,
    string? CandidateId = null,
    string TrackIdentity = "")
{
    // Complete credit projections asserted by this recording's source artist records.
    // They are not global aliases and never alter the raw canonical Artist field.
    public IReadOnlyList<string> ArtistAliases { get; init; } = [];
    public string CanonicalTitle { get; init; } = string.Empty;
    public IReadOnlyList<string> TitleAliases { get; init; } = [];
}
public enum LyricsLineTranslationState { Pending, Translated, Skipped, Failed }
public sealed record LyricsWord(string Text, TimeSpan Start, TimeSpan End);
public sealed record LyricsProviderTranslation(string Text, string? Language, bool? LanguageIsExplicit);
public sealed record LyricsLine(TimeSpan Start, TimeSpan End, string Text, string? Secondary, IReadOnlyList<LyricsWord> Words)
{
    public bool IsBackground { get; init; }
    public LyricsProviderTranslation? OriginalProviderTranslation { get; init; }
    public string? SourceLanguage { get; init; }
    public LyricsTranslationOrigin TranslationOrigin { get; init; }
    public string? TranslationLanguage { get; init; }
    // null is legacy/unspecified provenance, false is inferred and must be
    // re-evaluated by the current policy, true is the provider's explicit tag.
    public bool? TranslationLanguageIsExplicit { get; init; }
    // Current admitted source projection, set only after host output validation.
    public string? LocalAiAdmissionKey { get; init; }
    public LyricsLineTranslationState TranslationState { get; init; }
    public string? TranslationReason { get; init; }
}
public sealed record LyricsDocument(IReadOnlyList<LyricsLine> Lines, LyricsProviderKind Provider, LyricsMatchInfo? Match = null)
{
    // Session-only. Persistent source caches must re-enter the current model/rules.
    [System.Text.Json.Serialization.JsonIgnore]
    public LyricsWholeTrackAdmission? TranslationAdmission { get; init; }
    public int TimingRevision { get; init; }
    public bool? DeclaresSynchronizedTiming { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasSynchronizedTiming => DeclaresSynchronizedTiming ??
        (Lines.Count > 1 || Lines.Count == 1 && (Lines[0].Start > TimeSpan.Zero || Lines[0].Words.Count > 0));
    // Provider parser revision, persisted so previously dropped translations can be refreshed once.
    public int ProviderDataRevision { get; init; }
    public bool SourceSearchIncomplete { get; init; }
    public LyricsBodyQuality BodyQuality { get; init; } = Lines.Count == 0 ? LyricsBodyQuality.NoLyrics : LyricsBodyQuality.Usable;
    public static LyricsDocument Empty { get; } = new([], LyricsProviderKind.LocalLrc);

    public LyricsDocument Bind(LyricsQuery query, string title, string artist, string album, double durationSeconds,
        double score, string? candidateId = null)
    {
        var lines = Lines;
        // Plain-text providers are represented as one unbounded line because they carry no
        // timestamps. When the media session supplies a duration, cap that line to the current
        // track so it cannot remain visible forever after playback has ended.
        if (!HasSynchronizedTiming && query.Duration > TimeSpan.Zero && lines.Count == 1 &&
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

// Providers that search multiple candidates can expose a usable original before
// looking for a translated alternative. The service owns the shared time budget.
public interface IProgressiveLyricsProvider : ILyricsProvider
{
    Task<LyricsDocument> QueryAsync(LyricsQuery query, CancellationToken cancellationToken,
        Action<LyricsDocument> reportCandidate);
}

public sealed class LyricsTimelineEngine
{
    private long _revision;
    public LyricsHighlightFrame GetFrame(LyricsDocument document, TimeSpan position, int delayMilliseconds)
    {
        var time = position + TimeSpan.FromMilliseconds(Math.Clamp(delayMilliseconds, -30_000, 30_000));
        var cue = LyricsPlaybackTimeline.For(document.Lines).At(time);
        if (cue.Active < 0) return new(null, -1, 0, ++_revision);
        var line = document.Lines[cue.Active];
        var activeWord = -1;
        var previousWord = -1;
        for (var index = 0; index < line.Words.Count; index++)
        {
            var word = line.Words[index];
            // Word indices are display order. Older cached TTML can contain backing
            // words appended after later lead words, and authored word intervals may overlap.
            if (word.Start > time) continue;
            if (previousWord < 0 || word.Start >= line.Words[previousWord].Start) previousWord = index;
            if (time < word.End && (activeWord < 0 || word.Start >= line.Words[activeWord].Start)) activeWord = index;
        }
        var indexToHighlight = activeWord < 0 ? previousWord : activeWord;
        if (indexToHighlight < 0) return new(line, -1, 0, ++_revision);
        var highlighted = line.Words[indexToHighlight];
        var progress = (time - highlighted.Start).TotalMilliseconds / Math.Max(1, (highlighted.End - highlighted.Start).TotalMilliseconds);
        return new(line, indexToHighlight, Math.Clamp(progress, 0, 1), ++_revision);
    }
}
