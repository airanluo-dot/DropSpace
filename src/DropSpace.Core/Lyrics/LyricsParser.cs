using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using DropSpace.Core.Models;

namespace DropSpace.Core.Lyrics;

public enum TtmlTimingMode { Automatic, Absolute, ParentRelative }

public static class LyricsParser
{
    public const int MaximumCharacters = 2 * 1024 * 1024;
    public const int CurrentTimingRevision = 2;
    private const int MaximumLines = 10_000;
    private static readonly Regex LineStamp = new(@"\[((?:\d{1,3}:)?\d{1,3}:\d{1,2}(?:[.,]\d{1,3})?)\]", RegexOptions.None, TimeSpan.FromMilliseconds(200));
    private static readonly Regex MetadataStamp = new(@"^\[(?:ti|ar|al|by|offset|re|ve|length|id|la|lr|km|us|au):", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
    private static readonly Regex WordStamp = new(@"<((?:\d{1,3}:)?\d{1,3}:\d{1,2}(?:[.,]\d{1,3})?)>", RegexOptions.None, TimeSpan.FromMilliseconds(200));
    private static readonly Regex YrcHeader = new(@"^\[(\d+),(\d+)\]", RegexOptions.None, TimeSpan.FromMilliseconds(200));
    private static readonly Regex YrcWord = new(@"\((\d+),(\d+),\d+\)", RegexOptions.None, TimeSpan.FromMilliseconds(200));

    public static bool HasCurrentTiming(LyricsDocument document)
    {
        if (document.TimingRevision >= CurrentTimingRevision) return true;
        if (document.TimingRevision != 1) return false;
        if (!document.HasSynchronizedTiming || document.Provider == LyricsProviderKind.Amll) return true;
        // NetEase's old YRC loses the distinction between a hold and a point.
        // A complete ordered Kugou word track can keep its existing offline hit.
        // Older cache entries did not distinguish native KRC from the LRC fallback.
        if (document.Provider != LyricsProviderKind.Kugou) return false;
        foreach (var line in document.Lines)
        {
            if (line.Words.Count == 0 || string.IsNullOrWhiteSpace(line.Words[^1].Text) ||
                string.Concat(line.Words.Select(word => word.Text)).Trim() != line.Text.Trim()) return false;
            var previousEnd = line.Start;
            foreach (var word in line.Words)
            {
                if (word.Start < previousEnd || word.End <= word.Start) return false;
                previousEnd = word.End;
            }
        }
        return true;
    }

    public static LyricsDocument Parse(string text, LyricsProviderKind provider, string? secondary = null, TtmlTimingMode ttmlTiming = TtmlTimingMode.Automatic)
    {
        if (text.Length > MaximumCharacters) throw new InvalidDataException("Lyrics exceed the content limit.");
        var synchronized = true;
        var lines = LooksLikeTtml(text)
            ? ParseTtml(text, provider, ttmlTiming) : ParseTimedText(text, out synchronized);
        var translations = (string.IsNullOrEmpty(secondary) ? [] : LooksLikeTtml(secondary) ? ParseTtml(secondary, provider, ttmlTiming) : ParseTimedText(secondary, out _))
            .OrderBy(line => line.Start).ToArray();
        var ordered = lines.OrderBy(line => line.Start).Take(MaximumLines).ToArray();
        var translationIndices = AlignTranslations(ordered, translations);
        for (var index = 0; index < ordered.Length; index++)
        {
            var line = ordered[index];
            var translationIndex = translationIndices[index];
            var translation = translationIndex >= 0 ? translations[translationIndex].Text : line.Secondary;
            ordered[index] = line with
            {
                Secondary = translation,
                TranslationOrigin = translationIndex >= 0 && !string.IsNullOrWhiteSpace(translation)
                    ? LyricsTranslationOrigin.Provider : line.TranslationOrigin,
                // External TTML carries the translated text's own primary language.
                // Unlabelled LRC remains unknown until conservative document classification.
                TranslationLanguage = translationIndex >= 0 ? translations[translationIndex].SourceLanguage : line.TranslationLanguage,
                TranslationLanguageIsExplicit = translationIndex >= 0
                    ? !string.IsNullOrWhiteSpace(translations[translationIndex].SourceLanguage) : line.TranslationLanguageIsExplicit,
            };
        }
        return LyricsBodyQualityPolicy.Normalize(LyricsLanguagePolicy.IdentifyProviderTranslations(new LyricsDocument(ordered, provider)
        {
            TimingRevision = CurrentTimingRevision, DeclaresSynchronizedTiming = synchronized,
        }));
    }

    private static int[] AlignTranslations(LyricsLine[] lines, LyricsLine[] translations)
    {
        var matches = Enumerable.Repeat(-1, lines.Length).ToArray();
        if (translations.Length == 0) return matches;
        foreach (var group in translations.Select((line, index) => (line, index)).GroupBy(item => item.line.IsBackground))
        {
            var originals = Enumerable.Range(0, lines.Length).Where(i => lines[i].IsBackground == group.Key).ToArray();
            if (originals.Length == 0) continue;
            var cursor = 0;
            foreach (var (translation, index) in group)
            {
                // Prefer the last equal-time original (NetEase may prepend zero-time credits).
                // A backing translation can only attach to a backing vocal, and vice versa.
                long Distance(int original) => Math.Abs((lines[original].Start - translation.Start).Ticks);
                while (cursor + 1 < originals.Length && Distance(originals[cursor + 1]) <= Distance(originals[cursor])) cursor++;
                var original = originals[cursor];
                var distance = Distance(original);
                var previous = matches[original];
                if (distance <= TimeSpan.TicksPerMillisecond * 250 &&
                    (previous < 0 || distance < Math.Abs((lines[original].Start - translations[previous].Start).Ticks)))
                    matches[original] = index;
            }
        }
        return matches;
    }

    private static bool LooksLikeTtml(string text) => text.TrimStart().StartsWith('<') &&
        text.Contains("<tt", StringComparison.OrdinalIgnoreCase);

    private static List<LyricsLine> ParseTimedText(string text, out bool synchronized)
    {
        if (text.Length > MaximumCharacters) throw new InvalidDataException("Lyrics exceed the content limit.");
        var output = new List<LyricsLine>();
        var plainLines = new List<string>();
        var emptyBoundaries = new List<TimeSpan>();
        double offset = 0;
        foreach (var raw in text.Split('\n').Take(MaximumLines))
        {
            var value = raw.Trim().TrimStart('\uFEFF');
            if (value.Length == 0)
            {
                // Keep internal section boundaries for an untimed display block.
                // They also prevent language context from crossing a blank verse.
                if (plainLines.Count > 0) plainLines.Add(string.Empty);
                continue;
            }
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
                var duration = Milliseconds(yrc.Groups[2].Value);
                var lineStart = Milliseconds(yrc.Groups[1].Value);
                if (words.Count > 0 && duration > TimeSpan.Zero)
                    output.Add(new(lineStart, lineStart + duration, string.Concat(words.Select(word => word.Text)), null, words));
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
            var firstLineStart = Timestamp(times[0].Groups[1].Value);
            var lineEnd = firstLineStart;
            if (wordTimes.Count > 0 && wordTimes[0].Index > 0)
            {
                var leadingText = content[..wordTimes[0].Index];
                var nextWordStart = Timestamp(wordTimes[0].Groups[1].Value);
                if (!string.IsNullOrWhiteSpace(leadingText) && nextWordStart >= firstLineStart)
                    timedWords.Add(new(leadingText, firstLineStart, nextWordStart));
            }
            for (var index = 0; index < wordTimes.Count && timedWords.Count < 2_048; index++)
            {
                var stamp = wordTimes[index];
                var start = Timestamp(stamp.Groups[1].Value);
                var end = index + 1 < wordTimes.Count ? Timestamp(wordTimes[index + 1].Groups[1].Value) : TimeSpan.MaxValue;
                var word = content[(stamp.Index + stamp.Length)..(index + 1 < wordTimes.Count ? wordTimes[index + 1].Index : content.Length)];
                if (index + 1 == wordTimes.Count && string.IsNullOrWhiteSpace(word))
                {
                    if (start > firstLineStart) lineEnd = start;
                }
                else timedWords.Add(new(word, start, end));
            }
            var plain = WordStamp.Replace(content, string.Empty).Trim();
            if (plain.Length == 0)
            {
                emptyBoundaries.AddRange(times.Cast<Match>().Take(64).Select(stamp => Timestamp(stamp.Groups[1].Value)));
                continue;
            }
            foreach (Match stamp in times.Cast<Match>().Take(64))
            {
                var start = Timestamp(stamp.Groups[1].Value);
                // Repeated LRC rows share the authored word intervals and optional end marker.
                var shift = start - firstLineStart;
                var wordsForOccurrence = shift == TimeSpan.Zero ? timedWords : timedWords.Select(word => word with
                {
                    Start = word.Start + shift,
                    End = word.End == TimeSpan.MaxValue ? word.End : word.End + shift,
                }).ToList();
                output.Add(new(start, lineEnd + shift, plain, null, wordsForOccurrence));
            }
            if (output.Count >= MaximumLines) break;
        }
        synchronized = output.Count > 0;
        if (!synchronized)
        {
            if (plainLines.Count > 0)
                output.Add(new(TimeSpan.Zero, TimeSpan.FromHours(24), string.Join(Environment.NewLine, plainLines).TrimEnd(), null, []));
            return output;
        }

        // LRC omits row ends. Blank timestamped cues mark rests. Explicit YRC intervals are independent.
        var boundaries = output.Select(line => line.Start).Concat(emptyBoundaries).Distinct().Order().ToArray();
        var advance = TimeSpan.FromMilliseconds(offset);
        TimeSpan Shift(TimeSpan time) => time > advance ? time - advance : TimeSpan.Zero;
        var timedLines = new List<LyricsLine>(output.Count);
        foreach (var line in output)
        {
            var end = line.End;
            if (end == line.Start)
            {
                var next = Array.BinarySearch(boundaries, line.Start) + 1;
                end = next < boundaries.Length ? boundaries[next] : line.Start + TimeSpan.FromSeconds(5);
            }
            // A positive LRC offset advances the cues. Resolve missing word/line ends before
            // clipping at media zero so cues which have already finished cannot reappear.
            if (end <= advance) continue;
            var words = new List<LyricsWord>(line.Words.Count);
            foreach (var word in line.Words)
            {
                // A YRC word has an explicit duration. Punctuation often lands
                // exactly on a sung word boundary and takes zero time.
                var wordEnd = word.End > end ? end : word.End;
                if (wordEnd > word.Start && wordEnd > advance ||
                    wordEnd == word.Start && word.Start >= advance)
                    words.Add(word with { Start = Shift(word.Start), End = Shift(wordEnd) });
            }
            timedLines.Add(line with { Start = Shift(line.Start), End = Shift(end), Words = words });
        }
        return timedLines;
    }

    private static List<LyricsLine> ParseTtml(string text, LyricsProviderKind provider, TtmlTimingMode timingMode)
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
        // AMLL/Apple lyric documents use media-absolute word stamps. Standard
        // TTML offset expressions are relative to their parent's time container.
        // Callers can state the dialect explicitly; never infer it from numeric ordering.
        var absolute = timingMode == TtmlTimingMode.Absolute ||
            (timingMode == TtmlTimingMode.Automatic && (provider == LyricsProviderKind.Amll ||
            document.Root?.Attributes().Any(attribute => attribute.IsNamespaceDeclaration &&
                (attribute.Value.Contains("itunes.apple.com", StringComparison.OrdinalIgnoreCase) ||
                 attribute.Value == "http://music.apple.com/lyric-ttml-internal")) == true));
        var paragraphs = document.Descendants().Where(element => element.Name.LocalName == "p").Take(MaximumLines).Select(paragraph =>
        {
            var parentStart = absolute ? TimeSpan.Zero : paragraph.Ancestors()
                .Aggregate(TimeSpan.Zero, (time, element) => time + Timestamp(element.Attribute("begin")?.Value ?? "0s"));
            var start = parentStart + Timestamp(paragraph.Attribute("begin")?.Value ?? "0s");
            return (paragraph, parentStart, start);
        }).ToArray();
        var starts = paragraphs.Select(row => row.start).Distinct().Order().ToArray();
        var output = new List<LyricsLine>();
        foreach (var (paragraph, parentStart, start) in paragraphs)
        {
            // Only an unspecified, inferred end yields to the next paragraph. A declared
            // parallel interval remains active when a shorter answering voice ends.
            TimeSpan? inferredEnd = null;
            if (paragraph.Attribute("end") is null && paragraph.Attribute("dur") is null)
            {
                var next = Array.BinarySearch(starts, start) + 1;
                inferredEnd = start + TimeSpan.FromSeconds(5);
                if (next < starts.Length && starts[next] < inferredEnd) inferredEnd = starts[next];
            }
            var end = ResolveNestedEnd(paragraph, start, parentStart, TimeSpan.MaxValue, absolute);
            if (end > start)
                ReadTtmlLine(paragraph, start, end, absolute, HasBackgroundRole(paragraph), output, inferredEnd);
        }
        return output;
    }

