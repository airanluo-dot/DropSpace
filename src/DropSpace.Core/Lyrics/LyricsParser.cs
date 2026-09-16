using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using DropSpace.Core.Models;

namespace DropSpace.Core.Lyrics;

public static class LyricsParser
{
    public const int MaximumCharacters = 2 * 1024 * 1024;
    private const int MaximumLines = 10_000;
    private static readonly Regex LineStamp = new(@"\[((?:\d{1,3}:)?\d{1,3}:\d{1,2}(?:[.,]\d{1,3})?)\]", RegexOptions.None, TimeSpan.FromMilliseconds(200));
    private static readonly Regex MetadataStamp = new(@"^\[(?:ti|ar|al|by|offset|re|ve|length|id|la|lr|km|us|au):", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
    private static readonly Regex WordStamp = new(@"<((?:\d{1,3}:)?\d{1,3}:\d{1,2}(?:[.,]\d{1,3})?)>", RegexOptions.None, TimeSpan.FromMilliseconds(200));
    private static readonly Regex YrcHeader = new(@"^\[(\d+),(\d+)\]", RegexOptions.None, TimeSpan.FromMilliseconds(200));
    private static readonly Regex YrcWord = new(@"\((\d+),(\d+),\d+\)", RegexOptions.None, TimeSpan.FromMilliseconds(200));

    public static LyricsDocument Parse(string text, LyricsProviderKind provider, string? secondary = null)
    {
        if (text.Length > MaximumCharacters) throw new InvalidDataException("Lyrics exceed the content limit.");
        var lines = LooksLikeTtml(text)
            ? ParseTtml(text) : ParseTimedText(text);
        var translations = (string.IsNullOrEmpty(secondary) ? [] : LooksLikeTtml(secondary) ? ParseTtml(secondary) : ParseTimedText(secondary))
            .OrderBy(line => line.Start).ToArray();
        var translationIndex = 0;
        var ordered = lines.OrderBy(line => line.Start).Take(MaximumLines).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var line = ordered[index];
            var end = index + 1 < ordered.Length && ordered[index + 1].Start > line.Start
                ? ordered[index + 1].Start : line.End;
            if (end <= line.Start) end = line.Start + TimeSpan.FromSeconds(5);
            while (translationIndex + 1 < translations.Length &&
                Math.Abs((translations[translationIndex + 1].Start - line.Start).TotalMilliseconds) < Math.Abs((translations[translationIndex].Start - line.Start).TotalMilliseconds)) translationIndex++;
            var translated = translations.Length == 0 ? null : translations[translationIndex];
            var translation = translated is not null && Math.Abs((translated.Start - line.Start).TotalMilliseconds) <= 250 ? translated.Text : line.Secondary;
            var words = line.Words.Select(word => word with
            {
                End = word.End > word.Start ? (word.End > end ? end : word.End) : end,
            }).Where(word => word.End > word.Start).ToArray();
            ordered[index] = line with { End = end, Secondary = translation, Words = words };
        }
        return new(ordered, provider);
    }

    private static bool LooksLikeTtml(string text) => text.TrimStart().StartsWith('<') &&
        text.Contains("<tt", StringComparison.OrdinalIgnoreCase);

