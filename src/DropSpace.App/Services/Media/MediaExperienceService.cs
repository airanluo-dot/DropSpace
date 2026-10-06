using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Channels;
using DropSpace.App.Services.Audio;
using DropSpace.App.ViewModels;
using DropSpace.Core.Island;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Media;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Lyrics;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace DropSpace.App.Services.Media;

public sealed class MediaExperienceService : IAsyncDisposable
{
    private readonly MainViewModel _main;
    private readonly MediaViewModel _view;
    private readonly WindowsMediaSessionService _media;
    private readonly WindowsProcessLoopbackService _audio;
    private readonly MediaProcessResolver _processes;
    private readonly MediaArtworkService _artwork;
    private readonly IslandExperienceCoordinator _experience;
    private readonly DispatcherQueue _dispatcher;
    private readonly ILogger<MediaExperienceService> _logger;
    private readonly SystemVisualPreferenceService _visualPreferences;
    private readonly HttpClient _http;
    private readonly LyricsService _lyrics;
    private readonly LyricsCache _lyricsCache;
    private readonly object _lyricsDiagnosticGate = new();
    private long _lyricsDiagnosticWindow = Stopwatch.GetTimestamp();
    private int _lyricsDiagnosticCount, _lyricsDiagnosticsSuppressed;
    public AiLyricsService AiLyrics { get; }
    public QqMusicLoginService QqMusicLogin { get; }
    private readonly LyricsTimelineEngine _timeline = new();
    private readonly MediaPlaybackClock _clock = new();
    private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _lyricsMaintenance = new(1, 1);
    private readonly DispatcherQueueTimer _frames;
    private readonly Timer _audioRecovery;
    private long _lastAudioAttempt;
    private readonly MediaSoftRestartOperation _restart = new(TimeSpan.FromSeconds(20));
    private readonly RetirableMediaWork _lyricsWork = new(), _artworkWork = new();
    private Task _worker = Task.CompletedTask, _audioTransition = Task.CompletedTask;
    private readonly object _runtimeGate = new();
    private readonly List<Task> _runtimeRetirements = [];
    private MediaWorkCancellation? _runtimeStop;
    private AppSettings _settings = new();
    private MediaSessionSnapshot _latest = MediaSessionSnapshot.Empty;
    private sealed record SpectrumObservation(SpectrumFrame Frame, long Timestamp);
    private SpectrumObservation _spectrum = new(SpectrumFrame.Empty, 0);
    private LyricsDocument _document = LyricsDocument.Empty;
    private LyricsDocument _sourceDocument = LyricsDocument.Empty;
    private long _generation, _artworkGeneration, _lyricPositionTicks;
    private long _reloadRequest;
    private readonly MediaLyricsRefreshRequest _sourceRefresh = new();
    private bool _initialized, _disposed;

    public MediaExperienceService(MainViewModel main, MediaViewModel view, WindowsMediaSessionService media,
        WindowsProcessLoopbackService audio, MediaProcessResolver processes, MediaArtworkService artwork,
        IslandExperienceCoordinator experience, DispatcherQueue dispatcher, ILogger<MediaExperienceService> logger,
        SystemVisualPreferenceService visualPreferences, AiLyricsService aiLyrics, LyricsCache lyricsCache, QqMusicLoginService qqMusicLogin)
    {
        _main = main; _view = view; _media = media; _audio = audio; _processes = processes; _artwork = artwork;
        _experience = experience; _dispatcher = dispatcher; _logger = logger;
        _visualPreferences = visualPreferences; AiLyrics = aiLyrics; _lyricsCache = lyricsCache;
        QqMusicLogin = qqMusicLogin;
        QqMusicLogin.Session.CredentialsChanged += OnQqCredentialsChanged;
        AiLyrics.ModelDownloaded += OnModelDownloaded;
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
        _lyrics = new LyricsService(new LyricsProviderRegistry(new LyricsHttpClient(_http, RecordLyricsDiagnostic, qqMusicLogin.Session), () => Volatile.Read(ref _settings).Lyrics.LocalLrcDirectory), lyricsCache, RecordLyricsDiagnostic);
        _frames = dispatcher.CreateTimer(); _frames.Interval = TimeSpan.FromMilliseconds(33); _frames.IsRepeating = true;
        _frames.Tick += OnFrame;
        _audioRecovery = new Timer(_ =>
        {
            var observation = Volatile.Read(ref _spectrum);
            if (AudioCaptureRecoveryPolicy.ShouldRecover(true, observation.Frame.CaptureMode,
                Stopwatch.GetElapsedTime(observation.Timestamp), Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastAudioAttempt))))
                _changes.Writer.TryWrite(true);
        }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _experience.Changed += OnExperienceChanged;
        _visualPreferences.Changed += OnVisualPreferencesChanged;
        _view.PropertyChanged += OnPresentationChanged;
    }

    private void OnQqCredentialsChanged(object? sender, EventArgs args)
    {
        if (_disposed) return;
        _sourceRefresh.Request();
        Interlocked.Increment(ref _reloadRequest);
        Interlocked.Increment(ref _generation);
        _lyricsWork.CancelCurrent();
        _changes.Writer.TryWrite(true);
    }

    public async Task VerifyQqMusicAsync(CancellationToken token)
    {
        await QqMusicLogin.Session.LoadAsync(token).ConfigureAwait(false);
        QqMusicLogin.Session.AllowExplicitRetry();
        var current = Volatile.Read(ref _latest);
        var query = string.IsNullOrWhiteSpace(current.TrackTitle)
            ? new LyricsQuery("Happier", "Ed Sheeran", "", TimeSpan.FromSeconds(207))
            : new LyricsQuery(current.TrackTitle, current.Artist, current.AlbumTitle, current.Timeline.Duration)
                { AlbumArtist = current.AlbumArtist, TrackIdentity = current.LyricsCacheIdentity };
        query = query with { PreferredTranslationLanguage = "zh-Hans" };
        using var trace = LyricsRequestTrace.Begin(new { diagnostic = "qq-login-check", query.Title, query.Artist,
            query.Album, duration = query.Duration.TotalSeconds });
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        var result = await _lyrics.QueryDetailedAsync(query, Volatile.Read(ref _settings).Lyrics with
        {
            Enabled = true, Mode = LyricsMode.Online, Provider = LyricsProviderKind.QqMusic,
            BackupProvider = null, SearchRemainingProviders = false, SelectionMode = LyricsSelectionMode.Rules,
        }, deadline.Token, refresh: true).ConfigureAwait(false);
        trace.Write("qq-login-result", new { status = result.Status.ToString(), result.TranslationLookupIncomplete,
            document = LyricsRequestTrace.Describe(result.Document) });
    }

    public Task InitializeAsync(AppSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized) return Task.CompletedTask;
        _initialized = true; _settings = settings;
        _lyricsCache.SetMaximumBytesPolicy(settings.Lyrics.CacheMaximumBytes);
        _main.PropertyChanged += OnSettingsChanged;
        _media.Changed += OnMediaChanged; _audio.Changed += OnSpectrumChanged;
        StartRuntime();
        _changes.Writer.TryWrite(true);
