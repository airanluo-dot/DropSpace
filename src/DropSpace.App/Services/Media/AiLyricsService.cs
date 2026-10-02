using System.Reflection;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;
using DropSpace.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services.Media;

public enum AiLyricsTranslationState { Ready, Translating, Completed, Unavailable }

/// <summary>Optional offline translation. Model consent/download is separate from playback.</summary>
public sealed class AiLyricsService : IDisposable
{
    private readonly AiModelPackageService _models;
    private readonly AiLyricsRuntimePackage _runtime;
    private readonly LyricsTranslationCoordinator _translations;
    private readonly AiLyricsCache _cache;
    private readonly AiLyricsWorkLifetime _work = new();
    private readonly LlamaCompletionRunner _runner = new();
    private readonly ILogger<AiLyricsService> _logger;
    private readonly string _staging;
    private readonly LyricsInferenceCircuit _circuit = new();
    private readonly object _stateGate = new();
    private long _statusGeneration;
    private AiLyricsTranslationState _state;
    public AiLyricsTranslationState TranslationState { get { lock (_stateGate) return _state; } }

    public AiLyricsService(AppStoragePaths paths, ILogger<AiLyricsService> logger)
    {
        var root = Path.Combine(paths.Root, "AiLyrics");
        _models = new AiModelPackageService(Path.Combine(root, "Models"));
        _runtime = new AiLyricsRuntimePackage(Assembly.GetExecutingAssembly(), Path.Combine(root, "Runtime"));
        _cache = new AiLyricsCache(Path.Combine(root, "Cache"));
        _translations = new LyricsTranslationCoordinator(_cache);
        _staging = Path.Combine(root, "Staging");
        _logger = logger;
    }

    public event EventHandler? ModelDownloaded;
    public event EventHandler? TranslationStateChanged;
    public bool TranslationPaused => _circuit.IsPaused;
    public long RuntimeExtractionMiB
    {
        get
        {
            var assembly = Assembly.GetExecutingAssembly();
            long total = 0;
            foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith("DropSpace.AiLyricsRuntime.", StringComparison.Ordinal) && name.EndsWith(".exe", StringComparison.Ordinal)))
            { using var stream = assembly.GetManifestResourceStream(name); total += stream?.Length ?? 0; }
            return (total + 1_048_575) / 1_048_576;
        }
    }

    public void ResumeTranslation()
    {
        _circuit.Resume();
        lock (_stateGate) _state = AiLyricsTranslationState.Ready;
        TranslationStateChanged?.Invoke(this, EventArgs.Empty);
        // The existing refresh path cancels the old song generation and starts the current one.
        ModelDownloaded?.Invoke(this, EventArgs.Empty);
    }

    public Task<string?> GetInstalledPathAsync(string modelId, CancellationToken token) =>
        _models.GetInstalledPathAsync(modelId, token);

    public async Task DownloadAsync(string modelId, bool consent, IProgress<double>? progress, CancellationToken token)
    {
        await _models.DownloadAsync(modelId, consent, progress, token, RuntimeExtractionMiB * 1_048_576).ConfigureAwait(false);
        ModelDownloaded?.Invoke(this, EventArgs.Empty);
    }

    public bool HasModelArtifacts(string modelId) => _models.HasArtifacts(modelId);

    public Task DeleteModelAsync(string modelId, CancellationToken token) =>
        _work.MaintainAsync(cancellation => _models.DeleteAsync(modelId, cancellation), token);

    public Task ClearCacheAsync(CancellationToken token) =>
        _work.MaintainAsync(_cache.ClearAsync, token);

    public async Task<LyricsDocument> TranslateIfAvailableAsync(LyricsQuery query, LyricsDocument document,
        LyricsSettings settings, string targetLanguage, CancellationToken token)
    {
        try
        {
            return await _work.RunAsync(cancellation => TranslateCoreAsync(query, document, settings, targetLanguage, cancellation), document, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // Model/cache maintenance cancels only AI, never the original lyric provider.
            return document;
        }
    }

    public async Task<LyricsDocument> TranslateCoreAsync(LyricsQuery query, LyricsDocument document,
        LyricsSettings settings, string targetLanguage, CancellationToken token)
    {
        long statusGeneration;
        lock (_stateGate) statusGeneration = ++_statusGeneration;
        SetState(statusGeneration, AiLyricsTranslationState.Ready);
        if (!settings.Enabled || !settings.AiTranslationEnabled || document.Lines.Count == 0) return document;
        // The approved first version never fills gaps in a source-provided translation.
        if (LyricsTranslationPolicy.HasMatchingProviderTranslation(document, targetLanguage)) return document;
        var model = AiLyricsModelCatalog.Find(settings.AiModelId);
        if (model is null) return document;
        // Validated cached data needs neither executable extraction nor a large model rehash.
        // Actual inference still verifies every model/runtime before execution.
        var cached = await _translations.TryGetCachedAsync(query, document, targetLanguage, model.Sha256, token).ConfigureAwait(false);
        if (cached is not null)
        {
            SetState(statusGeneration, AiLyricsTranslationState.Completed);
            return cached;
        }
        if (!_circuit.TryBegin(out var generation)) return document;
        try
        {
            // This path never downloads. Only the settings consent flow can fetch weights.
            var modelPath = await _models.GetInstalledPathAsync(model.Id, token).ConfigureAwait(false);
            if (modelPath is null) return document;
            var executable = await _runtime.EnsureExecutableAsync(token).ConfigureAwait(false);
            var tokenizer = await _runtime.EnsureTokenizerAsync(token).ConfigureAwait(false);
            SetState(statusGeneration, AiLyricsTranslationState.Translating);
            var result = await _translations.TranslateBatchesAsync(query, document, targetLanguage, model.Sha256,
                (prompt, ids, cancellation) => _runner.RunAsync(executable, modelPath, prompt, _staging, cancellation, model.Sha256, ids), token,
                (prompt, cancellation) => _runner.CountTokensAsync(tokenizer, modelPath, prompt, _staging, cancellation, model.Sha256)).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var success = !ReferenceEquals(document, result);
            RecordResult(generation, success);
            SetState(statusGeneration, success ? AiLyricsTranslationState.Completed : AiLyricsTranslationState.Unavailable);
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            SetState(statusGeneration, AiLyricsTranslationState.Ready);
            throw;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A killed process can surface as an I/O failure before its cancellation await.
            if (token.IsCancellationRequested)
            {
                SetState(statusGeneration, AiLyricsTranslationState.Ready);
                token.ThrowIfCancellationRequested();
            }
            // Timeout, missing runtime and invalid model output must not erase provider lyrics.
            _logger.LogDebug("Local lyric translation unavailable ({Category}); process exit code {ExitCode}.",
                error.GetType().Name, (error as LocalInferenceExecutionException)?.ExitCode);
            RecordResult(generation, false);
            SetState(statusGeneration, AiLyricsTranslationState.Unavailable);
            return document;
        }
    }

    private void SetState(long generation, AiLyricsTranslationState state)
    {
        lock (_stateGate)
        {
            if (_statusGeneration != generation) return;
            _state = state;
        }
        TranslationStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RecordResult(long generation, bool success)
    {
        var paused = _circuit.IsPaused;
        _circuit.Complete(generation, success);
        if (paused != _circuit.IsPaused) TranslationStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _work.Dispose();
        _runner.Dispose();
        _models.Dispose();
    }
}