    private static List<LyricsLine> ParseTimedText(string text)
    {
        if (text.Length > MaximumCharacters) throw new InvalidDataException("Lyrics exceed the content limit.");
        var output = new List<LyricsLine>();
        var plainLines = new List<string>();
        double offset = 0;
        foreach (var raw in text.Split('\n').Take(MaximumLines))
        {
            var value = raw.Trim().TrimStart('\uFEFF');
            if (value.Length == 0) continue;
            if (value.StartsWith("[offset:", StringComparison.OrdinalIgnoreCase) && value.EndsWith(']'))
            { if (double.TryParse(value[8..^1], CultureInfo.InvariantCulture, out var milliseconds) && double.IsFinite(milliseconds)) offset = Math.Clamp(milliseconds, -30_000, 30_000); continue; }
            var yrc = YrcHeader.Match(value);
            if (yrc.Success)
            {
                var words = new List<LyricsWord>();
                var stamps = YrcWord.Matches(value);
                for (var index = 0; index < stamps.Count && index < 2_048; index++)
                {
                    var stamp = stamps[index];
                    var start = Milliseconds(stamp.Groups[1].Value);
                    var end = start + Milliseconds(stamp.Groups[2].Value);
                    words.Add(new(value[(stamp.Index + stamp.Length)..(index + 1 < stamps.Count ? stamps[index + 1].Index : value.Length)], start, end));
                }
                if (words.Count > 0) output.Add(new(Milliseconds(yrc.Groups[1].Value), Milliseconds(yrc.Groups[1].Value) + Milliseconds(yrc.Groups[2].Value), string.Concat(words.Select(word => word.Text)), null, words));
                continue;
            }
            var times = LineStamp.Matches(value);
            if (times.Count == 0 || times[0].Index != 0)
            {
                if (!MetadataStamp.IsMatch(value)) plainLines.Add(value);
                continue;
            }
            var contentStart = times[^1].Index + times[^1].Length;
            var content = value[contentStart..];
            var wordTimes = WordStamp.Matches(content);
            var timedWords = new List<LyricsWord>();
            for (var index = 0; index < wordTimes.Count && index < 2_048; index++)
            {
                var stamp = wordTimes[index];
                var start = Timestamp(stamp.Groups[1].Value);
                var end = index + 1 < wordTimes.Count ? Timestamp(wordTimes[index + 1].Groups[1].Value) : TimeSpan.Zero;
                timedWords.Add(new(content[(stamp.Index + stamp.Length)..(index + 1 < wordTimes.Count ? wordTimes[index + 1].Index : content.Length)], start, end));
            }
            var plain = WordStamp.Replace(content, string.Empty).Trim();
            if (plain.Length == 0) continue;
            foreach (Match stamp in times.Cast<Match>().Take(64))
            {
                var start = Timestamp(stamp.Groups[1].Value);
                output.Add(new(start, start + TimeSpan.FromSeconds(5), plain, null, timedWords));
            }
            if (output.Count >= MaximumLines) break;
        }
        if (output.Count == 0 && plainLines.Count > 0)
            output.Add(new(TimeSpan.Zero, TimeSpan.FromHours(24), string.Join(Environment.NewLine, plainLines), null, []));
        if (offset != 0)
            for (var index = 0; index < output.Count; index++)
            {
                var line = output[index];
                TimeSpan Shift(TimeSpan time) => TimeSpan.FromMilliseconds(Math.Max(0, time.TotalMilliseconds + offset));
                output[index] = line with { Start = Shift(line.Start), End = Shift(line.End), Words = line.Words.Select(word => word with { Start = Shift(word.Start), End = Shift(word.End) }).ToArray() };
            }
        return output;
    }