    private static bool HasBackgroundRole(XElement element) => element.Attributes()
        .Any(attribute => attribute.Name.LocalName == "role" &&
            attribute.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains("x-bg", StringComparer.OrdinalIgnoreCase));

    private static void ReadTtmlLine(XElement element, TimeSpan start, TimeSpan end, bool absolute,
        bool background, List<LyricsLine> output, TimeSpan? inferredEnd = null)
    {
        var words = new List<LyricsWord>();
        var primary = new StringBuilder();
        var translations = new List<(string Text, string? Language)>();
        var romanization = new StringBuilder();
        foreach (var node in element.Nodes())
            ReadTtmlNode(node, start, end, primary, translations, romanization, words, absolute, output);
        var text = primary.ToString().Trim();
        if (text.Length == 0 || output.Count >= MaximumLines) return;
        if (end == TimeSpan.MaxValue)
        {
            // A paragraph can omit its end while its words supply the actual endpoint.
            // Only unbounded text/words need the normal next-line or final-row fallback.
            var fallback = inferredEnd ?? start + TimeSpan.FromSeconds(5);
            end = words.Count == 0 ? fallback : TimeSpan.Zero;
            for (var i = 0; i < words.Count; i++)
            {
                var word = words[i];
                if (word.End == TimeSpan.MaxValue)
                    words[i] = word = word with { End = fallback > word.Start ? fallback : word.Start + TimeSpan.FromSeconds(5) };
                if (word.End > end) end = word.End;
            }
        }
        var translation = string.Concat(translations.Select(value => value.Text)).Trim();
        var secondary = translation.Length > 0 ? translation : romanization.ToString().Trim();
        var languages = translations.Select(value => value.Language).Distinct(StringComparer.Ordinal).ToArray();
        output.Add(new(start, end, text, secondary.Length == 0 ? null : secondary, words)
        {
            IsBackground = background,
            SourceLanguage = ExplicitSourceLanguage(element),
            TranslationOrigin = translation.Length > 0 ? LyricsTranslationOrigin.Provider : LyricsTranslationOrigin.None,
            TranslationLanguage = translation.Length > 0 && languages.Length == 1 ? languages[0] : null,
            TranslationLanguageIsExplicit = translation.Length > 0 && languages.Length == 1 && !string.IsNullOrWhiteSpace(languages[0]),
        });
    }

