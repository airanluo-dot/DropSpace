using System.Collections.Frozen;
using System.Text;

namespace DropSpace.Core.Lyrics;

internal static class ArtistCreditOrthography
{
    private static readonly Lazy<FrozenDictionary<Rune, Rune>> TraditionalToSimplified = new(Load);

    // Whole metadata strings only (artist credits and comparable titles).
    // Ambiguous dictionary mappings are deliberately not guessed.
    internal static string Fold(string credit)
    {
        var normalized = LyricsMatcher.Normalize(credit);
        if (normalized.All(character => character <= 127)) return normalized;
        var result = new StringBuilder(normalized.Length);
        foreach (var rune in normalized.EnumerateRunes())
            result.Append(TraditionalToSimplified.Value.TryGetValue(rune, out var mapped) ? mapped.ToString() : rune.ToString());
        return result.ToString();
    }

    private static FrozenDictionary<Rune, Rune> Load()
    {
        using var stream = typeof(ArtistCreditOrthography).Assembly.GetManifestResourceStream(
            "DropSpace.Core.Lyrics.Data.TSCharacters.txt") ?? throw new InvalidOperationException("Missing pinned artist orthography data.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var mappings = new Dictionary<Rune, Rune>();
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith('#')) continue;
            var fields = line.Split('\t');
            if (fields.Length != 2 || fields[1].Contains(' ')) continue;
            var from = fields[0].EnumerateRunes().ToArray();
            var to = fields[1].EnumerateRunes().ToArray();
            if (from.Length == 1 && to.Length == 1) mappings[from[0]] = to[0];
        }
        return mappings.ToFrozenDictionary();
    }
}