    private static List<LyricsLine> ParseTtml(string text)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumCharacters,
        };
        // XmlReaderSettings has no portable maximum-depth switch. Scan the bounded
        // document once before materializing it so a hostile deeply nested TTML file
        // cannot drive the recursive node reader or XML DOM into stack exhaustion.
        using (var depthReader = XmlReader.Create(new StringReader(text), settings))
        {
            while (depthReader.Read())
            {
                if (depthReader.Depth > 64)
                {
                    throw new InvalidDataException("Lyrics XML nesting exceeds the supported depth.");
                }
            }
        }

        using var reader = XmlReader.Create(new StringReader(text), settings);
        var document = XDocument.Load(reader);
        var output = new List<LyricsLine>();
        foreach (var paragraph in document.Descendants().Where(element => element.Name.LocalName == "p").Take(MaximumLines))
        {
            var start = Timestamp(paragraph.Attribute("begin")?.Value ?? "0s");
            var end = ResolveEnd(paragraph, start, TimeSpan.FromSeconds(5));
            var words = new List<LyricsWord>();
            var primary = new StringBuilder();
            var translation = new StringBuilder();
            foreach (var node in paragraph.Nodes()) ReadTtmlNode(node, start, end, primary, translation, words);
            var textValue = primary.ToString().Trim();
            if (textValue.Length > 0) output.Add(new(start, end, textValue, translation.Length == 0 ? null : translation.ToString().Trim(), words));
        }
        return output;
    }

    private static void ReadTtmlNode(XNode node, TimeSpan parentStart, TimeSpan parentEnd, StringBuilder primary,
        StringBuilder translation, List<LyricsWord> words)
    {
        if (node is XText literal) { primary.Append(literal.Value); return; }
        if (node is not XElement element) return;
        if (element.Name.LocalName.Equals("br", StringComparison.OrdinalIgnoreCase))
        {
            // TTML uses <br/> for an intentional line break inside one timed
            // paragraph. Preserve it instead of concatenating adjacent spans.
            primary.AppendLine();
            return;
        }
        var role = element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName.Equals("role", StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;
        var ruby = element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName.Equals("ruby", StringComparison.OrdinalIgnoreCase))?.Value;
        // Ruby annotations are pronunciation text, not a second copy of the sung lyric.
        // Keep the base text in the primary line and omit ruby text/text containers from
        // the visible lyric; otherwise Japanese/Chinese TTML is rendered as "baseannotation".
        if (ruby is not null &&
            (ruby.Equals("text", StringComparison.OrdinalIgnoreCase) ||
             ruby.Equals("textContainer", StringComparison.OrdinalIgnoreCase))) return;
        var isTranslation = role.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(IsTranslationRole);
        if (isTranslation) { translation.Append(element.Value); return; }
        var start = ResolveNestedTime(element.Attribute("begin")?.Value, parentStart);
        var end = ResolveNestedEnd(element, start, parentEnd);
        var children = element.Nodes().ToArray();
        if (children.All(child => child is XText))
        {
            var value = element.Value;
            primary.Append(value);
            if (element.Attribute("begin") is not null && value.Trim().Length > 0 && end > start)
                words.Add(new(value.Trim(), start, end));
            return;
        }
        foreach (var child in children) ReadTtmlNode(child, start, end, primary, translation, words);
    }

    private static bool IsTranslationRole(string value)
    {
        if (value.Equals("x-translation", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("x-roman", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("x-transliteration", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var normalized = value.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);
        return normalized.Contains("translation", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("transliteration", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("romanization", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("pinyin", StringComparison.OrdinalIgnoreCase);
    }

    private static TimeSpan ResolveEnd(XElement element, TimeSpan start, TimeSpan defaultDuration)
    {
        if (element.Attribute("end") is { } end)
        {
            var parsed = Timestamp(end.Value);
            return parsed > start ? parsed : start;
        }
        if (element.Attribute("dur") is { } duration) return start + Timestamp(duration.Value);
        return start + defaultDuration;
    }

    private static TimeSpan ResolveNestedTime(string? value, TimeSpan parentStart)
    {
        if (string.IsNullOrWhiteSpace(value)) return parentStart;
        var parsed = Timestamp(value);
        return parentStart > TimeSpan.Zero && parsed < parentStart ? parentStart + parsed : parsed;
    }

    private static TimeSpan ResolveNestedEnd(XElement element, TimeSpan start, TimeSpan parentEnd)
    {
        if (element.Attribute("end") is { } end)
        {
            var parsed = Timestamp(end.Value);
            var resolved = start > TimeSpan.Zero && parsed < start ? start + parsed : parsed;
            return resolved > start ? resolved : start;
        }
        if (element.Attribute("dur") is { } duration) return start + Timestamp(duration.Value);
        return parentEnd;
    }

    private static TimeSpan Milliseconds(string value) => double.TryParse(value, CultureInfo.InvariantCulture, out var result) && double.IsFinite(result)
        ? TimeSpan.FromMilliseconds(Math.Clamp(result, 0, 86_400_000)) : TimeSpan.Zero;

    private static TimeSpan Timestamp(string value)
    {
        value = value.Trim().Replace(',', '.');
        if (value.EndsWith("ms", StringComparison.OrdinalIgnoreCase)) return Milliseconds(value[..^2]);
        if (value.EndsWith("s", StringComparison.OrdinalIgnoreCase)) return double.TryParse(value[..^1], CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds) ? TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 86_400)) : TimeSpan.Zero;
        if (value.EndsWith("min", StringComparison.OrdinalIgnoreCase) && double.TryParse(value[..^3], CultureInfo.InvariantCulture, out var minutes) && double.IsFinite(minutes))
            return TimeSpan.FromSeconds(Math.Clamp(minutes * 60, 0, 86_400));
        double total = 0;
        foreach (var component in value.Split(':').Take(3))
        { if (!double.TryParse(component, CultureInfo.InvariantCulture, out var part) || !double.IsFinite(part) || part < 0) return TimeSpan.Zero; total = total * 60 + part; }
        return TimeSpan.FromSeconds(Math.Clamp(total, 0, 86_400));
    }
}