    private static void ReadTtmlNode(XNode node, TimeSpan parentStart, TimeSpan parentEnd, StringBuilder primary,
        List<(string Text, string? Language)> translations, StringBuilder romanization, List<LyricsWord> words, bool absolute,
        List<LyricsLine> output)
    {
        if (node is XText literal) { primary.Append(literal.Value); return; }
        if (node is not XElement element) return;
        if (element.Name.LocalName.Equals("br", StringComparison.OrdinalIgnoreCase))
        {
            primary.AppendLine();
            return;
        }
        var role = element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName.Equals("role", StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;
        var ruby = element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName.Equals("ruby", StringComparison.OrdinalIgnoreCase))?.Value;
        if (ruby is not null &&
            (ruby.Equals("text", StringComparison.OrdinalIgnoreCase) ||
             ruby.Equals("textContainer", StringComparison.OrdinalIgnoreCase))) return;
        var roles = role.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (roles.Any(IsRomanizationRole)) { romanization.Append(element.Value); return; }
        if (roles.Any(IsTranslationRole))
        {
            if (!string.IsNullOrWhiteSpace(element.Value))
                translations.Add((element.Value, ExplicitTranslationLanguage(element)));
            return;
        }
        var start = ResolveNestedTime(element.Attribute("begin")?.Value, parentStart, absolute);
        var end = ResolveNestedEnd(element, start, parentStart, parentEnd, absolute);
        if (roles.Contains("x-bg", StringComparer.OrdinalIgnoreCase))
        {
            if (end > start) ReadTtmlLine(element, start, end, absolute, true, output);
            return;
        }
        if (start < parentStart) start = parentStart;
        if (end > parentEnd) end = parentEnd;
        var previousWordCount = words.Count;
        var textStart = primary.Length;
        foreach (var child in element.Nodes()) ReadTtmlNode(child, start, end, primary, translations, romanization, words, absolute, output);
        // A timed span may contain ruby or translation markup. Use its visible text
        // if it has no finer timed children, retaining the original text positions.
        if (element.Attribute("begin") is not null && end > start && previousWordCount == words.Count && primary.Length > textStart)
        {
            var word = primary.ToString(textStart, primary.Length - textStart).Trim();
            if (word.Length > 0) words.Add(new(word, start, end));
        }
    }

