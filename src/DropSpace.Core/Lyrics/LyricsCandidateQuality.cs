namespace DropSpace.Core.Lyrics;

public readonly record struct LyricsCandidateQuality(double DurationCloseness, double TimedLineCoverage, double WordCoverage)
{
    // Quality breaks ties only after recording identity and source/translation priority.
    // No number here is evidence that two recordings are the same song.
    public static LyricsCandidateQuality Describe(LyricsQuery query, LyricsDocument document)
    {
        var expected = query.Duration.TotalSeconds;
        var actual = document.Match?.DurationSeconds ?? 0;
        // Catalogues round duration differently. Sub-second offsets are not
        // evidence that one otherwise identical recording is a better match.
        var duration = expected > 0 && double.IsFinite(actual) && actual > 0
            ? Math.Max(0, 1 - Math.Max(0, Math.Abs(actual - expected) - 1) / expected) : 0;
        var sung = document.Lines.Where(line => !string.IsNullOrWhiteSpace(line.Text) && !LyricsLanguagePolicy.IsCredit(line.Text)).ToArray();
        var total = sung.Sum(line => Compact(line.Text).Length);
        if (total == 0) return new(duration, 0, 0);
        var limit = expected > 0 ? expected + 2 : double.IsFinite(actual) && actual > 0 ? actual + 2 : 86_400;
        var multipleStarts = sung.Select(line => line.Start).Distinct().Skip(1).Any();
        long timed = 0, words = 0;
        foreach (var line in sung)
        {
            var text = Compact(line.Text);
            if (line.Start < TimeSpan.Zero || line.End <= line.Start || line.End.TotalSeconds > limit) continue;
            var wordsBefore = words;
            var cursor = 0;
            var previousEnd = line.Start;
            foreach (var word in line.Words)
            {
                if (word.Start < previousEnd || word.Start < line.Start || word.End <= word.Start || word.End > line.End) continue;
                var value = Compact(word.Text);
                if (value.Length == 0) continue;
                var position = text.IndexOf(value, cursor, StringComparison.Ordinal);
                if (position < 0) continue;
                words += value.Length;
                cursor = position + value.Length;
                previousEnd = word.End;
            }
            // A plain-text document capped to the track length has no timing evidence.
            if (multipleStarts || line.Start > TimeSpan.Zero || words > wordsBefore) timed += text.Length;
        }
        return new(duration, timed / (double)total, words / (double)total);
    }

    private static string Compact(string value) => string.Concat(value.Where(character => !char.IsWhiteSpace(character)));
}
