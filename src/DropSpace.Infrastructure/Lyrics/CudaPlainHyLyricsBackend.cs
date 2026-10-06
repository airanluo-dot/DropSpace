using System.Security.Cryptography;
using System.Text;
using DropSpace.Core.Lyrics;

namespace DropSpace.Infrastructure.Lyrics;

/// <summary>Unregistered experiment; binds both CUDA and CPU fallback manifests to a separate cache.</summary>
public sealed class CudaPlainHyLyricsBackend(PlainHyLyricsCoordinator coordinator,
    PersistentPlainLyricsRunner runner, AiLyricsRuntimePackage cpu, CudaLyricsRuntimePackage cuda,
    string stagingDirectory) : IAiLyricsBackend
{
    public const string BackendId = "hy-q8-plain-cuda-experiment-v1";
    public string Id => BackendId;
    public string Identity(string modelHash) => PlainHyLyricsProtocol.InferenceIdentity(
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            BackendId + ":" + cuda.GetManifestCacheIdentity() + ":" + cpu.GetManifestCacheIdentity()))), modelHash);

    public Task<LyricsTranslationResult?> TryGetCachedResultAsync(string selectionId, LyricsQuery query, LyricsDocument source,
        string targetLanguage, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        source = LyricsLanguagePolicy.IdentifyProviderTranslations(source);
        var model = AiLyricsModelCatalog.FindSelectable(selectionId);
        if (model is null || LyricsLanguagePolicy.EligibleIndices(source, targetLanguage).Length == 0)
            return Task.FromResult<LyricsTranslationResult?>(null);
        string identity;
        try { identity = Identity(model.Sha256); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
        { return Task.FromResult<LyricsTranslationResult?>(null); }
        return coordinator.TryGetCachedAsync(query, source, targetLanguage, identity, token);
    }

    public Task<LyricsTranslationResult> TranslateAsync(AiLyricsResolvedPackage package, LyricsQuery query,
        LyricsDocument source, string targetLanguage, CancellationToken token) =>
        TranslateAsync(package, query, source, targetLanguage, token, null);

    public Task<LyricsTranslationResult> TranslateAsync(AiLyricsResolvedPackage package,
        LyricsQuery query, LyricsDocument source, string targetLanguage, CancellationToken token,
        LyricsTranslationProgressContext? progress)
    {
        token.ThrowIfCancellationRequested();
        source = LyricsLanguagePolicy.IdentifyProviderTranslations(source);
        if (LyricsLanguagePolicy.EligibleIndices(source, targetLanguage).Length == 0)
            return Task.FromResult(new LyricsTranslationResult(source, LyricsTranslationOutcome.NoUsefulTranslation));
        var model = AiLyricsModelCatalog.FindSelectable(package.ModelId ?? string.Empty);
        if (model is null || package.VerifiedModelSha256 != model.Sha256 || package.BackendId != Id ||
            package.Ct2Route is not null || package.CacheIdentity != Identity(model.Sha256))
            throw new InvalidDataException("Resolved package does not belong to the CUDA experiment.");
        return coordinator.TranslateAsync(query, source, targetLanguage, package.CacheIdentity,
            package.CacheGeneration ?? throw new InvalidDataException("Missing cache generation."),
            (prompt, cancel) => runner.RunPlainAsync(package.RuntimePath, package.ModelPath, prompt,
                stagingDirectory, cancel, model.Sha256), token, progress);
    }

    public Task DrainCleanupAsync(CancellationToken token) => runner.DrainCleanupAsync(token);
    public void Dispose() => runner.Dispose();
}

/// <summary>Reuses installed, hash-verified GGUFs only. No downloads or shipping DI registration.</summary>
public sealed class CudaPlainHyLyricsPackageResolver(AiModelPackageService models,
    CudaLyricsRuntimePackage cuda, CudaPlainHyLyricsBackend backend) : IAiLyricsPackageResolver
{
    public async Task<AiLyricsResolvedPackage?> ResolveAsync(string selectionId, LyricsQuery query,
        LyricsDocument source, string targetLanguage, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var model = AiLyricsModelCatalog.FindSelectable(selectionId);
        source = LyricsLanguagePolicy.IdentifyProviderTranslations(source);
        if (model is null || LyricsLanguagePolicy.EligibleIndices(source, targetLanguage).Length == 0) return null;
        var path = await models.GetInstalledPathAsync(selectionId, token).ConfigureAwait(false);
        if (path is null) return null;
        var worker = await cuda.EnsureWorkerAsync(token).ConfigureAwait(false);
        return new(backend.Id, backend.Identity(model.Sha256), path, worker, null,
            ModelId: model.Id, VerifiedModelSha256: model.Sha256);
    }
}
