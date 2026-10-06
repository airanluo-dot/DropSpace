using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DropSpace.Core.Lyrics;

namespace DropSpace.Infrastructure.Lyrics;

/// <summary>Opt-in, local, bounded request tracing. Never records lyric bodies, headers or URL queries.</summary>
public sealed class LyricsRequestTrace : IDisposable
{
    private static readonly AsyncLocal<LyricsRequestTrace?> Current = new();
    private static readonly Channel<string>? Events = CreateSink();
    private static long _sequence;
    private readonly LyricsRequestTrace? _previous;
    private readonly string _id = $"{Environment.ProcessId}-{Interlocked.Increment(ref _sequence)}";
    private int _remaining = 512;

    private LyricsRequestTrace(object metadata)
    {
        _previous = Current.Value;
        Current.Value = this;
        Write("request", metadata);
    }

    public static string? RequestId => Current.Value?._id;
    public static bool Enabled => Events is not null;
    public static LyricsRequestTrace Begin(object metadata) => new(metadata);
    public static void Record(string stage, object data) => Current.Value?.Write(stage, data);
    public static string Key(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];
    public static object Describe(LyricsDocument document) => new
    {
        provider = document.Provider.ToString(), candidateId = document.Match?.CandidateId,
        document.ProviderDataRevision, lines = document.Lines.Count,
        bodyQuality = document.BodyQuality.ToString(), bodyQualityVersion = LyricsBodyQualityPolicy.Version,
        translated = document.Lines.Count(line => !string.IsNullOrWhiteSpace(line.Secondary)),
        languages = document.Lines.Select(line => line.TranslationLanguage).Distinct().ToArray(),
        origins = document.Lines.Select(line => line.TranslationOrigin.ToString()).Distinct().ToArray(),
    };

    public void Write(string stage, object data)
    {
        if (Events is null || Interlocked.Decrement(ref _remaining) < 0) return;
        try { Events.Writer.TryWrite(JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, request = _id, stage, data })); }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }

    public void Dispose() { Write("end", new { }); Current.Value = _previous; }

    private static Channel<string>? CreateSink()
    {
        // Explicit diagnostic sessions only; regular app launches keep listening history out of logs.
        var path = Environment.GetEnvironmentVariable("DROPSPACE_LYRICS_TRACE");
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            var events = Channel.CreateBounded<string>(new BoundedChannelOptions(2048)
                { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
            _ = Task.Run(async () =>
            {
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                long bytes = 0;
                try
                {
                    await foreach (var line in events.Reader.ReadAllAsync())
                    {
                        bytes += Encoding.UTF8.GetByteCount(line) + 1;
                        if (bytes > 8 * 1024 * 1024) break;
                        await writer.WriteLineAsync(line).ConfigureAwait(false);
                        await writer.FlushAsync().ConfigureAwait(false);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                finally { events.Writer.TryComplete(); }
            });
            return events;
        }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
