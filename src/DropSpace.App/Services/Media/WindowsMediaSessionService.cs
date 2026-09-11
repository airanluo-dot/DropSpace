using System.Runtime.InteropServices.WindowsRuntime;
using DropSpace.Core.Media;
using Microsoft.Extensions.Logging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DropSpace.App.Services.Media;

public sealed class WindowsMediaSessionService : IMediaSessionService
{
    private const int MaximumArtworkBytes = 4 * 1024 * 1024;
    private readonly ILogger<WindowsMediaSessionService> _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private bool _enabled;
    private bool _disposed;
    private MediaSessionSnapshot _current = MediaSessionSnapshot.Empty;

    public WindowsMediaSessionService(ILogger<WindowsMediaSessionService> logger) => _logger = logger;

    public event EventHandler<MediaSessionSnapshot>? Changed;

    public MediaSessionSnapshot Current => _current;

    public bool IsAvailable { get; private set; }

    public string AvailabilityReason { get; private set; } = "Media activity has not been initialized.";

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_enabled == enabled && (!enabled || _manager is not null)) return;
        _enabled = enabled;
        if (!enabled)
        {
            DetachSession();
            if (_manager is not null) _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            _manager = null;
            IsAvailable = false;
            AvailabilityReason = "Media activity is disabled.";
            Publish(MediaSessionSnapshot.Empty);
            return;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += OnCurrentSessionChanged;
            IsAvailable = true;
            AvailabilityReason = "Windows System Media Transport Controls are available.";
            await RefreshAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or COMException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "SMTC media activity is unavailable; the rest of DropSpace remains available.");
            IsAvailable = false;
            AvailabilityReason = $"Media activity is unavailable: {exception.GetType().Name}.";
            Publish(MediaSessionSnapshot.Empty);
        }
    }

    public async Task PlayPauseAsync(CancellationToken cancellationToken = default)
    {
        if (_session is null) return;
        if (_current.PlaybackState == MediaPlaybackState.Playing) await _session.TryPauseAsync();
        else await _session.TryPlayAsync();
        await RefreshAsync(cancellationToken);
    }

    public async Task SkipNextAsync(CancellationToken cancellationToken = default)
    {
        if (_session is null) return;
        await _session.TrySkipNextAsync();
        await RefreshAsync(cancellationToken);
    }

    public async Task SkipPreviousAsync(CancellationToken cancellationToken = default)
    {
        if (_session is null) return;
        await _session.TrySkipPreviousAsync();
        await RefreshAsync(cancellationToken);
    }

    public async Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        if (_session is null || !_current.CanSeek) return;
        var duration = _current.Timeline.Duration;
        if (duration > TimeSpan.Zero) position = TimeSpan.FromTicks(Math.Clamp(position.Ticks, 0, duration.Ticks));
        await _session.TryChangePlaybackPositionAsync(position.Ticks);
        await RefreshAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await SetEnabledAsync(false);
        _disposed = true;
        _refreshGate.Dispose();
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) =>
        _ = RefreshSafelyAsync();

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) =>
        _ = RefreshSafelyAsync();

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) =>
        _ = RefreshSafelyAsync();

    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) =>
        _ = RefreshSafelyAsync();

    private async Task RefreshSafelyAsync()
    {
        try { await RefreshAsync(CancellationToken.None); }
        catch (Exception exception) when (exception is COMException or InvalidOperationException or UnauthorizedAccessException)
        { _logger.LogDebug(exception, "SMTC refresh failed safely."); }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        if (!_enabled || _manager is null) return;
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            var session = _manager.GetCurrentSession();
            if (!ReferenceEquals(session, _session))
            {
                DetachSession();
                _session = session;
                if (_session is not null)
                {
                    _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
                    _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
                    _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
                }
            }

            if (_session is null)
            {
                Publish(MediaSessionSnapshot.Empty);
                return;
            }

            var properties = await _session.TryGetMediaPropertiesAsync();
            var playback = _session.GetPlaybackInfo();
            var timeline = _session.GetTimelineProperties();
            var artwork = await ReadArtworkAsync(properties.Thumbnail, cancellationToken);
            var state = playback.PlaybackStatus switch
            {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaPlaybackState.Playing,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaPlaybackState.Paused,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => MediaPlaybackState.Stopped,
                _ => MediaPlaybackState.Unknown,
            };
            var source = _session.SourceAppUserModelId ?? string.Empty;
            Publish(new MediaSessionSnapshot(
                source,
                source,
                source,
                properties.Title ?? string.Empty,
                properties.Artist ?? string.Empty,
                properties.AlbumTitle ?? string.Empty,
                artwork,
                state,
                playback.Controls.IsPlayEnabled,
                playback.Controls.IsPauseEnabled,
                playback.Controls.IsNextEnabled,
                playback.Controls.IsPreviousEnabled,
                playback.Controls.IsPlaybackPositionEnabled,
                new(timeline.Position, timeline.StartTime, timeline.EndTime, timeline.EndTime > timeline.StartTime ? 1 : 0, timeline.LastUpdatedTime),
                DateTimeOffset.UtcNow));
        }
        finally { _refreshGate.Release(); }
    }

    private void DetachSession()
    {
        if (_session is null) return;
        _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
        _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        _session = null;
    }

    private static async Task<byte[]?> ReadArtworkAsync(IRandomAccessStreamReference? reference, CancellationToken cancellationToken)
    {
        if (reference is null) return null;
        using var stream = await reference.OpenReadAsync();
        var length = (uint)Math.Min(stream.Size, MaximumArtworkBytes);
        if (length == 0) return null;
        using var reader = new DataReader(stream);
        await reader.LoadAsync(length).AsTask(cancellationToken);
        var bytes = new byte[length];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private void Publish(MediaSessionSnapshot snapshot)
    {
        _current = snapshot;
        Changed?.Invoke(this, snapshot);
    }
}
