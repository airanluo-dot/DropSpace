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
