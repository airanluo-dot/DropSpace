using System.ComponentModel;
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
    public AiLyricsService AiLyrics { get; }
    private readonly LyricsTimelineEngine _timeline = new();
    private readonly MediaPlaybackClock _clock = new();
    private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _lyricsMaintenance = new(1, 1);
    private readonly DispatcherQueueTimer _frames, _expiry;
    private readonly MediaSoftRestartOperation _restart = new(TimeSpan.FromSeconds(20));
    private readonly RetirableMediaWork _lyricsWork = new(), _artworkWork = new();
    private Task _worker = Task.CompletedTask, _audioTransition = Task.CompletedTask;
    private readonly object _runtimeGate = new();
    private readonly List<Task> _runtimeRetirements = [];
    private MediaWorkCancellation? _runtimeStop;
    private AppSettings _settings = new();
    private MediaSessionSnapshot _latest = MediaSessionSnapshot.Empty;
    private SpectrumFrame _spectrum = SpectrumFrame.Empty;
    private LyricsDocument _document = LyricsDocument.Empty;
    private long _generation, _artworkGeneration;
    private long _reloadRequest;
    private bool _initialized, _disposed;

    public MediaExperienceService(MainViewModel main, MediaViewModel view, WindowsMediaSessionService media,
        WindowsProcessLoopbackService audio, MediaProcessResolver processes, MediaArtworkService artwork,
        IslandExperienceCoordinator experience, DispatcherQueue dispatcher, ILogger<MediaExperienceService> logger,
        SystemVisualPreferenceService visualPreferences, AiLyricsService aiLyrics, LyricsCache lyricsCache)
    {
        _main = main; _view = view; _media = media; _audio = audio; _processes = processes; _artwork = artwork;
        _experience = experience; _dispatcher = dispatcher; _logger = logger;
        _visualPreferences = visualPreferences; AiLyrics = aiLyrics; _lyricsCache = lyricsCache;
        AiLyrics.ModelDownloaded += OnModelDownloaded;
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        _lyrics = new LyricsService(new LyricsProviderRegistry(new LyricsHttpClient(_http), () => Volatile.Read(ref _settings).Lyrics.LocalLrcDirectory), lyricsCache);
        _frames = dispatcher.CreateTimer(); _frames.Interval = TimeSpan.FromMilliseconds(33); _frames.IsRepeating = true;
        _frames.Tick += OnFrame;
        _expiry = dispatcher.CreateTimer(); _expiry.IsRepeating = false; _expiry.Tick += OnExpiry;
        _experience.Changed += OnExperienceChanged;
        _visualPreferences.Changed += OnVisualPreferencesChanged;
        _view.PropertyChanged += OnPresentationChanged;
    }

    public Task InitializeAsync(AppSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized) return Task.CompletedTask;
        _initialized = true; _settings = settings;
        _main.PropertyChanged += OnSettingsChanged;
        _media.Changed += OnMediaChanged; _audio.Changed += OnSpectrumChanged;
        StartRuntime();
        _changes.Writer.TryWrite(true);
        return Task.CompletedTask;
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
            Volatile.Write(ref _spectrum, SpectrumFrame.Empty);
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
        Interlocked.Increment(ref _reloadRequest);
        _changes.Writer.TryWrite(true);
    }
    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(MainViewModel.Settings)) return;
        Volatile.Write(ref _settings, _main.Settings); _changes.Writer.TryWrite(true);
    }
    private void OnMediaChanged(object? sender, MediaSessionSnapshot snapshot)
    { Volatile.Write(ref _latest, snapshot); _changes.Writer.TryWrite(true); }
    private void OnSpectrumChanged(object? sender, SpectrumFrame frame) => Volatile.Write(ref _spectrum, frame);

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
                        try { await _lyricsCache.SetMaximumBytesAsync(settings.Lyrics.CacheMaximumBytes, token).WaitAsync(token).ConfigureAwait(false); }
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
                            _view.SetLyricsDocument(LyricsDocument.Empty);
                            _view.Lyrics = LyricsHighlightFrame.Empty;
                        }
                        if (trackChanged) _view.Artwork = null;
                        _view.Settings = settings; _view.Session = session; _view.PositionEstimated = _clock.IsEstimated;
                        if (resetLyrics)
                            _view.LyricsStatus = settings.Lyrics.Enabled && !string.IsNullOrWhiteSpace(session.TrackTitle)
                                ? LyricsQueryStatus.Loading : LyricsQueryStatus.Disabled;
                        _view.IsReducedMotion = _visualPreferences.IsReducedMotion(settings.OverlayMotion);
                        _experience.UpdateMedia(playing, settings.IslandActivity.EnableMediaActivity, settings.IslandAppearance.HideDelayMilliseconds, settings.IslandAppearance.AutoHide);
                        RenderFrame(); UpdateFrameTimer();
                        return Task.CompletedTask;
                    }).WaitAsync(token).ConfigureAwait(false);
                    if (reload)
                    {
                        // Lyrics and cache maintenance are optional to the metadata path.
                        // Never wait here for a cancelled provider or a clear-cache drain.
                        lyricsPending = !_lyricsWork.TryStartWhileIdle(_lyricsMaintenance,
                            workToken => LoadLyricsAsync(session, settings, generation, workToken), token,
                            () => _changes.Writer.TryWrite(true));
                        if (lyricsPending)
                            await _dispatcher.EnqueueAsync(() =>
                            {
                                if (!_disposed && !token.IsCancellationRequested && generation == Interlocked.Read(ref _generation))
                                    _view.LyricsStatus = LyricsQueryStatus.Failed;
                                return Task.CompletedTask;
                            }).WaitAsync(token).ConfigureAwait(false);
                    }
                    if (reloadArtwork)
                        artworkPending = !_artworkWork.TryStart(
                            workToken => LoadArtworkAsync(session, artworkGeneration, workToken), token,
                            () => _changes.Writer.TryWrite(true));
                    var sourceKey = playing && ((settings.IslandActivity.ShowSpectrum && _view.IsPresentationVisible) || _view.IsIslandGlowActive) ? session.SourceAppUserModelId : string.Empty;
                    if (audioKey != sourceKey || trackChanged)
                    {
                        var resolved = true;
                        uint? process = null;
                        if (sourceKey.Length > 0)
                        {
                            try { process = await _processes.ResolveAudioAsync(sourceKey, token).WaitAsync(token).ConfigureAwait(false); }
                            catch (Exception exception) when (exception is not OperationCanceledException)
                            { resolved = false; _logger.LogDebug("Player process resolution unavailable ({Category}).", exception.GetType().Name); }
                        }
                        try { await SetAudioSourceAsync(process, resolved && sourceKey.Length > 0, token).ConfigureAwait(false); }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        { resolved = false; _logger.LogDebug("Player capture unavailable ({Category}).", exception.GetType().Name); }
                        audioKey = resolved ? sourceKey : null;
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

    private async Task LoadLyricsAsync(MediaSessionSnapshot session, AppSettings settings, long generation, CancellationToken token)
    {
        try
        {
            var result = !string.IsNullOrWhiteSpace(session.TrackTitle)
                ? await _lyrics.QueryDetailedAsync(new(session.TrackTitle, session.Artist, session.AlbumTitle,
                    session.Timeline.Duration, session.LyricsCacheIdentity, session.AlbumArtist), settings.Lyrics, token).ConfigureAwait(false)
                : new(LyricsDocument.Empty, LyricsQueryStatus.Disabled);
            await _dispatcher.EnqueueAsync(() =>
            {
                if (!token.IsCancellationRequested && generation == Interlocked.Read(ref _generation) && !_disposed)
                {
                    _document = result.Document;
                    _view.SetLyricsDocument(result.Document);
                    _view.LyricsStatus = result.Status;
                    RenderFrame();
                }
                return Task.CompletedTask;
            }).ConfigureAwait(false);
            if (!token.IsCancellationRequested && generation == Interlocked.Read(ref _generation))
            {
                var targetLanguage = LyricsTranslationPolicy.ResolveTarget(settings.Language, [System.Globalization.CultureInfo.CurrentUICulture.Name]);
                var query = new LyricsQuery(session.TrackTitle, session.Artist, session.AlbumTitle,
                    session.Timeline.Duration, session.LyricsCacheIdentity, session.AlbumArtist);
                var translated = await AiLyrics.TranslateIfAvailableAsync(query, result.Document, settings.Lyrics, targetLanguage, token).ConfigureAwait(false);
                if (!ReferenceEquals(translated, result.Document))
                    await _dispatcher.EnqueueAsync(() =>
                    {
                        if (!token.IsCancellationRequested && generation == Interlocked.Read(ref _generation) && !_disposed)
                        {
                            _document = translated;
                            _view.SetLyricsDocument(translated);
                            RenderFrame();
                        }
                        return Task.CompletedTask;
                    }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogDebug("Lyrics unavailable ({Category}).", exception.GetType().Name);
            try
            {
                await _dispatcher.EnqueueAsync(() =>
                {
                    if (!token.IsCancellationRequested && generation == Interlocked.Read(ref _generation) && !_disposed)
                    {
                        _document = LyricsDocument.Empty;
                        _view.SetLyricsDocument(LyricsDocument.Empty);
                        _view.LyricsStatus = LyricsQueryStatus.Failed;
                        _view.Lyrics = LyricsHighlightFrame.Empty;
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
        _view.Lyrics = _timeline.GetFrame(_document, lyricPosition, _view.Settings.Lyrics.DelayMilliseconds);
        _view.Spectrum = Volatile.Read(ref _spectrum);
    }
    private void OnExperienceChanged(object? sender, IslandExperienceSnapshot snapshot)
    {
        _expiry.Stop();
        if (snapshot.NextDeadline is { } deadline)
        {
            _expiry.Interval = TimeSpan.FromMilliseconds(Math.Max(1, (deadline - DateTimeOffset.UtcNow).TotalMilliseconds));
            _expiry.Start();
        }
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
    private void OnExpiry(DispatcherQueueTimer sender, object args) => _experience.Reconcile();

    public async ValueTask DisposeAsync()
    {
        lock (_runtimeGate)
        {
            if (_disposed) return;
            _disposed = true;
            _runtimeStop?.Request();
        }
        _frames.Stop(); _expiry.Stop();
        _frames.Tick -= OnFrame; _expiry.Tick -= OnExpiry; _experience.Changed -= OnExperienceChanged;
        _visualPreferences.Changed -= OnVisualPreferencesChanged;
        _view.PropertyChanged -= OnPresentationChanged;
        AiLyrics.ModelDownloaded -= OnModelDownloaded;
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
