using System.Text;
using System.Text.Json;

namespace DropSpace.Core.Lyrics;

/// <summary>Strictly validates model output; timestamps and original text never come from the model.</summary>
public static class LyricsTranslationOutput
{
    public const int MaximumOutputBytes = 65_536;

    // Reject known runtime corruption and leaked prompt fields, not arbitrary lyric punctuation.
    // This is a structural guard; semantic translation quality still requires model evaluation.
    private static bool ContainsProtocolLeak(string text) =>
        text.Contains('\uFFFD') ||
        text.Contains("translateIds:", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("\"translateIds\"", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("[end of text]", StringComparison.Ordinal) ||
        text.Contains("<|im_start|>", StringComparison.Ordinal) ||
        text.Contains("<|im_end|>", StringComparison.Ordinal);

    public static bool TryApply(string? json, LyricsDocument source, IReadOnlyList<int> lineIndices,
        string targetLanguage, out LyricsDocument result)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(lineIndices);
        result = source;
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaximumOutputBytes ||
            lineIndices.Count is 0 or > 500 || string.IsNullOrWhiteSpace(targetLanguage)) return false;
        var expected = new HashSet<int>();
        foreach (var index in lineIndices)
            if (index < 0 || index >= source.Lines.Count || !expected.Add(index)) return false;
        try
        {
            using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            if (parsed.RootElement.ValueKind != JsonValueKind.Array || parsed.RootElement.GetArrayLength() != lineIndices.Count) return false;
            var replacements = new List<(int Index, string Text)>(lineIndices.Count);
            var position = 0;
            foreach (var element in parsed.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) return false;
                int? id = null;
                string? text = null;
                var hasText = false;
                foreach (var property in element.EnumerateObject())
                {
                    if (property.NameEquals("id") && id is null && property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out var value)) id = value;
                    else if (property.NameEquals("text") && !hasText && property.Value.ValueKind == JsonValueKind.String)
                    {
                        text = property.Value.GetString();
                        hasText = true;
                    }
                    else return false;
                }
                if (id != lineIndices[position++] || string.IsNullOrWhiteSpace(text) || text.Length > 4096 ||
                    text.Any(character => char.IsControl(character) && character != '\t') ||
                    ContainsProtocolLeak(text)) return false;
                replacements.Add((id.Value, text));
            }
            var lines = source.Lines.ToArray();
            foreach (var replacement in replacements)
            {
                var unchanged = string.Equals(lines[replacement.Index].Text.Trim(), replacement.Text.Trim(), StringComparison.Ordinal);
                lines[replacement.Index] = lines[replacement.Index] with
                {
                    Secondary = unchanged ? null : replacement.Text,
                    TranslationOrigin = unchanged ? LyricsTranslationOrigin.None : LyricsTranslationOrigin.LocalAi,
                    TranslationLanguage = unchanged ? null : targetLanguage,
                };
            }
            result = source with { Lines = lines };
            return true;
        }
        catch (JsonException) { return false; }
    }
}
