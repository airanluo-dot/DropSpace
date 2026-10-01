using System.Reflection;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;
using DropSpace.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services.Media;

/// <summary>Optional offline translation. Model consent/download is separate from playback.</summary>
public sealed class AiLyricsService : IDisposable
{
    private readonly AiModelPackageService _models;
    private readonly AiLyricsRuntimePackage _runtime;
    private readonly LyricsTranslationCoordinator _translations;
    private readonly LlamaCompletionRunner _runner = new();
    private readonly ILogger<AiLyricsService> _logger;
    private readonly string _staging;

    public AiLyricsService(AppStoragePaths paths, ILogger<AiLyricsService> logger)
    {
        var root = Path.Combine(paths.Root, "AiLyrics");
        _models = new AiModelPackageService(Path.Combine(root, "Models"));
        _runtime = new AiLyricsRuntimePackage(Assembly.GetExecutingAssembly(), Path.Combine(root, "Runtime"));
        _translations = new LyricsTranslationCoordinator(new AiLyricsCache(Path.Combine(root, "Cache")));
        _staging = Path.Combine(root, "Staging");
        _logger = logger;
    }

    public event EventHandler? ModelDownloaded;

    public Task<string?> GetInstalledPathAsync(string modelId, CancellationToken token) =>
        _models.GetInstalledPathAsync(modelId, token);

    public async Task DownloadAsync(string modelId, bool consent, IProgress<double>? progress, CancellationToken token)
    {
        await _models.DownloadAsync(modelId, consent, progress, token).ConfigureAwait(false);
        ModelDownloaded?.Invoke(this, EventArgs.Empty);
    }

    public async Task<LyricsDocument> TranslateIfAvailableAsync(LyricsQuery query, LyricsDocument document,
        LyricsSettings settings, string targetLanguage, CancellationToken token)
    {
        if (!settings.Enabled || !settings.AiTranslationEnabled || document.Lines.Count == 0) return document;
        var normalizedTarget = LyricsTranslationPolicy.NormalizeLanguage(targetLanguage);
        if (document.Lines.All(line => string.IsNullOrWhiteSpace(line.Text) ||
            (!string.IsNullOrWhiteSpace(line.Secondary) && line.TranslationOrigin == LyricsTranslationOrigin.Provider &&
             !string.IsNullOrWhiteSpace(line.TranslationLanguage) &&
             LyricsTranslationPolicy.NormalizeLanguage(line.TranslationLanguage) == normalizedTarget))) return document;
        var model = AiLyricsModelCatalog.Find(settings.AiModelId);
        if (model is null) return document;
        try
        {
            // This path never downloads. Only the settings consent flow can fetch weights.
            var modelPath = await _models.GetInstalledPathAsync(model.Id, token).ConfigureAwait(false);
            if (modelPath is null) return document;
            var executable = await _runtime.EnsureExecutableAsync(token).ConfigureAwait(false);
            return await _translations.TranslateAsync(query, document, targetLanguage, model.Sha256,
                (prompt, cancellation) => _runner.RunAsync(executable, modelPath, prompt, _staging, cancellation, model.Sha256), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Timeout, missing runtime and invalid model output must not erase provider lyrics.
            _logger.LogDebug("Local lyric translation unavailable ({Category}).", error.GetType().Name);
            return document;
        }
    }

    public void Dispose()
    {
        _runner.Dispose();
        _models.Dispose();
    }
}
