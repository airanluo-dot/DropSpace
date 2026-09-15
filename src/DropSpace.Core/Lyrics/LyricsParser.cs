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
    private static readonly Regex LineStamp = new(@"\[(\d{1,3}:\d{1,2}(?:[.,]\d{1,3})?)\]", RegexOptions.None, TimeSpan.FromMilliseconds(200));
    private static readonly Regex WordStamp = new(@"<(\d{1,3}:\d{1,2}(?:[.,]\d{1,3})?)>", RegexOptions.None, TimeSpan.FromMilliseconds(200));
    private static readonly Regex YrcHeader = new(@"^\[(\d+),(\d+)\]", RegexOptions.None, TimeSpan.FromMilliseconds(200));
    private static readonly Regex YrcWord = new(@"\((\d+),(\d+),\d+\)", RegexOptions.None, TimeSpan.FromMilliseconds(200));

    public static LyricsDocument Parse(string text, LyricsProviderKind provider, string? secondary = null)
    {
        if (text.Length > MaximumCharacters) throw new InvalidDataException("Lyrics exceed the content limit.");
        var lines = text.TrimStart().StartsWith('<') && text.Contains("<tt", StringComparison.Ordinal)
            ? ParseTtml(text) : ParseTimedText(text);
        var translations = (string.IsNullOrEmpty(secondary) ? [] : ParseTimedText(secondary)).OrderBy(line => line.Start).ToArray();
        var translationIndex = 0;
        var ordered = lines.OrderBy(line => line.Start).Take(MaximumLines).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var line = ordered[index];
            var end = index + 1 < ordered.Length ? ordered[index + 1].Start : line.End;
            if (end <= line.Start) end = line.Start + TimeSpan.FromSeconds(5);
            while (translationIndex + 1 < translations.Length &&
                Math.Abs((translations[translationIndex + 1].Start - line.Start).TotalMilliseconds) < Math.Abs((translations[translationIndex].Start - line.Start).TotalMilliseconds)) translationIndex++;
            var translated = translations.Length == 0 ? null : translations[translationIndex];
            var translation = translated is not null && Math.Abs((translated.Start - line.Start).TotalMilliseconds) <= 250 ? translated.Text : line.Secondary;
            ordered[index] = line with { End = end, Secondary = translation };
        }
        return new(ordered, provider);
    }

    private static List<LyricsLine> ParseTimedText(string text)
    {
        if (text.Length > MaximumCharacters) throw new InvalidDataException("Lyrics exceed the content limit.");
        var output = new List<LyricsLine>();
        double offset = 0;
        foreach (var raw in text.Split('\n').Take(MaximumLines))
        {
            var value = raw.Trim().TrimStart('\uFEFF');
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
            if (times.Count == 0 || times[0].Index != 0) continue;
            var contentStart = times[^1].Index + times[^1].Length;
            var content = value[contentStart..];
            var wordTimes = WordStamp.Matches(content);
            var timedWords = new List<LyricsWord>();
            for (var index = 0; index < wordTimes.Count && index < 2_048; index++)
            {
                var stamp = wordTimes[index];
                var start = Timestamp(stamp.Groups[1].Value);
                var end = index + 1 < wordTimes.Count ? Timestamp(wordTimes[index + 1].Groups[1].Value) : start + TimeSpan.FromSeconds(1);
                timedWords.Add(new(content[(stamp.Index + stamp.Length)..(index + 1 < wordTimes.Count ? wordTimes[index + 1].Index : content.Length)], start, end));
            }
            var plain = WordStamp.Replace(content, string.Empty).Trim();
            foreach (Match stamp in times.Cast<Match>().Take(64))
            {
                var start = Timestamp(stamp.Groups[1].Value);
                output.Add(new(start, start + TimeSpan.FromSeconds(5), plain, null, timedWords));
            }
            if (output.Count >= MaximumLines) break;
        }
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
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumCharacters });
        var document = XDocument.Load(reader);
        var output = new List<LyricsLine>();
        foreach (var paragraph in document.Descendants().Where(element => element.Name.LocalName == "p").Take(MaximumLines))
        {
            var start = Timestamp(paragraph.Attribute("begin")?.Value ?? "0s");
            var end = Timestamp(paragraph.Attribute("end")?.Value ?? "0s");
            var words = new List<LyricsWord>();
            var primary = new StringBuilder();
            string? translation = null;
            foreach (var node in paragraph.Nodes())
            {
                if (node is XText literal) { primary.Append(literal.Value); continue; }
                if (node is not XElement span) continue;
                var role = span.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "role")?.Value;
                if (role is "x-translation" or "x-roman") { translation ??= span.Value; continue; }
                primary.Append(span.Value);
                if (span.Attribute("begin") is { } begin)
                    words.Add(new(span.Value, Timestamp(begin.Value), Timestamp(span.Attribute("end")?.Value ?? paragraph.Attribute("end")?.Value ?? "0s")));
            }
            output.Add(new(start, end, primary.ToString().Trim(), translation, words));
        }
        return output;
    }

    private static TimeSpan Milliseconds(string value) => double.TryParse(value, CultureInfo.InvariantCulture, out var result) && double.IsFinite(result)
        ? TimeSpan.FromMilliseconds(Math.Clamp(result, 0, 86_400_000)) : TimeSpan.Zero;

    private static TimeSpan Timestamp(string value)
    {
        value = value.Replace(',', '.');
        if (value.EndsWith("ms", StringComparison.Ordinal)) return Milliseconds(value[..^2]);
        if (value.EndsWith('s')) return double.TryParse(value[..^1], CultureInfo.InvariantCulture, out var seconds) && double.IsFinite(seconds) ? TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 86_400)) : TimeSpan.Zero;
        double total = 0;
        foreach (var component in value.Split(':').Take(3))
        { if (!double.TryParse(component, CultureInfo.InvariantCulture, out var part) || !double.IsFinite(part) || part < 0) return TimeSpan.Zero; total = total * 60 + part; }
        return TimeSpan.FromSeconds(Math.Clamp(total, 0, 86_400));
    }
}
