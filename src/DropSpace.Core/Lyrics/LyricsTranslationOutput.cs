using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

namespace DropSpace.Core.Lyrics;

/// <summary>Strictly validates model output; timestamps and original text never come from the model.</summary>
public static class LyricsTranslationOutput
{
    public const int MaximumOutputBytes = 65_536;

    public static string SourceVersion(LyricsDocument document) => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { version = "beta16-source-fingerprint-v1",
            source = LyricsLanguagePolicy.SourceIdentity(document), original = LyricsLanguagePolicy.OriginalRevision(document) })));

    public static string LineIdentity(LyricsDocument document, int index) => LineIdentity(SourceVersion(document), document.Lines[index], index);
    public static string LineIdentity(string sourceVersion, LyricsLine line, int index) => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { source = sourceVersion, occurrence = index, line.Start, line.End, line.Text })));

    /// <summary>Reconcile a frozen AI result against the current provider document. Text,
    /// timestamps and repeated occurrence positions must all still refer to the same row.</summary>
    public static LyricsDocument Reconcile(LyricsDocument current, LyricsDocument requestSource,
        LyricsDocument generated, string targetLanguage)
    {
        var sourceVersion = SourceVersion(requestSource);
        if (SourceVersion(current) != sourceVersion || SourceVersion(generated) != sourceVersion ||
            generated.Lines.Count != requestSource.Lines.Count ||
            LyricsLanguagePolicy.TranslationRevision(current) != LyricsLanguagePolicy.TranslationRevision(requestSource))
            return LyricsLanguagePolicy.RemoveIneligibleLocalTranslations(current, targetLanguage);
        current = LyricsLanguagePolicy.MarkTranslationStates(LyricsLanguagePolicy.IdentifyProviderTranslations(current), targetLanguage);
        if (!LyricsLanguagePolicy.CanTranslate(current, targetLanguage))
            return LyricsLanguagePolicy.RemoveIneligibleLocalTranslations(current, targetLanguage);
        var lines = current.Lines.ToArray();
        var segments = LyricsLanguagePolicy.EligibleSegments(current, targetLanguage);
        for (var id = 0; id < lines.Length; id++)
        {
            // Index is the occurrence within this source version, not a text-only key.
            if (id >= requestSource.Lines.Count || id >= generated.Lines.Count) continue;
            var line = lines[id]; var before = requestSource.Lines[id]; var output = generated.Lines[id];
            if (line.Text != before.Text || line.Start != before.Start || line.End != before.End ||
                line.SourceLanguage != before.SourceLanguage || output.Text != before.Text ||
                output.Start != before.Start || output.End != before.End) continue;
            if (LyricsLanguagePolicy.HasTargetProviderTranslation(line, targetLanguage)) continue;
            if (segments[id].Length == 0) continue;
            if (output.TranslationOrigin == LyricsTranslationOrigin.LocalAi &&
                !string.IsNullOrWhiteSpace(output.Secondary) &&
                LyricsLanguagePolicy.SameSourceLanguage(output.TranslationLanguage, targetLanguage) &&
                output.LocalAiAdmissionKey == LyricsLanguagePolicy.LocalAiAdmissionKey(current, id, targetLanguage, segments[id]))
                lines[id] = line with { Secondary = output.Secondary, TranslationOrigin = output.TranslationOrigin,
                    TranslationLanguage = output.TranslationLanguage, TranslationLanguageIsExplicit = false,
                    OriginalProviderTranslation = output.OriginalProviderTranslation,
                    LocalAiAdmissionKey = output.LocalAiAdmissionKey, TranslationState = output.TranslationState,
                    TranslationReason = output.TranslationReason };
            else if (output.TranslationState is LyricsLineTranslationState.Failed or LyricsLineTranslationState.Skipped &&
                line.TranslationOrigin != LyricsTranslationOrigin.LocalAi)
                lines[id] = line with { TranslationState = output.TranslationState, TranslationReason = output.TranslationReason };
        }
        return LyricsLanguagePolicy.WithPresentationLines(current, lines);
    }

    // Reject known runtime corruption and leaked prompt fields, not arbitrary lyric punctuation.
    // This is a structural guard; semantic translation quality still requires model evaluation.
    private static bool ContainsProtocolLeak(string text) =>
        text.Contains('\uFFFD') ||
        text.Contains("translateIds:", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("\"translateIds\"", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("[end of text]", StringComparison.Ordinal) ||
        text.Contains("<|im_start|>", StringComparison.Ordinal) ||
        text.Contains("<|im_end|>", StringComparison.Ordinal);

    public static bool IsValidTranslationText(string text) => !string.IsNullOrWhiteSpace(text) && text.Length <= 4096 &&
        !text.Any(character => char.IsControl(character) && character != '\t') && !ContainsProtocolLeak(text);

    public static bool HasUsefulLocalTranslation(LyricsDocument document, string targetLanguage)
    {
        var target = LyricsTranslationPolicy.NormalizeLanguage(targetLanguage);
        return target.Length > 0 && document.Lines.Any(line =>
            line.TranslationOrigin == LyricsTranslationOrigin.LocalAi &&
            !string.IsNullOrWhiteSpace(line.Secondary) &&
            LyricsTranslationPolicy.NormalizeLanguage(line.TranslationLanguage) == target &&
            !string.Equals(line.Text.Trim(), line.Secondary.Trim(), StringComparison.Ordinal));
    }

    public static bool TryApply(string? json, LyricsDocument source, IReadOnlyList<int> lineIndices,
        string targetLanguage, out LyricsDocument result)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(lineIndices);
        source = LyricsLanguagePolicy.IdentifyProviderTranslations(source);
        result = source;
        if (!LyricsLanguagePolicy.CanTranslate(source, targetLanguage)) return false;
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
                if (id != lineIndices[position++] || text is null || !IsValidTranslationText(text)) return false;
                replacements.Add((id.Value, text));
            }
            var lines = source.Lines.ToArray();
            foreach (var replacement in replacements)
            {
                if (LyricsLanguagePolicy.HasTargetProviderTranslation(lines[replacement.Index], targetLanguage)) continue;
                var unchanged = string.Equals(lines[replacement.Index].Text.Trim(), replacement.Text.Trim(), StringComparison.Ordinal);
                if (unchanged && lines[replacement.Index].TranslationOrigin == LyricsTranslationOrigin.Provider) continue;
                lines[replacement.Index] = lines[replacement.Index] with
                {
                    OriginalProviderTranslation = lines[replacement.Index].OriginalProviderTranslation ??
                        (lines[replacement.Index].TranslationOrigin == LyricsTranslationOrigin.Provider &&
                            !string.IsNullOrWhiteSpace(lines[replacement.Index].Secondary)
                            ? new LyricsProviderTranslation(lines[replacement.Index].Secondary!,
                                lines[replacement.Index].TranslationLanguage, lines[replacement.Index].TranslationLanguageIsExplicit) : null),
                    Secondary = unchanged ? null : replacement.Text,
                    TranslationOrigin = unchanged ? LyricsTranslationOrigin.None : LyricsTranslationOrigin.LocalAi,
                    TranslationLanguage = unchanged ? null : targetLanguage,
                };
            }
            result = LyricsLanguagePolicy.WithPresentationLines(source, lines);
            return true;
        }
        catch (JsonException) { return false; }
    }
}
