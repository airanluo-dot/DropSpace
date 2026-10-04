using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;

namespace DropSpace.Infrastructure.Lyrics;

// Independent implementation of KRC's documented byte/timing layout. Translation rows
// are positional; never align an incomplete array to a different original line.
internal static class KugouKrcParser
{
    private const int MaximumPackedBytes = 256 * 1024;
    private const int MaximumDecodedBytes = 1024 * 1024;
    private const int MaximumRows = 10_000;
    private static readonly byte[] Key = [64, 71, 97, 119, 94, 50, 116, 71, 81, 54, 49, 45, 206, 210, 110, 105];
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static LyricsDocument Parse(string content, string? target, CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        void Check()
        {
            token.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromMilliseconds(500))
                throw new InvalidDataException("KRC parsing exceeded its processing budget.");
        }
        Check();
        if (content.Length > (MaximumPackedBytes + 2) / 3 * 4)
            throw new InvalidDataException("KRC packed content exceeds limit.");
        var packed = Convert.FromBase64String(content);
        if (packed.Length > MaximumPackedBytes) throw new InvalidDataException("KRC packed content exceeds limit.");
        // Some servers return LRC even for a KRC request. Use that response directly;
        // a second HTTP request would not add information.
        if (!packed.AsSpan().StartsWith("krc1"u8))
        {
            var text = Utf8.GetString(packed);
            if (!text.TrimStart().StartsWith('[')) throw new InvalidDataException("Unknown Kugou lyric format.");
            Check();
            var plain = LyricsParser.Parse(text, LyricsProviderKind.Kugou);
            Check();
            return plain;
        }
        for (var index = 4; index < packed.Length; index++)
        {
            if ((index & 4095) == 0) Check();
            packed[index] ^= Key[(index - 4) % Key.Length];
        }
        using var input = new MemoryStream(packed, 4, packed.Length - 4, false);
        using var inflate = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var chunk = new byte[4096];
        var maximumInflated = Math.Min(MaximumDecodedBytes, Math.Max(64 * 1024, (packed.Length - 4) * 128));
        int count;
        while ((count = inflate.Read(chunk)) > 0)
        {
            Check();
            if (output.Length + count > maximumInflated) throw new InvalidDataException("KRC inflation exceeds limit.");
            output.Write(chunk, 0, count);
        }
        Check();
        var decoded = Utf8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
        var rows = new List<LyricsLine>();
        string? language = null;
        foreach (var raw in decoded.Split('\n'))
        {
            Check();
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.StartsWith("[language:", StringComparison.Ordinal) && line.EndsWith(']'))
            { language = line[10..^1]; continue; }
            if (!line.StartsWith('[')) continue;
            var headerEnd = line.IndexOf(']');
            if (headerEnd < 0) throw new InvalidDataException("Incomplete KRC line header.");
            var header = line.AsSpan(1, headerEnd - 1);
            if (!header.Contains(',')) continue; // artist/title/offset metadata is not sung text
            var pair = header.ToString().Split(',');
            if (pair.Length != 2) throw new InvalidDataException("Invalid KRC line header.");
            var start = Milliseconds(pair[0]);
            var end = start + Milliseconds(pair[1]);
            if (end > TimeSpan.FromHours(24)) throw new InvalidDataException("Invalid KRC line end.");
            var body = line[(headerEnd + 1)..];
            if (body.Length > 0 && end <= start) throw new InvalidDataException("Invalid KRC line duration.");
            var words = new List<LyricsWord>();
            if (!body.StartsWith('<')) rows.Add(new(start, end, body, null, []));
            else
            {
                var position = 0;
                while (position < body.Length)
                {
                    Check();
                    if (words.Count >= 2048 || body[position] != '<') throw new InvalidDataException("Invalid KRC word sequence.");
                    var close = body.IndexOf('>', position + 1);
                    if (close < 0) throw new InvalidDataException("Incomplete KRC word header.");
                    var timing = body[(position + 1)..close].Split(',');
                    if (timing.Length != 3 || !int.TryParse(timing[2], NumberStyles.None, CultureInfo.InvariantCulture, out _))
                        throw new InvalidDataException("Invalid KRC word header.");
                    var wordStart = start + Milliseconds(timing[0]);
                    var wordEnd = wordStart + Milliseconds(timing[1]);
                    if (wordStart < start || wordStart > end || wordEnd > TimeSpan.FromHours(24) ||
                        words.Count > 0 && wordStart < words[^1].Start)
                        throw new InvalidDataException("Invalid KRC word timing.");
                    var next = body.IndexOf('<', close + 1);
                    if (next < 0) next = body.Length;
                    words.Add(new(body[(close + 1)..next], wordStart, wordEnd > end ? end : wordEnd));
                    position = next;
                }
                rows.Add(new(start, end, string.Concat(words.Select(word => word.Text)), null,
                    words.Where(word => word.End > word.Start).ToArray()));
            }
            if (rows.Count > MaximumRows) throw new InvalidDataException("KRC row count exceeds limit.");
        }
        var original = new LyricsDocument(rows, LyricsProviderKind.Kugou);
        // Broken optional translation metadata must not destroy valid original words.
        if (!string.IsNullOrWhiteSpace(language))
        {
            Check();
            try
            {
                if (language.Length > 256 * 1024) throw new InvalidDataException("KRC language data exceeds limit.");
                using var json = JsonDocument.Parse(Convert.FromBase64String(language), new JsonDocumentOptions { MaxDepth = 8 });
                if (json.RootElement.ValueKind == JsonValueKind.Object &&
                    json.RootElement.TryGetProperty("content", out var languages) && languages.ValueKind == JsonValueKind.Array)
                    foreach (var item in languages.EnumerateArray().Take(8))
                    {
                        Check();
                        if (item.ValueKind != JsonValueKind.Object || LyricsHttpClient.Number(item, "type") != 1 || !item.TryGetProperty("lyricContent", out var translations) ||
                            translations.ValueKind != JsonValueKind.Array || translations.GetArrayLength() != rows.Count) continue;
                        var translated = new List<LyricsLine>();
                        foreach (var row in translations.EnumerateArray())
                        {
                            Check();
                            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != 1 || row[0].ValueKind != JsonValueKind.String)
                            { translated.Clear(); break; }
                            translated.Add(rows[translated.Count] with
                            {
                                Secondary = row[0].GetString(), TranslationOrigin = LyricsTranslationOrigin.Provider,
                            });
                        }
                        if (translated.Count != rows.Count) continue;
                        var candidate = LyricsLanguagePolicy.IdentifyProviderTranslations(original with { Lines = translated });
                        if (original.Lines.All(row => string.IsNullOrWhiteSpace(row.Secondary))) original = candidate;
                        if (LyricsTranslationPolicy.HasMatchingProviderTranslation(candidate, target ?? string.Empty)) { original = candidate; break; }
                    }
            }
            catch (Exception error) when (error is JsonException or FormatException or InvalidDataException) { }
        }
        Check();
        return original with { Lines = original.Lines.Where(row => !string.IsNullOrWhiteSpace(row.Text)).OrderBy(row => row.Start).ToArray() };
    }

    private static TimeSpan Milliseconds(string value)
    {
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds) || milliseconds > 86_400_000)
            throw new InvalidDataException("Invalid KRC timestamp.");
        return TimeSpan.FromMilliseconds(milliseconds);
    }
}
