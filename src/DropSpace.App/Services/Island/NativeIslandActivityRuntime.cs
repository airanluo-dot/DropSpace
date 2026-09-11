using DropSpace.App.Services.Audio;
using DropSpace.App.Services.Media;
using DropSpace.App.Services.Notifications;
using DropSpace.App.Services.Volume;
using DropSpace.App.Services.Widgets;
using DropSpace.Core.Island;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Media;
using DropSpace.Core.Models;
using DropSpace.Core.SystemActivities;
using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services.Island;

public sealed class NativeIslandActivityRuntime : IAsyncDisposable
{
    private const string MediaSourceId = "media";
    private readonly IIslandActivityRouter _router;
    private readonly WindowsMediaSessionService _media;
    private readonly WindowsSpectrumService _spectrum;
    private readonly WindowsNotificationActivityService _notifications;
    private readonly WindowsVolumeActivityService _volume;
    private readonly NativeWidgetActivityService _widgets;
    private readonly LyricsService _lyrics;
    private readonly ILogger<NativeIslandActivityRuntime> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IslandActivitySettings _mediaSettings = new();
    private LyricsSettings _lyricsSettings = new();
    private LyricsDocument? _lyricsDocument;
    private string? _lyricsTrackKey;
    private CancellationTokenSource? _lyricsCancellation;
    private bool _disposed;
    private Guid _mediaActivityId;

    public NativeIslandActivityRuntime(
        IIslandActivityRouter router,
        WindowsMediaSessionService media,
        WindowsSpectrumService spectrum,
        WindowsNotificationActivityService notifications,
        WindowsVolumeActivityService volume,
        NativeWidgetActivityService widgets,
        LyricsService lyrics,
        ILogger<NativeIslandActivityRuntime> logger)
    {
        _router = router;
        _media = media;
        _spectrum = spectrum;
        _notifications = notifications;
        _volume = volume;
        _widgets = widgets;
        _lyrics = lyrics;
        _logger = logger;
        _media.Changed += OnMediaChanged;
        _notifications.NotificationReceived += OnNotificationReceived;
        _volume.Changed += OnVolumeChanged;
    }

    public async Task ApplySettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _mediaSettings = settings.IslandActivity;
            _lyricsSettings = settings.Lyrics;
            if (!_lyricsSettings.Enabled)
            {
                CancelLyricsLookup();
                _lyricsDocument = null;
                _lyricsTrackKey = null;
            }
            await _media.SetEnabledAsync(settings.IslandActivity.EnableMediaActivity, cancellationToken);
            await _spectrum.SetEnabledAsync(settings.IslandActivity.EnableMediaActivity && settings.IslandActivity.ShowSpectrum, cancellationToken);
            await _notifications.SetEnabledAsync(settings.SystemActivities.ShowWindowsNotifications, cancellationToken);
            await _volume.SetEnabledAsync(settings.SystemActivities.ShowVolumeChanges, cancellationToken);
            await _widgets.ApplySettingsAsync(settings.Widgets, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public Task<string> RequestNotificationAccessAsync(CancellationToken cancellationToken = default) =>
        _notifications.RequestAccessAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _media.Changed -= OnMediaChanged;
        _notifications.NotificationReceived -= OnNotificationReceived;
        _volume.Changed -= OnVolumeChanged;
        CancelLyricsLookup();
        await _widgets.DisposeAsync();
        await _spectrum.DisposeAsync();
        await _media.DisposeAsync();
        await _notifications.DisposeAsync();
        await _volume.DisposeAsync();
        _gate.Dispose();
    }

    private void OnMediaChanged(object? sender, MediaSessionSnapshot snapshot)
    {
        _router.RemoveSource(MediaSourceId);
        _mediaActivityId = Guid.Empty;
        if (!_mediaSettings.EnableMediaActivity || !snapshot.IsActive) return;
        var trackKey = string.Join("|", snapshot.TrackId, snapshot.TrackTitle, snapshot.Artist, snapshot.AlbumTitle);
        if (_lyricsSettings.Enabled && !string.IsNullOrWhiteSpace(snapshot.TrackTitle) && !string.Equals(_lyricsTrackKey, trackKey, StringComparison.Ordinal))
        {
            CancelLyricsLookup();
            _lyricsTrackKey = trackKey;
            _lyricsDocument = null;
            _lyricsCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            _ = LoadLyricsAsync(snapshot, trackKey, _lyricsCancellation.Token);
        }

        PublishMedia(snapshot);
    }

