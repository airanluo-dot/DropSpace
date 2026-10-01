using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DropSpace.Core.Lyrics;

namespace DropSpace.Infrastructure.Lyrics;

/// <summary>The caller owns song/language generations and must discard results after its token is cancelled.</summary>
public sealed class LyricsTranslationCoordinator(AiLyricsCache cache)
{
    private static readonly JsonSerializerOptions CacheJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public Task<LyricsDocument> TranslateAsync(LyricsQuery query, LyricsDocument source, string targetLanguage,
        string modelSha256, Func<string, CancellationToken, Task<string>> infer, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(infer);
        return TranslateBatchesAsync(query, source, targetLanguage, modelSha256,
            (prompt, _, cancellation) => infer(prompt, cancellation), token);
    }

    public async Task<LyricsDocument> TranslateBatchesAsync(LyricsQuery query, LyricsDocument source, string targetLanguage,
        string modelSha256, Func<string, IReadOnlyList<int>, CancellationToken, Task<string>> infer, CancellationToken token,
        Func<string, CancellationToken, Task<int>>? countTokens = null)
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
        for (var offset = 0; offset < indices.Length;)
        {
            budget.Token.ThrowIfCancellationRequested();
            var take = Math.Min(12, indices.Length - offset);
            int[] batch;
            string prompt;
            while (true)
            {
                batch = indices.Skip(offset).Take(take).ToArray();
                prompt = LyricsTranslationPrompt.Build(query, source, batch, targetLanguage);
                if (countTokens is null || await countTokens(prompt, budget.Token).ConfigureAwait(false) <= LyricsTranslationPrompt.MaximumPromptTokens)
                    break;
                // Background can be omitted, but requested source lines are never truncated.
                prompt = LyricsTranslationPrompt.Build(query, source, batch, targetLanguage, includeBackground: false);
                if (await countTokens(prompt, budget.Token).ConfigureAwait(false) <= LyricsTranslationPrompt.MaximumPromptTokens)
                    break;
                if (take == 1) throw new InvalidDataException("A lyric line exceeds the verified tokenizer input budget.");
                take = (take + 1) / 2;
            }
            var valid = false;
            for (var attempt = 0; attempt < 2 && !valid; attempt++)
            {
                var output = await infer(prompt, batch, budget.Token).ConfigureAwait(false);
                budget.Token.ThrowIfCancellationRequested();
                valid = LyricsTranslationOutput.TryApply(output, translated, batch, targetLanguage, out var next);
                if (valid) translated = next;
            }
            // A failed batch never promotes partial results to a supposedly complete song cache.
            if (!valid) return source;
            offset += batch.Length;
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
