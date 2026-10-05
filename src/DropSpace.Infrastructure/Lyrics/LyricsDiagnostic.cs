using DropSpace.Core.Models;

namespace DropSpace.Infrastructure.Lyrics;

public enum LyricsDiagnosticStage { Search, Lyric, Query, Source, Reuse, Admission }
public enum LyricsDiagnosticOutcome { Found, NoMatch, RateLimited, Rejected, TransportFailure, Timeout, Cancelled, Malformed, Disabled }

// No titles, artists, track IDs, URLs, response messages, lyrics or model data.
public sealed record LyricsDiagnostic(LyricsProviderKind Provider, LyricsDiagnosticStage Stage,
    LyricsDiagnosticOutcome Outcome, long ElapsedMilliseconds, int Lines = 0, int TranslatedLines = 0,
    int? HttpStatus = null, int? ApiCode = null, bool TranslationLookupIncomplete = false);

internal static class LyricsDiagnostics
{
    public static LyricsDiagnosticOutcome Classify(Exception error, bool cancelled) => error switch
    {
        LyricsProviderRejectedException { ApiCode: 405 } => LyricsDiagnosticOutcome.RateLimited,
        LyricsProviderRejectedException => LyricsDiagnosticOutcome.Rejected,
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.TooManyRequests } => LyricsDiagnosticOutcome.RateLimited,
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden } => LyricsDiagnosticOutcome.Rejected,
        OperationCanceledException => cancelled ? LyricsDiagnosticOutcome.Cancelled : LyricsDiagnosticOutcome.Timeout,
        System.Text.Json.JsonException or InvalidDataException or FormatException or System.Xml.XmlException => LyricsDiagnosticOutcome.Malformed,
        _ => LyricsDiagnosticOutcome.TransportFailure,
    };

    public static void Report(Action<LyricsDiagnostic>? observer, LyricsDiagnostic value)
    {
        LyricsRequestTrace.Record("diagnostic", value);
        try { observer?.Invoke(value); }
        catch (Exception error) when (error is not OutOfMemoryException) { /* Diagnostics cannot break provider work. */ }
    }
}
