using System.Net;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Xml;
using DropSpace.Core.Lyrics;

namespace DropSpace.Infrastructure.Lyrics;

// A broken catalogue entry must not hide other already-matched recordings.
// Authentication/rate-limit rejections and cancellation remain terminal. Keep the
// last recoverable failure so exhausted candidates are not reported as a miss.
internal sealed class LyricsCandidateRequests
{
    internal const int MaximumCandidates = 6;
    private ExceptionDispatchInfo? _failure;

    // Several lyric IDs can describe the very same recording. When identity is
    // ambiguous, six duplicate bodies must not hide the other plausible singer or edit.
    internal static IEnumerable<T> OrderCatalogue<T>(IEnumerable<T> candidates, LyricsQuery query,
        Func<T, LyricsMatchInfo> describe)
    {
        var ordered = candidates.Select(item => (Item: item, Match: describe(item)))
            .Select(row => (row.Item, row.Match, Evidence: LyricsCandidateRules.RecordingEvidence(query, row.Match)))
            .OrderByDescending(row => row.Match.Score >= 4)
            .ThenByDescending(row => row.Evidence)
            .ThenBy(row => LyricsMatcher.IsSpellingMatch(query, row.Match))
            .ThenByDescending(row => row.Match.Score)
            .ThenBy(row => LyricsMatcher.DurationError(query, row.Match.DurationSeconds)).ToArray();
        if (ordered.Length == 0) yield break;
        var first = ordered[0].Match;
        var competitor = -1;
        if (ordered.Length > 1 && LyricsMatcher.NeedsRecordingComparison(query, first))
            competitor = Array.FindIndex(ordered, 1, row => row.Match.Score >= 4 && row.Evidence == ordered[0].Evidence &&
                LyricsMatcher.ConflictingCandidates(query, first, row.Match));
        yield return ordered[0].Item;
        if (competitor > 0) yield return ordered[competitor].Item;
        for (var index = 1; index < ordered.Length; index++)
            if (index != competitor) yield return ordered[index].Item;
    }

    // Compare the current search's candidates before deciding whether another
    // search term is useful. The first good body still reaches the UI immediately.
    internal static bool NeedsMoreSearch(LyricsQuery query, LyricsDocument document, string target) =>
        query.CollectSelectionCandidates || document.Lines.Count == 0 || target.Length > 0 &&
        LyricsTranslationPolicy.NeedsProviderTranslationLookup(document, target);

    public async Task<LyricsDocument> TryAsync(Func<Task<LyricsDocument>> read)
    {
        try { return await read().ConfigureAwait(false); }
        catch (Exception exception) when (CanTryNextCandidate(exception))
        {
            _failure = ExceptionDispatchInfo.Capture(exception);
            return LyricsDocument.Empty;
        }
    }

    public void ThrowIfFailed() => _failure?.Throw();

    private static bool CanTryNextCandidate(Exception exception) => exception switch
    {
        LyricsProviderRejectedException or LyricsRequestBackoffException => false,
        HttpRequestException { StatusCode: null or HttpStatusCode.NotFound or HttpStatusCode.RequestTimeout } => true,
        HttpRequestException { StatusCode: { } status } when (int)status is >= 500 and <= 599 => true,
        JsonException or XmlException or FormatException or InvalidDataException or IOException => true,
        _ => false,
    };
}

// API status codes are provider-specific and may indicate an access or rate-limit
// block. Do not treat them as a broken recording and probe other candidates.
internal sealed class LyricsProviderRejectedException(string message, int? apiCode = null) : HttpRequestException(message)
{
    public int? ApiCode { get; } = apiCode;
}
