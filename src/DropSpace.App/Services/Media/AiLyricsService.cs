using System.Reflection;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;
using DropSpace.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services.Media;

public enum AiLyricsTranslationState { Ready, Translating, Completed, Unavailable, ResourcesUnavailable }

public sealed record AiLyricsExecutionView(PlainLyricsCurrentExecution? Current,
    PlainLyricsExecutionStatus? Last, PlainLyricsRuntimeFailure? Failure, bool FromCache);

/// <summary>Retains the request/cache fence until the final document reaches its UI dispatcher.</summary>
public sealed class AiLyricsPublication(LyricsDocument document, Func<bool> isCurrent)
{
    public LyricsDocument Document { get; } = document;
    public bool IsCurrent => isCurrent();
}

/// <summary>Optional offline translation. Model consent/download is separate from playback.</summary>
public sealed class AiLyricsService : IDisposable
{
    private readonly AiModelPackageService _models;
    private readonly IAiLyricsPackageResolver _packageResolver;
    private readonly IAiLyricsBackend _backend;
    private readonly bool _ownsModels;
    private readonly bool _ownsBackend;
    private readonly AiLyricsCache _cache;
    private readonly AiLyricsWorkLifetime _work;
    private readonly AiLyricsRuntimeOptions _runtimeOptions;
    private readonly CudaLyricsRuntimePackage? _cudaPackage;
    private readonly PersistentPlainLyricsRunner? _residentRunner;
    public bool IsNvidia => CudaDriverAvailability.IsCompatible();
    public string? ActualBackend => _residentRunner?.CurrentExecution?.Backend;
    public AiLyricsExecutionView GetExecutionStatus(LyricsSettings settings)
    {
        var model = AiLyricsModelCatalog.FindSelectable(settings.AiModelId);
        bool Matches(string hash, bool gpu, LyricsGpuBackend preference) => settings.Enabled && settings.AiTranslationEnabled &&
            model?.Sha256 == hash && settings.AiLyricsGpuAccelerationEnabled == gpu && settings.AiLyricsGpuBackend == preference;
        var current = _residentRunner?.CurrentExecution;
        var last = _residentRunner?.LastExecutionStatus;
        var failure = _residentRunner?.LastFailure;
        lock (_stateGate)
            return new(current is not null && Matches(current.ModelSha256, current.GpuEnabled, current.BackendPreference) ? current : null,
                last is not null && Matches(last.ModelSha256, last.GpuEnabled, last.BackendPreference) ? last : null,
                failure is not null && Matches(failure.ModelSha256, failure.GpuEnabled, failure.BackendPreference) ? failure : null,
                _fromCache && settings.Enabled && settings.AiTranslationEnabled && _presentationSettings is { } presented &&
                presented.AiModelId == settings.AiModelId && presented.AiLyricsGpuAccelerationEnabled == settings.AiLyricsGpuAccelerationEnabled &&
                presented.AiLyricsGpuBackend == settings.AiLyricsGpuBackend);
    }

    private readonly SemaphoreSlim _configurationGate = new(1, 1);
    private string? _configuredModelId;
    private bool? _configuredEnabled;
    private readonly ILogger<AiLyricsService> _logger;
    private readonly string _applicationRoot;
    private int _cacheMigrationFailed;
    public bool CacheMigrationFailed => Volatile.Read(ref _cacheMigrationFailed) != 0;
    private readonly LyricsInferenceCircuit _circuit = new();
    private readonly object _stateGate = new();
    private long _statusGeneration;
    private AiLyricsTranslationState _state;
    private bool _fromCache;
    private LyricsSettings? _presentationSettings;
    public AiLyricsTranslationState TranslationState { get { lock (_stateGate) return _state; } }

    public AiLyricsService(AppStoragePaths paths, LyricsCache lyricsCache, ILogger<AiLyricsService> logger)
        : this(paths, lyricsCache, logger, null, null, null) { }

