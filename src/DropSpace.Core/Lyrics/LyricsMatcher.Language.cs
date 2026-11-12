using System.Text;

namespace DropSpace.Core.Lyrics;

public static partial class LyricsMatcher
{
    public static bool HasDeclaredOriginalLanguage(string title) => LanguageVersion(title) is not null;

    // The requested singing language belongs to the recording. A UI translation target,
    // the script of its title, and the provider's translated lines say nothing about it.
    public static bool OriginalLanguageConflicts(LyricsQuery query, LyricsDocument document)
    {
        var expected = LanguageVersion(query.Title);
        if (expected is null || document.Lines.Count == 0) return false;
        var sung = document.Lines.Where(line => !LyricsLanguagePolicy.IsCredit(line.Text)).ToArray();
        var lines = sung.SelectMany(line => line.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            .Where(line => line.Any(char.IsLetter)).Distinct(StringComparer.Ordinal).Take(64).ToArray();
        if (lines.Length < 2) return false;
        var sample = LyricsLanguagePolicy.BuildSample(document, LyricsLanguageRole.Original);
        var letters = sample.EnumerateRunes().Where(Rune.IsLetter).ToArray();
        if (letters.Length < LyricsLanguagePolicy.MinimumLetters) return false;
        // A complete body in East Asian script cannot be the requested English lyric.
        // One line or an occasional word from a second language is insufficient evidence.
        if (expected == "en" && letters.All(rune => rune.Value is >= 0x3040 and <= 0x30ff or
            >= 0x3400 and <= 0x9fff or >= 0xac00 and <= 0xd7a3 or >= 0x20000 and <= 0x323af)) return true;
        var tagged = sung.Where(line => !string.IsNullOrWhiteSpace(line.SourceLanguage))
            .Select(line => LyricsLanguagePolicy.NormalizeTarget(line.SourceLanguage)).Distinct().ToArray();
        var normalized = LyricsLanguagePolicy.NormalizeTarget(expected);
        if (tagged.Contains(normalized)) return false;
        var prediction = document.TranslationAdmission?.OriginalPrediction;
        var identified = prediction?.Label?.Replace("__label__", string.Empty, StringComparison.Ordinal);
        return identified is not null && LyricsLanguagePolicy.NormalizeTarget(identified) != normalized &&
            // A dominant-language classifier can also see a bilingual song. Preserve
            // substantial passages in the requested script when they coexist.
            !(normalized == "en" && lines.Count(line => line.Count(char.IsAsciiLetter) >= 8) >= 2 &&
                letters.Any(rune => rune.Value >= 0x3040)) &&
            !(normalized == "ja" && letters.Any(rune => rune.Value is >= 0x3040 and <= 0x30ff)) &&
            !(normalized == "ko" && letters.Any(rune => rune.Value is >= 0xac00 and <= 0xd7a3)) &&
            !(normalized is "zh" or "yue" && letters.Any(rune => rune.Value is >= 0x3400 and <= 0x9fff)) &&
            LyricsLanguagePolicy.PredictionMatches(prediction!, sample, identified);
    }
}
