using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace DropSpace.Infrastructure.Lyrics;

/// <summary>Opt-in in-process metrics. Only fixed stage/outcome labels and numbers; no lyric, prompt,
/// track, model path, request ID or exception text. No exporter or persistence is installed here.</summary>
public static class PlainLyricsMetrics
{
    public const string MeterName = "DropSpace.PlainLyrics.Host";
    public const string DurationName = "dropspace.plain_lyrics.host.duration";
    public const string CountName = "dropspace.plain_lyrics.host.events";
    private static readonly Meter Meter = new(MeterName, "1.0");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(DurationName, "ms");
    private static readonly Counter<long> Events = Meter.CreateCounter<long>(CountName);

    internal enum Stage { Song, FirstUseful, Infer, OperationQueue, NativeQueue, RuntimeResolve, LoadReady, Call, CancellationRelease }
    internal enum Outcome { Success, Failed, Cancelled, NoUseful }
    internal enum Event { SegmentHit, SongHit, NeutralHit, FallbackAttempt, FallbackUse, ResidentReuse }

    internal static Measurement Measure(Stage stage, CancellationToken token = default) => new(stage, token);

    internal static void Record(Stage stage, long started, Outcome outcome)
    {
        try { Duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            new("stage", stage.ToString()), new("outcome", outcome.ToString())); }
        catch (Exception error) when (error is not OutOfMemoryException) { /* A listener cannot break ownership. */ }
    }

    internal static void Count(Event value)
    {
        try { Events.Add(1, new KeyValuePair<string, object?>("event", value.ToString())); }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }

    internal sealed class Measurement(Stage stage, CancellationToken token) : IDisposable
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        private Outcome _outcome = Outcome.Failed;
        private int _finished;
        internal void Complete(Outcome outcome = Outcome.Success) => _outcome = outcome;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _finished, 1) != 0) return;
            Record(stage, _started, _outcome == Outcome.Failed && token.IsCancellationRequested
                ? Outcome.Cancelled : _outcome);
        }
    }
}
