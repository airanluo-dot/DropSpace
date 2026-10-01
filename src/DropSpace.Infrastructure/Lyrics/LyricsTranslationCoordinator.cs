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
        var indices = Enumerable.Range(0, source.Lines.Count).Where(index => !string.IsNullOrWhiteSpace(source.Lines[index].Text)).ToArray();
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
        try { await cache.WriteAsync(key, json, budget.Token).ConfigureAwait(false); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        token.ThrowIfCancellationRequested();
        return translated;
    }
}