    private void PublishMedia(MediaSessionSnapshot snapshot)
    {
        var currentLine = _lyricsSettings.Enabled
            ? _lyricsDocument?.FindCurrent(snapshot.Timeline.Position, TimeSpan.FromMilliseconds(_lyricsSettings.DelayMilliseconds))
            : null;
        var subtitle = _mediaSettings.ShowLyricsInCompact && currentLine is not null
            ? currentLine.PrimaryText
            : _mediaSettings.ShowArtist ? snapshot.Artist : string.Empty;
        if (_mediaSettings.ShowProgress && snapshot.Timeline.Duration > TimeSpan.Zero)
        {
            subtitle = string.IsNullOrWhiteSpace(subtitle)
                ? FormatTime(snapshot.Timeline.Position, snapshot.Timeline.Duration)
                : $"{subtitle} · {FormatTime(snapshot.Timeline.Position, snapshot.Timeline.Duration)}";
        }

        var title = _mediaSettings.ShowTitle ? snapshot.TrackTitle : "Media";
        var expandedSubtitle = currentLine is null
            ? string.Join(" · ", new[] { snapshot.Artist, snapshot.AlbumTitle }.Where(value => !string.IsNullOrWhiteSpace(value)))
            : string.Join(" · ", new[] { snapshot.Artist, currentLine.PrimaryText }.Where(value => !string.IsNullOrWhiteSpace(value)));
        _mediaActivityId = _router.Publish(new IslandActivity(
            Guid.Empty,
            IslandActivityKind.Media,
            IslandActivityPriority.Media,
            IslandActivityPresentation.Both,
            MediaSourceId,
            title,
            subtitle,
            snapshot.TrackTitle,
            expandedSubtitle,
            new(DateTimeOffset.UtcNow),
            true));
    }

    private async Task LoadLyricsAsync(MediaSessionSnapshot snapshot, string trackKey, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _lyrics.QueryAsync(
                new LyricsQuery(snapshot.TrackTitle, snapshot.Artist, snapshot.AlbumTitle, snapshot.Timeline.Duration),
                _lyricsSettings.Mode,
                _lyricsSettings.Provider,
                _lyricsSettings.LocalLrcDirectory,
                cancellationToken);
            if (!result.Success || cancellationToken.IsCancellationRequested || !string.Equals(_lyricsTrackKey, trackKey, StringComparison.Ordinal)) return;
            _lyricsDocument = result.Document;
            PublishMedia(snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Timed lyrics lookup failed safely.");
        }
    }

    private void CancelLyricsLookup()
    {
        _lyricsCancellation?.Cancel();
        _lyricsCancellation?.Dispose();
        _lyricsCancellation = null;
    }

    private void OnNotificationReceived(object? sender, SystemNotificationSnapshot notification)
    {
        var id = _router.Publish(new IslandActivity(
            Guid.Empty,
            IslandActivityKind.Notification,
            IslandActivityPriority.Notification,
            IslandActivityPresentation.Both,
            $"notification:{notification.Id}",
            notification.SourceDisplayName,
            notification.Title,
            notification.Title,
            notification.Body,
            new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(5))));
        _ = RemoveAfterAsync(id, TimeSpan.FromSeconds(5));
    }

    private void OnVolumeChanged(object? sender, VolumeActivitySnapshot volume)
    {
        var id = _router.Publish(new IslandActivity(
            Guid.Empty,
            IslandActivityKind.Volume,
            IslandActivityPriority.Volume,
            IslandActivityPresentation.Both,
            "volume",
            volume.IsMuted ? "Volume" : $"Volume {volume.Percent}%",
            volume.IsMuted ? "Muted" : $"{volume.Percent}%",
            "System volume",
            volume.IsMuted ? "Windows volume is muted" : $"Windows volume is {volume.Percent}%",
            new(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(2))));
        _ = RemoveAfterAsync(id, TimeSpan.FromSeconds(2));
    }

    private async Task RemoveAfterAsync(Guid id, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay);
            _router.Remove(id);
        }
        catch (Exception exception) when (exception is ObjectDisposedException or TaskCanceledException)
        {
            _logger.LogDebug(exception, "Transient activity cleanup ended during shutdown.");
        }
    }

    private static string FormatTime(TimeSpan position, TimeSpan duration) => $"{(int)position.TotalMinutes}:{position.Seconds:00} / {(int)duration.TotalMinutes}:{duration.Seconds:00}";
}