    public AiLyricsService(AppStoragePaths paths, LyricsCache lyricsCache, ILogger<AiLyricsService> logger,
        AiModelPackageService? models, IAiLyricsPackageResolver? packageResolver, IAiLyricsBackend? backend,
        AiLyricsRuntimeOptions? runtimeOptions = null, CudaLyricsRuntimePackage? cudaPackage = null,
        PersistentPlainLyricsRunner? residentRunner = null)
    {
        _cudaPackage = cudaPackage; _residentRunner = residentRunner;
        _applicationRoot = paths.Root;
        var root = Path.Combine(paths.Root, "AiLyrics");
        _ownsModels = models is null;
        _models = models ?? new AiModelPackageService(Path.Combine(root, "Models"));
        var runtime = new AiLyricsRuntimePackage(Assembly.GetExecutingAssembly(), Path.Combine(root, "Runtime"));
        _cache = new AiLyricsCache(lyricsCache);
        _ownsBackend = backend is null;
        _runtimeOptions = runtimeOptions ?? new AiLyricsRuntimeOptions();
        _backend = backend ?? new PlainHyLyricsBackend(new PlainHyLyricsCoordinator(_cache),
            new PersistentPlainLyricsRunner(runtime, _runtimeOptions), runtime, Path.Combine(root, "Staging"));
        _packageResolver = packageResolver ?? new PlainHyLyricsPackageResolver(_models, runtime);
        _work = new AiLyricsWorkLifetime(_backend.DrainCleanupAsync);
        _logger = logger;
        if (_residentRunner is not null) _residentRunner.ExecutionStatusChanged += OnExecutionStatusChanged;
    }

    private void OnExecutionStatusChanged(object? sender, EventArgs args) => TranslationStateChanged?.Invoke(this, EventArgs.Empty);

    public void ObserveSettingsChange(LyricsSettings previous, LyricsSettings next)
    {
        if (previous.Enabled == next.Enabled && previous.AiTranslationEnabled == next.AiTranslationEnabled &&
            previous.AiModelId == next.AiModelId && previous.AiLyricsGpuAccelerationEnabled == next.AiLyricsGpuAccelerationEnabled &&
            previous.AiLyricsGpuBackend == next.AiLyricsGpuBackend) return;
        InvalidateExecutionConfiguration();
    }

    private void InvalidateExecutionConfiguration()
    {
        _runtimeOptions.NotifyBackendChanged();
        _residentRunner?.InvalidateExecutionStatus();
        _circuit.Resume();
        InvalidateTranslation();
    }

