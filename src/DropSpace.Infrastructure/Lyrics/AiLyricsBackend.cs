using DropSpace.Core.Lyrics;

namespace DropSpace.Infrastructure.Lyrics;

/// <summary>A verified, local-only backend input. CT2 carries an immutable reviewed route.</summary>
public sealed record AiLyricsResolvedPackage(string BackendId, string CacheIdentity,
    string ModelPath, string RuntimePath, string? TokenizerPath, Ct2ResolvedRoute? Ct2Route = null, long? CacheGeneration = null, string? RequestIdentity = null);

public interface IAiLyricsPackageResolver
{
    Task<AiLyricsResolvedPackage?> ResolveAsync(string selectionId, LyricsQuery query,
        LyricsDocument source, string targetLanguage, CancellationToken token);
}

/// <summary>Owns backend-specific cache validation, whole-song inference, and native exit draining.</summary>
public interface IAiLyricsBackend : IDisposable
{
    string Id { get; }
    // A backend may decline preflight when its full cache identity needs verified route resolution.
    Task<LyricsTranslationResult?> TryGetCachedResultAsync(string selectionId, LyricsQuery query,
        LyricsDocument source, string targetLanguage, CancellationToken token) => Task.FromResult<LyricsTranslationResult?>(null);
    Task<LyricsTranslationResult> TranslateAsync(AiLyricsResolvedPackage package, LyricsQuery query,
        LyricsDocument source, string targetLanguage, CancellationToken token);
    // Compatibility path: existing one-shot backends need not manufacture partial output.
    Task<LyricsTranslationResult> TranslateAsync(AiLyricsResolvedPackage package, LyricsQuery query,
        LyricsDocument source, string targetLanguage, CancellationToken token, LyricsTranslationProgressContext progress) =>
        TranslateAsync(package, query, source, targetLanguage, token);
    Task DrainCleanupAsync(CancellationToken token);
}

/// <summary>The shipping GGUF resolver re-verifies model and embedded runtime before inference.</summary>
public sealed class LlamaLyricsPackageResolver(AiModelPackageService models,
    AiLyricsRuntimePackage runtime) : IAiLyricsPackageResolver
{
    public async Task<AiLyricsResolvedPackage?> ResolveAsync(string selectionId, LyricsQuery query,
        LyricsDocument source, string targetLanguage, CancellationToken token)
    {
        var descriptor = AiLyricsModelCatalog.Find(selectionId);
        if (descriptor is null) return null;
        var model = await models.GetInstalledPathAsync(descriptor.Id, token).ConfigureAwait(false);
        if (model is null) return null;
        var executable = await runtime.EnsureExecutableAsync(token).ConfigureAwait(false);
        var tokenizer = await runtime.EnsureTokenizerAsync(token).ConfigureAwait(false);
        return new(LlamaLyricsBackend.BackendId, descriptor.Sha256, model, executable, tokenizer);
    }
}

public sealed class LlamaLyricsBackend(LyricsTranslationCoordinator coordinator,
    LlamaCompletionRunner runner, string stagingDirectory) : IAiLyricsBackend
{
    public const string BackendId = "llama-gguf-v1";
    public string Id => BackendId;

    public Task<LyricsTranslationResult?> TryGetCachedResultAsync(string selectionId, LyricsQuery query,
        LyricsDocument source, string targetLanguage, CancellationToken token)
    {
        var model = AiLyricsModelCatalog.Find(selectionId);
        return model is null ? Task.FromResult<LyricsTranslationResult?>(null) :
            coordinator.TryGetCachedResultAsync(query, source, targetLanguage, model.Sha256, token);
    }

    public Task<LyricsTranslationResult> TranslateAsync(AiLyricsResolvedPackage package, LyricsQuery query,
        LyricsDocument source, string targetLanguage, CancellationToken token)
    {
        if (!StringComparer.Ordinal.Equals(package.BackendId, Id) || package.TokenizerPath is null || package.Ct2Route is not null)
            throw new InvalidDataException("The resolved package does not belong to the GGUF backend.");
        return coordinator.TranslateBatchesDetailedAsync(query, source, targetLanguage, package.CacheIdentity,
            (prompt, ids, cancellation) => runner.RunAsync(package.RuntimePath, package.ModelPath, prompt,
                stagingDirectory, cancellation, package.CacheIdentity, ids), token,
            (prompt, cancellation) => runner.CountTokensAsync(package.TokenizerPath, package.ModelPath, prompt,
                stagingDirectory, cancellation, package.CacheIdentity));
    }

    public Task DrainCleanupAsync(CancellationToken token) => runner.DrainCleanupAsync(token);
    public void Dispose() => runner.Dispose();
}
