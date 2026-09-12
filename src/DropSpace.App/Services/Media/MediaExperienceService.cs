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
    private readonly LyricsTimelineEngine _timeline = new();
    private readonly MediaPlaybackClock _clock = new();
    private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _stop = new();
    private readonly DispatcherQueueTimer _frames, _expiry;
    private Task _worker = Task.CompletedTask, _lyricsJob = Task.CompletedTask, _artworkJob = Task.CompletedTask;
    private CancellationTokenSource? _trackStop;
    private AppSettings _settings = new();
    private MediaSessionSnapshot _latest = MediaSessionSnapshot.Empty;
    private SpectrumFrame _spectrum = SpectrumFrame.Empty;
    private LyricsDocument _document = LyricsDocument.Empty;
    private long _generation;
    private long _reloadRequest;
    private bool _initialized, _disposed;

    public MediaExperienceService(MainViewModel main, MediaViewModel view, WindowsMediaSessionService media,
        WindowsProcessLoopbackService audio, MediaProcessResolver processes, MediaArtworkService artwork,
        IslandExperienceCoordinator experience, DispatcherQueue dispatcher, ILogger<MediaExperienceService> logger,
        SystemVisualPreferenceService visualPreferences)
    {
        _main = main; _view = view; _media = media; _audio = audio; _processes = processes; _artwork = artwork;
        _experience = experience; _dispatcher = dispatcher; _logger = logger;
        _visualPreferences = visualPreferences;
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        _lyrics = new LyricsService(new LyricsProviderRegistry(new LyricsHttpClient(_http), () => Volatile.Read(ref _settings).Lyrics.LocalLrcDirectory));
        _frames = dispatcher.CreateTimer(); _frames.Interval = TimeSpan.FromMilliseconds(33); _frames.IsRepeating = true;
        _frames.Tick += OnFrame;
        _expiry = dispatcher.CreateTimer(); _expiry.IsRepeating = false; _expiry.Tick += OnExpiry;
        _experience.Changed += OnExperienceChanged;
        _visualPreferences.Changed += OnVisualPreferencesChanged;
    }

    public Task InitializeAsync(AppSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized) return Task.CompletedTask;
        _initialized = true; _settings = settings;
        _main.PropertyChanged += OnSettingsChanged;
        _media.Changed += OnMediaChanged; _audio.Changed += OnSpectrumChanged;
        _worker = Task.Run(() => RunAsync(_stop.Token));
        _changes.Writer.TryWrite(true);
        return Task.CompletedTask;
    }
    public void ClearLyricsCache()
    {
        _lyrics.ClearCache(); Interlocked.Increment(ref _generation); Interlocked.Increment(ref _reloadRequest);
        _document = LyricsDocument.Empty; _view.Lyrics = LyricsHighlightFrame.Empty; _changes.Writer.TryWrite(true);
    }
    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(MainViewModel.Settings)) return;
        Volatile.Write(ref _settings, _main.Settings); _changes.Writer.TryWrite(true);
    }
    private void OnMediaChanged(object? sender, MediaSessionSnapshot snapshot)
    { Volatile.Write(ref _latest, snapshot); _changes.Writer.TryWrite(true); }
    private void OnSpectrumChanged(object? sender, SpectrumFrame frame) => Volatile.Write(ref _spectrum, frame);

    private async Task RunAsync(CancellationToken token)
    {
        AppSettings? previousSettings = null;
        MediaSessionSnapshot? previousMedia = null;
        string? audioKey = null;
        long previousReloadRequest = -1;
        try
        {
            await foreach (var _ in _changes.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                try
                {
                    var settings = Volatile.Read(ref _settings);
                    var reloadRequest = Interlocked.Read(ref _reloadRequest);
                    if (previousSettings?.IslandActivity != settings.IslandActivity)
                    {
                        _media.SetAllowedSources(settings.IslandActivity.AllowedMediaSourceAppIds);
                        await _media.SetEnabledAsync(settings.IslandActivity.EnableMediaActivity, token).ConfigureAwait(false);
                    }
                    var session = Volatile.Read(ref _latest);
                    var playing = settings.IslandActivity.EnableMediaActivity && session.PlaybackState == MediaPlaybackState.Playing && !string.IsNullOrWhiteSpace(session.TrackTitle);
                    var trackChanged = previousMedia?.SourceAppUserModelId != session.SourceAppUserModelId || previousMedia.TrackTitle != session.TrackTitle ||
                        previousMedia.Artist != session.Artist || previousMedia.AlbumTitle != session.AlbumTitle;
                    var reload = trackChanged || previousSettings?.Lyrics != settings.Lyrics || previousSettings?.IslandActivity.EnableMediaActivity != settings.IslandActivity.EnableMediaActivity ||
                        !ReferenceEquals(previousMedia?.Artwork, session.Artwork) || previousReloadRequest != reloadRequest;
                    await _dispatcher.EnqueueAsync(() =>
                    {
                        if (_disposed) return Task.CompletedTask;
                        _clock.Update(session);
                        _view.Settings = settings; _view.Session = session; _view.PositionEstimated = _clock.IsEstimated;
                        _view.IsReducedMotion = _visualPreferences.IsReducedMotion(settings.OverlayMotion);
                        if (trackChanged) { _view.Artwork = null; _document = LyricsDocument.Empty; }
                        _experience.UpdateMedia(playing, settings.IslandActivity.EnableMediaActivity, settings.IslandAppearance.HideDelayMilliseconds);
                        RenderFrame(); UpdateFrameTimer();
                        return Task.CompletedTask;
                    }).ConfigureAwait(false);
                    if (reload)
                    {
                        _trackStop?.Cancel();
                        await Task.WhenAll(_lyricsJob, _artworkJob).ConfigureAwait(false);
                        _trackStop?.Dispose(); _trackStop = CancellationTokenSource.CreateLinkedTokenSource(token);
                        var generation = Interlocked.Increment(ref _generation);
                        _lyricsJob = LoadLyricsAsync(session, settings, generation, _trackStop.Token);
                        _artworkJob = LoadArtworkAsync(session, generation, _trackStop.Token);
                    }
                    var sourceKey = playing && settings.IslandActivity.ShowSpectrum ? session.SourceAppUserModelId : string.Empty;
                    if (audioKey != sourceKey)
                    {
                        audioKey = sourceKey;
                        var process = sourceKey.Length == 0 ? null : await _processes.ResolveAsync(sourceKey, token).ConfigureAwait(false);
                        try { await _audio.SetSourceAsync(process, playing && settings.IslandActivity.ShowSpectrum, token).ConfigureAwait(false); }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        { _logger.LogDebug("Player capture unavailable ({Category}).", exception.GetType().Name); }
                    }
                    previousSettings = settings; previousMedia = session;
                    previousReloadRequest = reloadRequest;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                { _logger.LogWarning("Media presentation update failed ({Category}).", exception.GetType().Name); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task LoadLyricsAsync(MediaSessionSnapshot session, AppSettings settings, long generation, CancellationToken token)
    {
        try
        {
            var document = settings.IslandActivity.EnableMediaActivity
                ? await _lyrics.QueryAsync(new(session.TrackTitle, session.Artist, session.AlbumTitle, session.Timeline.Duration), settings.Lyrics, token).ConfigureAwait(false)
                : LyricsDocument.Empty;
            await _dispatcher.EnqueueAsync(() =>
            {
                if (!token.IsCancellationRequested && generation == Interlocked.Read(ref _generation) && !_disposed) { _document = document; RenderFrame(); }
                return Task.CompletedTask;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) { _logger.LogDebug("Lyrics unavailable ({Category}).", exception.GetType().Name); }
    }
    private async Task LoadArtworkAsync(MediaSessionSnapshot session, long generation, CancellationToken token)
    {
        try
        {
            var image = await _artwork.DecodeAsync(session.Artwork, token).ConfigureAwait(false);
            await _dispatcher.EnqueueAsync(() =>
            {
                if (!token.IsCancellationRequested && generation == Interlocked.Read(ref _generation) && !_disposed) _view.Artwork = image;
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
        _view.Lyrics = _timeline.GetFrame(_document, _view.Position, _view.Settings.Lyrics.DelayMilliseconds);
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
        if (_view.IsPlaying && _experience.Current.State != DropSpace.Core.Overlay.OverlayState.Hidden) _frames.Start();
        else _frames.Stop();
    }
    private void OnExpiry(DispatcherQueueTimer sender, object args) => _experience.Reconcile();

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true; _frames.Stop(); _expiry.Stop();
        _frames.Tick -= OnFrame; _expiry.Tick -= OnExpiry; _experience.Changed -= OnExperienceChanged;
        _visualPreferences.Changed -= OnVisualPreferencesChanged;
        _main.PropertyChanged -= OnSettingsChanged; _media.Changed -= OnMediaChanged; _audio.Changed -= OnSpectrumChanged;
        _stop.Cancel(); _changes.Writer.TryComplete();
        await _worker.ConfigureAwait(false); await Task.WhenAll(_lyricsJob, _artworkJob).ConfigureAwait(false);
        await _media.SetEnabledAsync(false).ConfigureAwait(false); await _audio.SetSourceAsync(null, false).ConfigureAwait(false);
        _trackStop?.Dispose(); _stop.Dispose(); _http.Dispose(); _lyrics.ClearCache();
    }
}