    public async Task RetryGpuAsync(CancellationToken token)
    {
        InvalidateExecutionConfiguration();
        await _work.MaintainAsync(_ => Task.CompletedTask, token).ConfigureAwait(false);
        // Existing cache stays valid. Only a subsequent uncached request starts inference.
        ModelDownloaded?.Invoke(this, EventArgs.Empty);
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

    public async Task DownloadAsync(string modelId, bool consent, IProgress<double>? progress, CancellationToken token, IProgress<DropSpace.Core.Downloads.TrackProgress>? transferProgress = null)
    {
        if (AiLyricsModelCatalog.FindSelectable(modelId) is null)
            throw new ArgumentException("This legacy model is available only for removal.", nameof(modelId));
        await _models.DownloadAsync(modelId, consent, progress, token, RuntimeExtractionMiB * 1_048_576, transferProgress).ConfigureAwait(false);
        InvalidateExecutionConfiguration();
        ModelDownloaded?.Invoke(this, EventArgs.Empty);
    }

    public bool HasModelArtifacts(string modelId) => _models.HasArtifacts(modelId);

    public Task<DropSpace.Core.Abstractions.DlcPackageInspection> InspectModelAsync(string modelId, CancellationToken token) =>
        _models.InspectAsync(modelId, token);

    public Task DeleteModelAsync(string modelId, CancellationToken token)
    {
        InvalidateExecutionConfiguration();
        return _work.MaintainAsync(cancellation => _models.DeleteAsync(modelId, cancellation), token);
    }

    public Task DownloadCudaComponentsAsync(bool consent, IProgress<double>? progress, CancellationToken token)
    {
        if (_cudaPackage is null) throw new InvalidOperationException("CUDA components are unavailable.");
        return DownloadCoreAsync();
        async Task DownloadCoreAsync()
        {
            // Transfer can coexist with inference. Activation replaces no live worker files.
            await _cudaPackage.DownloadAsync(consent, progress, token).ConfigureAwait(false);
            InvalidateExecutionConfiguration();
            await _work.MaintainAsync(cancellation =>
            {
                cancellation.ThrowIfCancellationRequested();
                _runtimeOptions.NotifyBackendChanged();
                return Task.CompletedTask;
            }, token).ConfigureAwait(false);
            ModelDownloaded?.Invoke(this, EventArgs.Empty);
        }
    }

    public Task DeleteCudaComponentsAsync(CancellationToken token)
    {
        if (_cudaPackage is null) throw new InvalidOperationException("CUDA components are unavailable.");
        InvalidateExecutionConfiguration();
        return _work.MaintainAsync(async cancellation =>
        {
            await _cudaPackage.RemoveAsync(cancellation).ConfigureAwait(false);
            _runtimeOptions.NotifyBackendChanged();
        }, token);
    }

    public Task ClearCacheAsync(CancellationToken token)
    {
        InvalidateTranslation();
        return _work.MaintainAsync(async cancellation =>
        {
            await _cache.ClearAsync(cancellation).ConfigureAwait(false);
            await RemoveLegacyCacheAsync(cancellation).ConfigureAwait(false);
        }, token);
    }

    /// <summary>Retires queued UI progress synchronously, before cancellation/draining completes.</summary>
    public void InvalidateTranslation()
    {
        lock (_stateGate) { _statusGeneration++; _state = AiLyricsTranslationState.Ready; _fromCache = false; }
        TranslationStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task MigrateCacheAsync(CancellationToken token)
    {
        try { await RemoveLegacyCacheAsync(token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        { _logger.LogDebug("Legacy lyrics cache cleanup needs a retry ({Category}).", exception.GetType().Name); }
    }

    private async Task RemoveLegacyCacheAsync(CancellationToken token)
    {
        try
        {
            await AiLyricsCache.RemoveLegacyAsync(_applicationRoot, token).ConfigureAwait(false);
            Volatile.Write(ref _cacheMigrationFailed, 0);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        { Volatile.Write(ref _cacheMigrationFailed, 1); throw; }
        finally { TranslationStateChanged?.Invoke(this, EventArgs.Empty); }
    }

    public async Task<LyricsDocument> TranslateIfAvailableAsync(LyricsQuery query, LyricsDocument document,
        LyricsSettings settings, string targetLanguage, CancellationToken token, LyricsTranslationProgressContext? progress = null) =>
        (await TranslateForPublicationAsync(query, document, settings, targetLanguage, token, progress).ConfigureAwait(false)).Document;

    public async Task<AiLyricsPublication> TranslateForPublicationAsync(LyricsQuery query, LyricsDocument document,
        LyricsSettings settings, string targetLanguage, CancellationToken token, LyricsTranslationProgressContext? progress = null)
    {
        document = LyricsLanguagePolicy.IdentifyProviderTranslations(document);
        document = LyricsLanguagePolicy.RemoveIneligibleLocalTranslations(document, targetLanguage);
        Func<bool> isCurrent = () => !token.IsCancellationRequested && (progress?.IsCurrent ?? true);
        if (!settings.Enabled || !settings.AiTranslationEnabled ||
            LyricsLanguagePolicy.EligibleIndices(document, targetLanguage).Length == 0)
        {
            if (!isCurrent()) return new(document, () => false);
            // Retire the preceding presentation before any asynchronous configuration wait.
            // A stale bypass continuation must never invalidate a newer song's AI result.
            InvalidateTranslation();
            // A provider/same-language bypass never initializes AI. If a resident runtime
            // was already configured, setting changes must still retire its old owner.
            await ConfigureRuntimeAsync(settings, isCurrent, token, onlyIfConfigured: true).ConfigureAwait(false);
            return new(document, isCurrent);
        }
        try
        {
            await ConfigureRuntimeAsync(settings, isCurrent, token).ConfigureAwait(false);
            if (!isCurrent()) return new(document, () => false);
            var translated = await _work.RunAsync(cancellation => TranslateCoreAsync(query, document, settings,
                targetLanguage, cancellation, progress, fence => isCurrent = fence), document, token).ConfigureAwait(false);
            return new(translated, isCurrent);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // Model/cache maintenance cancels only AI, never the original lyric provider.
            return new(document, () => false);
        }
    }

    private async Task ConfigureRuntimeAsync(LyricsSettings settings, Func<bool> isCurrent, CancellationToken token, bool onlyIfConfigured = false)
    {
        await _configurationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            if (!isCurrent() || onlyIfConfigured && _configuredEnabled is null) return;
            var enabled = settings.Enabled && settings.AiTranslationEnabled;
            if (_configuredEnabled == enabled && _configuredModelId == settings.AiModelId &&
                _runtimeOptions.GpuEnabled == settings.AiLyricsGpuAccelerationEnabled &&
                _runtimeOptions.Backend == settings.AiLyricsGpuBackend) return;
            InvalidateTranslation();
            // GPU/model/disable changes release the old resident owner before a new profile
            // becomes visible. Ordinary song changes keep the verified model warm.
            await _work.MaintainAsync(cancellation =>
            {
                cancellation.ThrowIfCancellationRequested();
                if (!isCurrent()) return Task.CompletedTask;
                _runtimeOptions.GpuEnabled = settings.AiLyricsGpuAccelerationEnabled;
                _runtimeOptions.Backend = settings.AiLyricsGpuBackend;
                _configuredModelId = settings.AiModelId;
                _configuredEnabled = enabled;
                return Task.CompletedTask;
            }, token).ConfigureAwait(false);

        }
        finally { _configurationGate.Release(); }
    }

    public async Task<LyricsDocument> TranslateCoreAsync(LyricsQuery query, LyricsDocument document,
        LyricsSettings settings, string targetLanguage, CancellationToken token, LyricsTranslationProgressContext? progress = null,
        Action<Func<bool>>? capturePublicationFence = null)
    {
        long statusGeneration;
        lock (_stateGate) { statusGeneration = ++_statusGeneration; _presentationSettings = settings; }
        SetState(statusGeneration, AiLyricsTranslationState.Ready);
        if (!settings.Enabled || !settings.AiTranslationEnabled || document.Lines.Count == 0) return document;
        // Native target translations keep priority on their own rows; eligible gaps can be filled.
        document = LyricsLanguagePolicy.IdentifyProviderTranslations(document);
        document = LyricsLanguagePolicy.RemoveIneligibleLocalTranslations(document, targetLanguage);
        document = LyricsLanguagePolicy.MarkTranslationStates(document, targetLanguage);
        if (LyricsLanguagePolicy.EligibleIndices(document, targetLanguage).Length == 0) return document;
        // Validated cached data needs neither executable extraction nor a large model rehash.
        // Actual inference still verifies every model/runtime before execution.
        var cacheGeneration = _cache.Generation;
        var executionGeneration = _cache.ExecutionGeneration;
        bool IsCurrent() => !token.IsCancellationRequested && _cache.ExecutionGeneration == executionGeneration &&
            Interlocked.Read(ref _statusGeneration) == statusGeneration && (progress?.IsCurrent ?? true);
        capturePublicationFence?.Invoke(IsCurrent);
        // Even a caller that does not display partial output gets the same request/cache fence.
        var acceptingProgress = 1;
        var guardedProgress = (progress ?? new LyricsTranslationProgressContext(() => TimeSpan.MinValue,
            () => true, (_, _) => Task.CompletedTask)).WithFence(cacheGeneration, () => Volatile.Read(ref acceptingProgress) != 0 && IsCurrent());
        if (!IsCurrent()) return document;
        var cached = await _backend.TryGetCachedResultAsync(settings.AiModelId, query, document, targetLanguage, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!IsCurrent()) return document;
        if (cached is not null)
        {
            SetState(statusGeneration, cached.Outcome == LyricsTranslationOutcome.Translated
                ? AiLyricsTranslationState.Completed : AiLyricsTranslationState.Ready, fromCache: true);
            return cached.Document;
        }
        if (!_circuit.TryBegin(out var generation)) return LyricsLanguagePolicy.FailPending(document, "inference-circuit-paused");
        try
        {
            // This path never downloads. Only the settings consent flow can fetch weights.
            var package = await _packageResolver.ResolveAsync(settings.AiModelId, query, document, targetLanguage, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!IsCurrent()) return document;
            if (package is null || !StringComparer.Ordinal.Equals(package.BackendId, _backend.Id))
                return LyricsLanguagePolicy.FailPending(document, "model-or-runtime-not-ready");
            SetState(statusGeneration, AiLyricsTranslationState.Translating);
            var result = await _backend.TranslateAsync(package with { CacheGeneration = cacheGeneration },
                query, document, targetLanguage, token, guardedProgress).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!IsCurrent())
            {
                SetState(statusGeneration, AiLyricsTranslationState.Ready);
                return document;
            }
            CompleteTranslation(statusGeneration, generation, result.Outcome, result.FromCache);
            return result.Document;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            SetState(statusGeneration, AiLyricsTranslationState.Ready);
            throw;
        }
        catch (InferenceResourcesUnavailableException)
        {
            if (token.IsCancellationRequested)
            {
                SetState(statusGeneration, AiLyricsTranslationState.Ready);
                token.ThrowIfCancellationRequested();
            }
            // Resource admission is recoverable when host memory becomes available.
            // It is not an inference/model failure and must not trip the failure circuit.
            SetState(statusGeneration, AiLyricsTranslationState.ResourcesUnavailable);
            return LyricsLanguagePolicy.FailPending(document, "inference-resources-unavailable");
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
            return LyricsLanguagePolicy.FailPending(document, "inference-" + error.GetType().Name);
        }
        finally { Interlocked.Exchange(ref acceptingProgress, 0); }
    }

    internal void CompleteTranslation(long statusGeneration, long circuitGeneration, LyricsTranslationOutcome outcome, bool fromCache = false)
    {
        if (outcome == LyricsTranslationOutcome.NoUsefulTranslation)
            SetState(statusGeneration, AiLyricsTranslationState.Ready, fromCache);
        else
        {
            var success = outcome == LyricsTranslationOutcome.Translated;
            RecordResult(circuitGeneration, success);
            SetState(statusGeneration, success ? AiLyricsTranslationState.Completed : AiLyricsTranslationState.Unavailable, fromCache);
        }
    }

    private void SetState(long generation, AiLyricsTranslationState state, bool fromCache = false)
    {
        lock (_stateGate)
        {
            if (_statusGeneration != generation) return;
            _state = state;
            _fromCache = fromCache;
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
        if (_residentRunner is not null) _residentRunner.ExecutionStatusChanged -= OnExecutionStatusChanged;
        InvalidateTranslation();
        _work.Dispose();
        if (_ownsBackend) _backend.Dispose();
        if (_ownsModels) _models.Dispose();
    }
}
