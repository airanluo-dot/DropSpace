using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DropSpace.Core.Lyrics;

namespace DropSpace.Infrastructure.Lyrics;

/// <summary>The caller owns song/language generations and must discard results after its token is cancelled.</summary>
public sealed class LyricsTranslationCoordinator(AiLyricsCache cache)
{
    private static readonly JsonSerializerOptions CacheJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public async Task<LyricsDocument> TranslateAsync(LyricsQuery query, LyricsDocument source, string targetLanguage,
        string modelSha256, Func<string, CancellationToken, Task<string>> infer, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(infer);
        token.ThrowIfCancellationRequested();
        var target = LyricsTranslationPolicy.NormalizeLanguage(targetLanguage);
        var indices = Enumerable.Range(0, source.Lines.Count).Where(index =>
        {
            var line = source.Lines[index];
            var matchingProvider = line.TranslationOrigin == LyricsTranslationOrigin.Provider &&
                !string.IsNullOrWhiteSpace(line.Secondary) && !string.IsNullOrWhiteSpace(line.TranslationLanguage) &&
                LyricsTranslationPolicy.NormalizeLanguage(line.TranslationLanguage) == target;
            return !string.IsNullOrWhiteSpace(line.Text) && !matchingProvider;
        }).ToArray();
        if (indices.Length is 0 or > 500) return source;
        var key = LyricsTranslationPrompt.CacheKey(query, source, targetLanguage, modelSha256);
        var saved = await cache.ReadAsync(key, token).ConfigureAwait(false);
        if (saved is not null && LyricsTranslationOutput.TryApply(saved, source, indices, targetLanguage, out var cached))
        {
            token.ThrowIfCancellationRequested();
            return cached;
        }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(180));
        var translated = source;
        foreach (var batch in indices.Chunk(12))
        {
            budget.Token.ThrowIfCancellationRequested();
            var prompt = LyricsTranslationPrompt.Build(query, source, batch, targetLanguage);
            var valid = false;
            for (var attempt = 0; attempt < 2 && !valid; attempt++)
            {
                var output = await infer(prompt, budget.Token).ConfigureAwait(false);
                budget.Token.ThrowIfCancellationRequested();
                valid = LyricsTranslationOutput.TryApply(output, translated, batch, targetLanguage, out var next);
                if (valid) translated = next;
            }
            // A failed batch never promotes partial results to a supposedly complete song cache.
            if (!valid) return source;
        }
        budget.Token.ThrowIfCancellationRequested();
        var json = JsonSerializer.Serialize(indices.Select(index => new
        {
            id = index,
            text = translated.Lines[index].Secondary ?? translated.Lines[index].Text,
        }), CacheJson);
        // Every batch is bounded independently. A valid complete song can exceed the
        // per-entry cache budget, but optional persistence must not discard its result.
        if (Encoding.UTF8.GetByteCount(json) <= LyricsTranslationOutput.MaximumOutputBytes)
        {
            try { await cache.WriteAsync(key, json, budget.Token).ConfigureAwait(false); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (InvalidDataException) { }
        }
        token.ThrowIfCancellationRequested();
        return translated;
    }
}
