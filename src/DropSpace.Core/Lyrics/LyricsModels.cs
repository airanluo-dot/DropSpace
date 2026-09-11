using System.Text;

namespace DropSpace.Core.Lyrics;

public enum LyricsMode
{
    Online,
    LocalLrc,
}

public enum LyricsProviderKind
{
    NetEase,
    QqMusic,
    Kugou,
    Lrclib,
    Amll,
    LocalLrc,
}

public sealed record LyricsQuery(string Title, string Artist, string Album, TimeSpan Duration)
{
    public string NormalizedTitle => LyricsParser.NormalizeQueryPart(Title);

    public string NormalizedArtist => LyricsParser.NormalizeQueryPart(Artist);
}

public sealed record LyricsWord(string Text, TimeSpan Start, TimeSpan End);

public sealed record LyricsLine(
    TimeSpan Start,
    TimeSpan End,
    string PrimaryText,
    string? SecondaryText = null,
    IReadOnlyList<LyricsWord>? Words = null)
{
    public IReadOnlyList<LyricsWord> WordTimings { get; init; } = Words ?? [];
}

public sealed record LyricsDocument(
    IReadOnlyList<LyricsLine> Lines,
    string Provider,
    DateTimeOffset RetrievedAt,
    int OffsetMilliseconds = 0)
{
    public static LyricsDocument Empty { get; } = new([], string.Empty, DateTimeOffset.MinValue);

    public LyricsLine? FindCurrent(TimeSpan position, TimeSpan delay)
    {
        var effective = position + delay;
        if (effective < TimeSpan.Zero) effective = TimeSpan.Zero;
        return Lines.LastOrDefault(line => line.Start <= effective);
    }
}

public sealed record LyricsResult(bool Success, LyricsDocument Document, string? Error = null)
{
    public static LyricsResult NotFound(string? error = null) => new(false, LyricsDocument.Empty, error);
}

public interface ILyricsProvider
{
    LyricsProviderKind Kind { get; }

    Task<LyricsResult> QueryAsync(LyricsQuery query, CancellationToken cancellationToken = default);
}

public static class LyricsParser
{
    public static IReadOnlyList<LyricsLine> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var lines = new List<LyricsLine>();
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var cursor = 0;
            var timestamps = new List<TimeSpan>();
            while (cursor < rawLine.Length && rawLine[cursor] == '[')
            {
                var end = rawLine.IndexOf(']', cursor + 1);
                if (end <= cursor) break;
                var token = rawLine[(cursor + 1)..end];
                if (TryParseTimestamp(token, out var timestamp)) timestamps.Add(timestamp);
                cursor = end + 1;
            }

            if (timestamps.Count == 0) continue;
            var content = rawLine[cursor..].Trim();
            if (content.StartsWith("[", StringComparison.Ordinal)) continue;
            foreach (var timestamp in timestamps)
            {
                lines.Add(new LyricsLine(timestamp, timestamp, content));
            }
        }

        lines.Sort((left, right) => left.Start.CompareTo(right.Start));
        for (var index = 0; index < lines.Count; index++)
        {
            var end = index + 1 < lines.Count ? lines[index + 1].Start : lines[index].Start + TimeSpan.FromSeconds(4);
            lines[index] = lines[index] with { End = end > lines[index].Start ? end : lines[index].Start + TimeSpan.FromMilliseconds(250) };
        }

        return lines;
    }

    public static string NormalizeQueryPart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = value.Normalize(NormalizationForm.FormKC).Trim();
        return string.Join(' ', normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool TryParseTimestamp(string token, out TimeSpan timestamp)
    {
        timestamp = TimeSpan.Zero;
        var parts = token.Split(':', 2);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var minutes)) return false;
        var secondsPart = parts[1].Replace(',', '.');
        if (!double.TryParse(secondsPart, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds) || seconds < 0)
        {
            return false;
        }

        timestamp = TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
        return timestamp >= TimeSpan.Zero;
    }
}