#if DEBUG
        _ = LyricsRapidSkipDiagnostic.RunAsync(_media, _stop.Token);
        _ = LyricsRapidSkipDiagnostic.CheckProvidersAsync(_media, _lyrics, settings.Lyrics, _stop.Token);
#endif
        return Task.CompletedTask;
    }

    private void RecordLyricsDiagnostic(LyricsDiagnostic diagnostic)
    {
        // Successful transport calls are covered by their validated provider summary.
        // Keep a fixed log budget even if a publisher repeatedly changes metadata.
        if (diagnostic.Stage is LyricsDiagnosticStage.Search or LyricsDiagnosticStage.Lyric &&
            diagnostic.Outcome == LyricsDiagnosticOutcome.Found) return;
        int suppressed;
        lock (_lyricsDiagnosticGate)
        {
            if (Stopwatch.GetElapsedTime(_lyricsDiagnosticWindow) >= TimeSpan.FromMinutes(1))
            {
                _lyricsDiagnosticWindow = Stopwatch.GetTimestamp();
                _lyricsDiagnosticCount = 0;
            }
            if (_lyricsDiagnosticCount >= 64)
            {
                if (_lyricsDiagnosticsSuppressed < int.MaxValue) _lyricsDiagnosticsSuppressed++;
                return;
            }
            _lyricsDiagnosticCount++;
            suppressed = _lyricsDiagnosticsSuppressed;
            _lyricsDiagnosticsSuppressed = 0;
        }
        _logger.LogInformation("Lyrics query diagnostic: provider={Provider}, stage={Stage}, outcome={Outcome}, elapsedMs={Elapsed}, lines={Lines}, translated={Translated}, http={Http}, api={Api}, translationIncomplete={Incomplete}, suppressed={Suppressed}.",
            diagnostic.Provider, diagnostic.Stage, diagnostic.Outcome, diagnostic.ElapsedMilliseconds,
            diagnostic.Lines, diagnostic.TranslatedLines, diagnostic.HttpStatus, diagnostic.ApiCode,
            diagnostic.TranslationLookupIncomplete, suppressed);
    }
    /// <summary>Reconnects only the music runtime. Settings, models and lyric caches are retained.</summary>
    public Task RestartAsync(CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_initialized) throw new InvalidOperationException("Music has not initialized.");
        return _restart.RunAsync(RestartCoreAsync, _stop.Token, token);
    }

    private async Task RestartCoreAsync(CancellationToken token)
    {
        // Cancel the coordinator first, so queued old snapshots cannot overwrite recovery.
        lock (_runtimeGate) _runtimeStop?.Request();
        Interlocked.Increment(ref _generation);
        Interlocked.Increment(ref _artworkGeneration);
        _lyricsWork.CancelCurrent();
        AiLyrics.InvalidateTranslation();
        _artworkWork.CancelCurrent();
        TaskCompletionSource? ready = null;
        try
        {
            try { await _worker.WaitAsync(token).ConfigureAwait(false); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { token.ThrowIfCancellationRequested(); }
            _lyricsWork.RetireCurrent();
            _artworkWork.RetireCurrent();
            await _media.RestartAsync(token).WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!_media.IsAvailable)
                throw new InvalidOperationException("Music reconnection failed: " + _media.AvailabilityReason);
            // A same-track reload must restart capture too, rather than keeping a stalled
            // capture worker merely because its process ID has not changed.
            await SetAudioSourceAsync(null, false, token).ConfigureAwait(false);
            Volatile.Write(ref _latest, _media.Current);
            Volatile.Write(ref _spectrum, new SpectrumObservation(SpectrumFrame.Empty, 0));
            _sourceRefresh.Request();
            Interlocked.Increment(ref _reloadRequest);
            ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = MediaSoftRestartOperation.ObserveAsync(ready.Task);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogWarning("Music soft restart failed ({Category}).", exception.GetType().Name);
            throw;
        }
        finally
        {
            // Never run two readers if even retirement failed to finish in budget.
            if (_worker.IsCompleted) StartRuntime(ready);
        }
        // Success means a fresh native manager and a resumed presentation pass, not only
        // a changed button label. Lyrics/artwork finish asynchronously under new generations.
        await ready!.Task.WaitAsync(token).ConfigureAwait(false);
        if (!_media.IsAvailable)
            throw new InvalidOperationException("Music became unavailable during reconnection: " + _media.AvailabilityReason);
    }

    private void StartRuntime(TaskCompletionSource? ready = null)
    {
        lock (_runtimeGate)
        {
            if (_disposed || _stop.IsCancellationRequested)
            {
                ready?.TrySetCanceled();
                return;
            }
            _runtimeRetirements.RemoveAll(task => task.IsCompleted);
            if (_runtimeRetirements.Count >= 2)
            {
                ready?.TrySetException(new InvalidOperationException("Previous music coordinators are still retiring."));
                return;
            }
            var cancellation = new MediaWorkCancellation(_stop.Token);
            _runtimeStop = cancellation;
            _worker = Task.Run(() => RunAsync(cancellation.Token, ready));
            _runtimeRetirements.Add(cancellation.CompleteWhenAsync(_worker));
            _changes.Writer.TryWrite(true);
        }
    }

    private async Task SetAudioSourceAsync(uint? process, bool enabled, CancellationToken token)
    {
        // Retain one real capture transition if native audio ignores cancellation. Retrying
        // recovery waits on that owner instead of queuing unbounded calls behind its gate.
        try { await _audioTransition.WaitAsync(token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { token.ThrowIfCancellationRequested(); }
        token.ThrowIfCancellationRequested();
        _audioTransition = _audio.SetSourceAsync(process, enabled, token);
        await _audioTransition.WaitAsync(token).ConfigureAwait(false);
    }

    public async Task ClearLyricsCacheAsync(CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
        await _lyricsMaintenance.WaitAsync(linked.Token);
        try
        {
            Interlocked.Increment(ref _generation);
            _lyricsWork.CancelCurrent();
            await _lyricsWork.DrainAsync(linked.Token);
            await _dispatcher.EnqueueAsync(() =>
            {
                if (_disposed || linked.IsCancellationRequested) return Task.CompletedTask;
                _document = LyricsDocument.Empty;
                _view.SetLyricsDocument(LyricsDocument.Empty);
                _view.Lyrics = LyricsHighlightFrame.Empty;
                return Task.CompletedTask;
            }).WaitAsync(linked.Token);
            // Both source fetches and AI work have drained before deleting their shared store.
            // Surface deletion failures to the initiating control instead of reporting success.
            _lyrics.ClearResponseCaches();
            await AiLyrics.ClearCacheAsync(linked.Token);
        }
        finally
        {
            Interlocked.Increment(ref _generation);
            Interlocked.Increment(ref _reloadRequest);
            _lyricsMaintenance.Release();
            _changes.Writer.TryWrite(true);
        }
    }
    private void OnModelDownloaded(object? sender, EventArgs args)
    {
        Interlocked.Increment(ref _generation);
        _lyricsWork.CancelCurrent();
        AiLyrics.InvalidateTranslation();
        Interlocked.Increment(ref _reloadRequest);
        _changes.Writer.TryWrite(true);
    }
    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(MainViewModel.Settings)) return;
        var previous = Interlocked.Exchange(ref _settings, _main.Settings);
        AiLyrics.ObserveSettingsChange(previous.Lyrics, _main.Settings.Lyrics);
        if (LyricsReloadPolicy.RequiresReload(previous, _main.Settings))
        {
            Interlocked.Increment(ref _generation);
            _lyricsWork.CancelCurrent();
            AiLyrics.InvalidateTranslation();
        }
        _changes.Writer.TryWrite(true);
    }
    private void OnMediaChanged(object? sender, MediaSessionSnapshot snapshot)
    {
        var previous = Interlocked.Exchange(ref _latest, snapshot);
        if (!previous.IsSameTrack(snapshot))
        {
            // Invalidate at observation, not later in the coordinator queue: an already
            // queued partial update or final cache write belongs to the old song now.
            Interlocked.Increment(ref _generation);
            // Preserve the observed cancellation even if queued snapshots coalesce
            // A -> B -> A and the coordinator sees the same final track as before.
            Interlocked.Increment(ref _reloadRequest);
            _lyricsWork.CancelCurrent();
            AiLyrics.InvalidateTranslation();
        }
        _changes.Writer.TryWrite(true);
    }
    private void OnSpectrumChanged(object? sender, SpectrumFrame frame) =>
        Volatile.Write(ref _spectrum, new SpectrumObservation(frame, Stopwatch.GetTimestamp()));

    private async Task RunAsync(CancellationToken token, TaskCompletionSource? ready = null)
    {
        AppSettings? previousSettings = null;
        MediaSessionSnapshot? previousMedia = null;
        string? audioKey = null;
        bool? previousObserve = null;
        long previousReloadRequest = -1;
        bool lyricsPending = false, artworkPending = false;
        try
        {
            await foreach (var _ in _changes.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                try
                {
                    var settings = Volatile.Read(ref _settings);
                    if (previousSettings is null) await AiLyrics.MigrateCacheAsync(token).WaitAsync(token).ConfigureAwait(false);
                    if (previousSettings?.Lyrics.CacheMaximumBytes != settings.Lyrics.CacheMaximumBytes)
                    {
                        try { await _lyricsCache.TrimToCurrentQuotaAsync(token).WaitAsync(token).ConfigureAwait(false); }
                        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
                        { _logger.LogDebug("Lyrics cache quota could not be applied ({Category}).", exception.GetType().Name); }
                    }
                    var reloadRequest = Interlocked.Read(ref _reloadRequest);
                    var observe = settings.IslandActivity.EnableMediaActivity || _view.IsPresentationVisible;
                    if (previousSettings?.IslandActivity != settings.IslandActivity || previousObserve != observe)
                    {
                        _media.SetAllowedSources(settings.IslandActivity.AllowedMediaSourceAppIds, settings.IslandActivity.UseMediaSourceAllowList);
                        await _media.SetEnabledAsync(observe, token).WaitAsync(token).ConfigureAwait(false);
                        previousObserve = observe;
                    }
                    var session = Volatile.Read(ref _latest);
                    var playing = session.PlaybackState == MediaPlaybackState.Playing && !string.IsNullOrWhiteSpace(session.TrackTitle);
                    var trackChanged = previousMedia is null || !previousMedia.IsSameTrack(session);
                    var improvedLyricsEvidence = ShouldRetryLyricsWithImprovedEvidence(previousMedia, session, _document.Lines.Count > 0);
                    var lyricsSettingsChanged = LyricsReloadPolicy.RequiresReload(previousSettings, settings);
                    var reload = trackChanged || improvedLyricsEvidence || lyricsSettingsChanged || previousSettings?.IslandActivity.EnableMediaActivity != settings.IslandActivity.EnableMediaActivity ||
                        previousReloadRequest != reloadRequest || lyricsPending;
                    var reloadArtwork = trackChanged || !ReferenceEquals(previousMedia?.Artwork, session.Artwork) || artworkPending;
                    // Invalidate before publishing the new track so a queued old result
                    // cannot paint over the cleared lyric state.
                    var generation = reload ? Interlocked.Increment(ref _generation) : Interlocked.Read(ref _generation);
                    if (reload) _lyricsWork.RetireCurrent();
                    var artworkGeneration = reloadArtwork ? Interlocked.Increment(ref _artworkGeneration) : Interlocked.Read(ref _artworkGeneration);
                    if (reloadArtwork) _artworkWork.RetireCurrent();
                    await _dispatcher.EnqueueAsync(() =>
                    {
                        if (_disposed || token.IsCancellationRequested) return Task.CompletedTask;
                        _clock.Update(session);
                        var resetLyrics = trackChanged || improvedLyricsEvidence || lyricsSettingsChanged || previousReloadRequest != reloadRequest;
                        // Clear the old frame before publishing the new session. Property
                        // subscribers render synchronously, so assigning Session first would
                        // briefly display the previous song's lyric under the new title.
                        if (resetLyrics)
                        {
                            _document = LyricsDocument.Empty;
                            _sourceDocument = LyricsDocument.Empty;
                            _view.SetLyricsDocument(LyricsDocument.Empty);
                            _view.Lyrics = LyricsHighlightFrame.Empty;
                        }
                        if (trackChanged) _view.Artwork = null;
                        _view.Settings = settings; _view.Session = session; _view.PositionEstimated = _clock.IsEstimated;
                        if (resetLyrics)
                            _view.LyricsStatus = settings.Lyrics.Enabled && !string.IsNullOrWhiteSpace(session.TrackTitle)
                                ? LyricsQueryStatus.Loading : LyricsQueryStatus.Disabled;
                        _view.IsReducedMotion = _visualPreferences.IsReducedMotion(settings.OverlayMotion);
                        _experience.UpdateMedia(session.IsActive && !string.IsNullOrWhiteSpace(session.TrackTitle),
                            session.PlaybackState == MediaPlaybackState.Playing,
                            settings.IslandActivity.EnableMediaActivity, settings.IslandAppearance.HideDelayMilliseconds, contentIdentity:
                            string.Join("\u001f", session.SessionId, session.SourceAppUserModelId, session.TrackTitle));
                        RenderFrame(); UpdateFrameTimer();
                        return Task.CompletedTask;
                    }).WaitAsync(token).ConfigureAwait(false);
                    if (reload)
                    {
                        // Lyrics and cache maintenance are optional to the metadata path.
                        // Never wait here for a cancelled provider or a clear-cache drain.
                        var refreshRequest = _sourceRefresh.Capture();
                        var forceSourceRefresh = _sourceRefresh.IsPending(refreshRequest);
                        // Publishers update title/credits/timeline separately, in either order.
                        // Publish the UI immediately, but let a provisional duration-only or
                        // zero-duration snapshot retire before it can issue duplicate searches.
                        var admissionDelay = session.Timeline.Duration <= TimeSpan.Zero ||
                            trackChanged && previousMedia is not null &&
                            previousMedia.Timeline.Duration > TimeSpan.Zero &&
                            (previousMedia.TrackIdentity == session.TrackIdentity ||
                                previousMedia.Timeline.Duration == session.Timeline.Duration)
                            ? TimeSpan.FromMilliseconds(1500) : TimeSpan.FromMilliseconds(400);
                        lyricsPending = !_lyricsWork.TryStartWhileIdle(_lyricsMaintenance,
                            workToken => LoadLyricsAsync(session, settings, generation, workToken, forceSourceRefresh, admissionDelay), token,
                            () => _changes.Writer.TryWrite(true));
                        if (!lyricsPending) _sourceRefresh.MarkStarted(refreshRequest);
                        if (lyricsPending)
                            await _dispatcher.EnqueueAsync(() =>
                            {
                                if (!_disposed && !token.IsCancellationRequested && generation == Interlocked.Read(ref _generation))
                                    _view.LyricsStatus = settings.Lyrics.Enabled && !string.IsNullOrWhiteSpace(session.TrackTitle)
                                        ? LyricsQueryStatus.Loading : LyricsQueryStatus.Disabled;
                                return Task.CompletedTask;
                            }).WaitAsync(token).ConfigureAwait(false);
                    }
                    if (reloadArtwork)
                        artworkPending = !_artworkWork.TryStart(
                            workToken => LoadArtworkAsync(session, artworkGeneration, workToken), token,
                            () => _changes.Writer.TryWrite(true));
                    var sourceKey = playing && ((settings.IslandActivity.ShowSpectrum && _view.IsPresentationVisible) || _view.IsIslandGlowActive) ? session.SourceAppUserModelId : string.Empty;
                    var observation = Volatile.Read(ref _spectrum);
                    var recoverAudio = AudioCaptureRecoveryPolicy.ShouldRecover(sourceKey.Length > 0,
                        observation.Frame.CaptureMode, Stopwatch.GetElapsedTime(observation.Timestamp),
                        Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastAudioAttempt)));
                    // Track metadata changes do not change the audio producer. Re-resolving
                    // every song can turn a transient ambiguous process snapshot into an
                    // explicit StopCoreAsync(null), killing an otherwise healthy capture.
                    if (audioKey != sourceKey || recoverAudio)
                    {
                        _logger.LogInformation("Audio capture transition: wanted={Wanted}, sourceChanged={SourceChanged}, trackChanged={TrackChanged}, recovery={Recovery}, mode={Mode}.",
                            sourceKey.Length > 0, audioKey != sourceKey, trackChanged, recoverAudio, observation.Frame.CaptureMode);
                        Interlocked.Exchange(ref _lastAudioAttempt, Stopwatch.GetTimestamp());
                        _audioRecovery.Change(sourceKey.Length > 0 ? TimeSpan.FromSeconds(2) : Timeout.InfiniteTimeSpan,
                            sourceKey.Length > 0 ? TimeSpan.FromSeconds(2) : Timeout.InfiniteTimeSpan);
                        var resolved = true;
                        uint? process = null;
                        if (sourceKey.Length > 0)
                        {
                            try
                            {
                                process = await _processes.ResolveAudioAsync(sourceKey, token).WaitAsync(token).ConfigureAwait(false);
                                if (process is null) _logger.LogWarning("Audio capture process identity is unavailable or ambiguous.");
                            }
                            catch (Exception exception) when (exception is not OperationCanceledException)
                            { resolved = false; _logger.LogDebug("Player process resolution unavailable ({Category}).", exception.GetType().Name); }
                        }
                        try
                        {
                            if (recoverAudio)
                                await SetAudioSourceAsync(null, false, token).ConfigureAwait(false);
                            await SetAudioSourceAsync(process, resolved && sourceKey.Length > 0, token).ConfigureAwait(false);
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        { resolved = false; _logger.LogDebug("Player capture unavailable ({Category}).", exception.GetType().Name); }
                        audioKey = sourceKey; // Failed attempts retry through the bounded recovery clock.
                    }
                    previousSettings = settings; previousMedia = session;
                    previousReloadRequest = reloadRequest;
                    ready?.TrySetResult();
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                { ready?.TrySetException(exception); _logger.LogWarning("Media presentation update failed ({Category}).", exception.GetType().Name); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { ready?.TrySetCanceled(token); }
    }

    private async Task LoadLyricsAsync(MediaSessionSnapshot session, AppSettings settings, long generation, CancellationToken token, bool refresh, TimeSpan admissionDelay)
    {
        using var trace = LyricsRequestTrace.Begin(new { generation, session.TrackTitle, session.Artist, session.AlbumArtist, session.AlbumTitle,
            duration = session.Timeline.Duration.TotalSeconds, session.SourceAppUserModelId, playback = session.PlaybackState.ToString(),
            cacheIdentity = LyricsRequestTrace.Key(session.LyricsCacheIdentity), language = settings.Language.ToString(),
            culture = System.Globalization.CultureInfo.CurrentUICulture.Name, refresh, settings.Lyrics.Enabled,
            provider = settings.Lyrics.Provider.ToString(), backup = settings.Lyrics.BackupProvider?.ToString(),
            settings.Lyrics.SearchRemainingProviders, settings.Lyrics.SecondaryLyrics, settings.Lyrics.AiTranslationEnabled });
        LyricsQueryResult? sourceResult = null;
        var previewClosed = 0;
        var started = Stopwatch.GetTimestamp();
        try
        {
            // Coalesce transient SMTC snapshots/rapid skips before sending irreversible
            // public HTTP traffic. Stable tracks incur only this short admission delay.
            if (!refresh)
                await Task.Delay(admissionDelay, token).ConfigureAwait(false);
            if (!IsLyricsRequestCurrent(session, settings, generation, token)) return;
            var targetLanguage = LyricsTranslationPolicy.ResolveTarget(settings.Language, [System.Globalization.CultureInfo.CurrentUICulture.Name]);
            var result = !string.IsNullOrWhiteSpace(session.TrackTitle)
                ? await _lyrics.QueryDetailedAsync(new(session.TrackTitle, session.Artist, session.AlbumTitle,
                    session.Timeline.Duration, session.LyricsCacheIdentity, session.AlbumArtist) { PreferredTranslationLanguage = targetLanguage }, settings.Lyrics with { SelectionMode = LyricsSelectionMode.Rules }, token, refresh,
                    original => _dispatcher.TryEnqueue(() =>
                    {
                        // Validate freshness after dispatch; a fast skip may retire the callback in the queue.
                        if (Volatile.Read(ref previewClosed) != 0 || !IsLyricsRequestCurrent(session, settings, generation, token)) return;
                        var cleaned = LyricsLanguagePolicy.RemoveIneligibleLocalTranslations(
                            LyricsLanguagePolicy.IdentifyProviderTranslations(original), targetLanguage);
                        _document = cleaned;
                        _sourceDocument = cleaned;
                        _view.SetLyricsDocument(cleaned);
                        trace.Write("ui-preview", LyricsRequestTrace.Describe(cleaned));
                        _view.LyricsStatus = LyricsQueryStatus.Found;
                        RenderFrame();
                    })).ConfigureAwait(false)
                : new(LyricsDocument.Empty, LyricsQueryStatus.Disabled);
            Interlocked.Exchange(ref previewClosed, 1);
            // The initial view and every failure/retired-fence fallback share this cleaned source.
            result = result with { Document = LyricsLanguagePolicy.RemoveIneligibleLocalTranslations(
                LyricsLanguagePolicy.IdentifyProviderTranslations(result.Document), targetLanguage) };
            sourceResult = result;
            trace.Write("source-result", new { status = result.Status.ToString(), result.TranslationLookupIncomplete, document = LyricsRequestTrace.Describe(result.Document) });
            RecordLyricsDiagnostic(new(result.Document.Lines.Count > 0 ? result.Document.Provider :
                settings.Lyrics.Mode == LyricsMode.LocalLrc ? LyricsProviderKind.LocalLrc : settings.Lyrics.Provider,
                LyricsDiagnosticStage.Source, result.Status switch
                {
                    LyricsQueryStatus.Found => LyricsDiagnosticOutcome.Found,
                    LyricsQueryStatus.NotFound => LyricsDiagnosticOutcome.NoMatch,
                    LyricsQueryStatus.Disabled => LyricsDiagnosticOutcome.Disabled,
                    _ => LyricsDiagnosticOutcome.TransportFailure,
                },
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, result.Document.Lines.Count,
                result.Document.Lines.Count(line => !string.IsNullOrWhiteSpace(line.Secondary)),
                TranslationLookupIncomplete: result.TranslationLookupIncomplete));
            await _dispatcher.EnqueueAsync(() =>
            {
                if (IsLyricsRequestCurrent(session, settings, generation, token))
                {
                    _document = result.Document;
                    _sourceDocument = result.Document;
                    _view.SetLyricsDocument(result.Document);
                    trace.Write("ui-source", LyricsRequestTrace.Describe(result.Document));
                    _view.LyricsStatus = result.Status;
                    RenderFrame();
                }
                return Task.CompletedTask;
            }).ConfigureAwait(false);
            // A bounded provider search may finish without proving that no translation exists.
            // Its incompleteness controls source caching, not the user-enabled AI fallback.
            if (LyricsTranslationPolicy.CanOfferLocalFallback(result) && IsLyricsRequestCurrent(session, settings, generation, token))
            {
                var query = new LyricsQuery(session.TrackTitle, session.Artist, session.AlbumTitle,
                    session.Timeline.Duration, session.LyricsCacheIdentity, session.AlbumArtist);
                var partialApplied = false;
                var progress = new LyricsTranslationProgressContext(
                    () => TimeSpan.FromTicks(Interlocked.Read(ref _lyricPositionTicks)),
                    () => IsLyricsRequestCurrent(session, settings, generation, token),
                    (update, cancellation) => _dispatcher.EnqueueAsync(() =>
                    {
                        // This guard executes after dispatch. A valid callback can be stale
                        // by the time UI work runs (clear, settings, model or song changed).
                        if (update.IsCurrent && !cancellation.IsCancellationRequested &&
                            IsLyricsRequestCurrent(session, settings, generation, token))
                        {
                            partialApplied = true;
                            _document = LyricsTranslationOutput.Reconcile(_sourceDocument, result.Document,
                                update.Document, targetLanguage);
                            _view.SetLyricsDocument(_document);
                            trace.Write("ui-ai-progress", LyricsRequestTrace.Describe(_document));
                            RenderFrame();
                        }
                        return Task.CompletedTask;
                    }));
                var translationCancellation = new MediaWorkCancellation(token);
                var translation = AiLyrics.TranslateForPublicationAsync(query, result.Document, settings.Lyrics,
                    targetLanguage, translationCancellation.Token, progress);
                // The AI lifetime retains native cleanup ownership. A song change must not
                // consume both source-fetch slots while cancelled inference is still draining.
                _ = translationCancellation.CompleteWhenAsync(translation);
                var publication = await translation.WaitAsync(token).ConfigureAwait(false);
                var translated = publication.Document;
                // A later invalid line or timeout discards the ephemeral view as well as the
                // durable result; always restore provider lyrics when progress was displayed.
                if (partialApplied || !ReferenceEquals(translated, result.Document))
                    await _dispatcher.EnqueueAsync(() =>
                    {
                        if (IsLyricsRequestCurrent(session, settings, generation, token))
                        {
                            // Maintenance can start after inference returns but before this
                            // action runs. Restore only the source if its AI fence has retired.
                            _document = publication.IsCurrent
                                ? LyricsTranslationOutput.Reconcile(_sourceDocument, result.Document, translated, targetLanguage)
                                : _sourceDocument;
                            _view.SetLyricsDocument(_document);
                            trace.Write("ui-ai-final", LyricsRequestTrace.Describe(_document));
                            RenderFrame();
                        }
                        return Task.CompletedTask;
                    }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { trace.Write("cancelled", new { generation }); Interlocked.Exchange(ref previewClosed, 1); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Interlocked.Exchange(ref previewClosed, 1);
            _logger.LogDebug("Lyrics unavailable ({Category}).", exception.GetType().Name);
            try
            {
                await _dispatcher.EnqueueAsync(() =>
                {
                    if (IsLyricsRequestCurrent(session, settings, generation, token))
                    {
                        // Translation/runtime failures must not erase a successful source
                        // result or mislabel that provider fetch as a network/load failure.
                        _document = sourceResult is null ? LyricsDocument.Empty : _sourceDocument;
                        _view.SetLyricsDocument(_document);
                        _view.LyricsStatus = sourceResult?.Status ?? LyricsQueryStatus.Failed;
                        RenderFrame();
                    }
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
            }
            catch (Exception dispatchException) when (dispatchException is not OutOfMemoryException)
            {
                _logger.LogDebug("Lyrics failure state could not reach the UI ({Category}).", dispatchException.GetType().Name);
            }
        }
    }

    private bool IsLyricsRequestCurrent(MediaSessionSnapshot session, AppSettings settings, long generation,
        CancellationToken token) => !_disposed && !token.IsCancellationRequested &&
        generation == Interlocked.Read(ref _generation) && session.IsSameTrack(Volatile.Read(ref _latest)) &&
        !LyricsReloadPolicy.RequiresReload(settings, Volatile.Read(ref _settings));

    internal static bool ShouldRetryLyricsWithImprovedEvidence(
        MediaSessionSnapshot? previous,
        MediaSessionSnapshot current,
        bool alreadyFound) =>
        !alreadyFound && previous is not null && previous.IsSameTrack(current) &&
        previous.Timeline.Duration <= TimeSpan.Zero && current.Timeline.Duration > TimeSpan.Zero;
    private async Task LoadArtworkAsync(MediaSessionSnapshot session, long generation, CancellationToken token)
    {
        try
        {
            var image = await _artwork.DecodeAsync(session.Artwork, token).ConfigureAwait(false);
            await _dispatcher.EnqueueAsync(() =>
            {
                if (!token.IsCancellationRequested && generation == Interlocked.Read(ref _artworkGeneration) && !_disposed) _view.Artwork = image;
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { _logger.LogDebug("Artwork unavailable ({Category}).", exception.GetType().Name); }
    }
    private void OnFrame(DispatcherQueueTimer sender, object args) => RenderFrame();
    private void OnVisualPreferencesChanged(object? sender, EventArgs args) =>
        _view.IsReducedMotion = _visualPreferences.IsReducedMotion(_view.Settings.OverlayMotion);
    private void RenderFrame()
    {
        _view.Position = _clock.Position;
        // SMTC timelines may have a non-zero media start. Lyric timestamps are track-relative,
        // so keep the clock absolute for seeking/display but feed the lyric engine a relative
        // position.
        var lyricPosition = _view.Position - _view.Session.Timeline.Start;
        Interlocked.Exchange(ref _lyricPositionTicks, (lyricPosition +
            TimeSpan.FromMilliseconds(Math.Clamp(_view.Settings.Lyrics.DelayMilliseconds, -30_000, 30_000))).Ticks);
        _view.Lyrics = _timeline.GetFrame(_document, lyricPosition, _view.Settings.Lyrics.DelayMilliseconds);
        var observation = Volatile.Read(ref _spectrum);
        _view.Spectrum = SpectrumFreshnessPolicy.Apply(observation.Frame, Stopwatch.GetElapsedTime(observation.Timestamp));
    }
    private void OnExperienceChanged(object? sender, IslandExperienceSnapshot snapshot)
    {
        UpdateFrameTimer();
    }
    private void UpdateFrameTimer()
    {
        if (_view.IsPlaying && (_view.IsPresentationVisible || _view.IsIslandGlowActive)) _frames.Start();
        else _frames.Stop();
    }
    private void OnPresentationChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is not (nameof(MediaViewModel.IsPresentationVisible) or nameof(MediaViewModel.IsIslandGlowActive))) return;
        UpdateFrameTimer(); _changes.Writer.TryWrite(true);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_runtimeGate)
        {
            if (_disposed) return;
            _disposed = true;
            _runtimeStop?.Request();
        }
        _audioRecovery.Dispose();
        _frames.Stop();
        _frames.Tick -= OnFrame; _experience.Changed -= OnExperienceChanged;
        _visualPreferences.Changed -= OnVisualPreferencesChanged;
        _view.PropertyChanged -= OnPresentationChanged;
        AiLyrics.ModelDownloaded -= OnModelDownloaded;
        QqMusicLogin.Session.CredentialsChanged -= OnQqCredentialsChanged;
        _main.PropertyChanged -= OnSettingsChanged; _media.Changed -= OnMediaChanged; _audio.Changed -= OnSpectrumChanged;
        var restartRetirement = _restart.StopAsync();
        var stopCallbacks = _stop.CancelAsync();
        _changes.Writer.TryComplete();
        var cleanup = CompleteShutdownAsync(restartRetirement, stopCallbacks);
        _ = MediaSoftRestartOperation.ObserveAsync(cleanup);
        try { await cleanup.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            // Keep ownership and resources in cleanup until actual work/callback completion.
            // Closing the UI must not hang on a broken media publisher or release its buffers early.
            _logger.LogWarning("Music shutdown is still retiring native work in the background.");
        }
    }

    private async Task CompleteShutdownAsync(Task restartRetirement, Task stopCallbacks)
    {
        await restartRetirement.ConfigureAwait(false);
        await MediaSoftRestartOperation.ObserveAsync(_worker).ConfigureAwait(false);
        Task[] coordinators;
        lock (_runtimeGate) coordinators = _runtimeRetirements.ToArray();
        await Task.WhenAll(coordinators).ConfigureAwait(false);
        await Task.WhenAll(_lyricsWork.DrainAsync(CancellationToken.None), _artworkWork.DrainAsync(CancellationToken.None)).ConfigureAwait(false);
        await MediaSoftRestartOperation.ObserveAsync(_media.SetEnabledAsync(false)).ConfigureAwait(false);
        await MediaSoftRestartOperation.ObserveAsync(SetAudioSourceAsync(null, false, CancellationToken.None)).ConfigureAwait(false);
        await MediaSoftRestartOperation.ObserveAsync(stopCallbacks).ConfigureAwait(false);
        _stop.Dispose(); _http.Dispose();
    }
}