    private static bool IsTranslationRole(string value)
    {
        var normalized = value.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);
        return normalized.Contains("translation", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRomanizationRole(string value)
    {
        var normalized = value.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);
        return normalized.Equals("xroman", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("transliteration", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("romanization", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("pinyin", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExplicitSourceLanguage(XElement paragraph)
    {
        var languages = new HashSet<string>(StringComparer.Ordinal);
        foreach (var text in paragraph.DescendantNodes().OfType<XText>().Where(t => t.Value.Any(char.IsLetter)))
        {
            var ancestors = text.Ancestors().ToArray();
            if (ancestors.TakeWhile(e => e != paragraph).Any(e =>
            {
                var roles = e.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals("role", StringComparison.OrdinalIgnoreCase))?.Value ?? string.Empty;
                var ruby = e.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals("ruby", StringComparison.OrdinalIgnoreCase))?.Value;
                return HasBackgroundRole(e) || roles.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Any(r => IsTranslationRole(r) || IsRomanizationRole(r)) ||
                    ruby is not null && (ruby.Equals("text", StringComparison.OrdinalIgnoreCase) || ruby.Equals("textContainer", StringComparison.OrdinalIgnoreCase));
            })) continue;
            var declaration = ancestors.Select(e => e.Attribute(XNamespace.Xml + "lang")?.Value).FirstOrDefault(v => v is not null);
            var language = LyricsTranslationPolicy.NormalizeLanguage(declaration);
            if (language.Length == 0) return null;
            languages.Add(language);
        }
        return languages.Count == 1 ? languages.Single() : languages.Count > 1 ? "mul" : null;
    }

    private static string? ExplicitTranslationLanguage(XElement translation)
    {
        string? language = null;
        foreach (var text in translation.DescendantNodes().OfType<XText>().Where(text => !string.IsNullOrWhiteSpace(text.Value)))
        {
            string? declared = null;
            for (var current = text.Parent; current is not null; current = current.Parent)
            {
                if (current.Attribute(XNamespace.Xml + "lang") is { } attribute)
                {
                    declared = attribute.Value;
                    break;
                }
                // A language on the source paragraph/document does not identify an
                // unlabelled translation. Only declarations within its span count.
                if (current == translation) break;
            }
            var normalized = LyricsTranslationPolicy.NormalizeLanguage(declared);
            if (normalized.Length == 0 || (language is not null && language != normalized)) return null;
            language = normalized;
        }
        return language;
    }

    private static TimeSpan ResolveNestedTime(string? value, TimeSpan parentStart, bool absolute)
    {
        if (string.IsNullOrWhiteSpace(value)) return parentStart;
        var parsed = Timestamp(value);
        return absolute ? parsed : parentStart + parsed;
    }

    private static TimeSpan ResolveNestedEnd(XElement element, TimeSpan start, TimeSpan parentStart, TimeSpan parentEnd, bool absolute)
    {
        // Relative begin/end share their parent's origin. Only dur is relative to
        // the child start. When both specify an endpoint the earlier one applies.
        var resolved = element.Attribute("end") is { } end ? ResolveNestedTime(end.Value, parentStart, absolute) : parentEnd;
        if (element.Attribute("dur") is { } duration)
        {
            var durationEnd = start + Timestamp(duration.Value);
            if (durationEnd < resolved) resolved = durationEnd;
        }
        return resolved > start ? resolved : start;
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
