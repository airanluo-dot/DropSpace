using System.Runtime.InteropServices;
using System.Threading.Channels;
using DropSpace.Core.Media;
using Microsoft.Extensions.Logging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DropSpace.App.Services.Media;

/// <summary>Owns SMTC subscriptions, a coalescing event consumer, and bounded metadata reads.</summary>
public sealed class WindowsMediaSessionService(ILogger<WindowsMediaSessionService> logger) : IMediaSessionService
{
    private const int MaximumArtworkBytes = 4 * 1024 * 1024;
    private const int MaximumMetadataCharacters = 2_048;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private CancellationTokenSource? _lifetime;
    private Channel<bool>? _refresh;
    private Task _consumer = Task.CompletedTask;
    private bool _disposed;
    private MediaSessionSnapshot _current = MediaSessionSnapshot.Empty;
    private string[] _allowedSources = [];

    public event EventHandler<MediaSessionSnapshot>? Changed;
    public MediaSessionSnapshot Current => Volatile.Read(ref _current);
    public bool IsAvailable { get; private set; }
    public string AvailabilityReason { get; private set; } = "NotInitialized";
    public IReadOnlyList<string> AvailableSources { get; private set; } = [];

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!enabled) { await StopAsync().ConfigureAwait(false); return; }
            if (_manager is not null) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(OperationTimeout);
            try
            {
                var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(timeout.Token).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                _lifetime = new();
                _refresh = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
                { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
                _manager = manager;
                manager.CurrentSessionChanged += OnCurrentSessionChanged;
                manager.SessionsChanged += OnSessionsChanged;
                IsAvailable = true;
                AvailabilityReason = "Available";
                _consumer = ConsumeAsync(_refresh.Reader, _lifetime.Token);
                RequestRefresh();
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                await StopAsync().ConfigureAwait(false);
                AvailabilityReason = exception.GetType().Name;
                logger.LogWarning("SMTC initialization unavailable ({Category}).", AvailabilityReason);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        finally { _lifecycle.Release(); }
    }

    public void SetAllowedSources(IEnumerable<string> sourceIds)
    {
        Volatile.Write(ref _allowedSources, sourceIds.Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(128).ToArray());
        RequestRefresh();
    }

    public Task PlayPauseAsync(CancellationToken cancellationToken = default) => ControlAsync(async (session, token) =>
    {
        if (Current.PlaybackState == MediaPlaybackState.Playing)
        { if (Current.CanPause) await session.TryPauseAsync().AsTask(token).ConfigureAwait(false); }
        else if (Current.CanPlay) await session.TryPlayAsync().AsTask(token).ConfigureAwait(false);
    }, cancellationToken);

    public Task SkipNextAsync(CancellationToken cancellationToken = default) => ControlAsync(async (session, token) =>
    { if (Current.CanSkipNext) await session.TrySkipNextAsync().AsTask(token).ConfigureAwait(false); }, cancellationToken);

    public Task SkipPreviousAsync(CancellationToken cancellationToken = default) => ControlAsync(async (session, token) =>
    { if (Current.CanSkipPrevious) await session.TrySkipPreviousAsync().AsTask(token).ConfigureAwait(false); }, cancellationToken);

    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) => ControlAsync(async (session, token) =>
    {
        if (!Current.CanSeek) return;
        var timeline = Current.Timeline;
        var ticks = Math.Clamp(position.Ticks, timeline.Start.Ticks, Math.Max(timeline.Start.Ticks, timeline.End.Ticks));
        await session.TryChangePlaybackPositionAsync(ticks).AsTask(token).ConfigureAwait(false);
    }, cancellationToken);

    private async Task ControlAsync(Func<GlobalSystemMediaTransportControlsSession, CancellationToken, Task> action, CancellationToken token)
    {
        await _sessionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_session is null || _lifetime is null) return;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            timeout.CancelAfter(OperationTimeout);
            await action(_session, timeout.Token).ConfigureAwait(false);
        }
        finally { _sessionGate.Release(); }
        RequestRefresh();
    }

    private async Task ConsumeAsync(ChannelReader<bool> reader, CancellationToken token)
    {
        try
        {
            await foreach (var _ in reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                await _sessionGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(OperationTimeout);
                    var snapshot = await ReadSnapshotAsync(timeout.Token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    Publish(snapshot);
                }
                catch (Exception exception) when (IsRecoverable(exception))
                {
                    if (token.IsCancellationRequested) break;
                    logger.LogDebug("SMTC refresh failed ({Category}).", exception.GetType().Name);
                    Publish(MediaSessionSnapshot.Empty);
                }
                finally { _sessionGate.Release(); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task<MediaSessionSnapshot> ReadSnapshotAsync(CancellationToken token)
    {
        var manager = _manager;
        if (manager is null) return MediaSessionSnapshot.Empty;
        var sessions = manager.GetSessions().Take(128).ToArray();
        AvailableSources = sessions.Select(session => Bound(session.SourceAppUserModelId)).Distinct().ToArray();
        var allowed = Volatile.Read(ref _allowedSources);
        bool Accept(GlobalSystemMediaTransportControlsSession value) => allowed.Length == 0 || allowed.Contains(value.SourceAppUserModelId, StringComparer.OrdinalIgnoreCase);
        var selected = manager.GetCurrentSession();
        if (selected is null || !Accept(selected))
            selected = sessions.FirstOrDefault(value => Accept(value) && value.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                ?? sessions.FirstOrDefault(Accept);
        if (!ReferenceEquals(selected, _session))
        {
            DetachSession();
            _session = selected;
            if (selected is not null)
            {
                selected.MediaPropertiesChanged += OnMediaPropertiesChanged;
                selected.PlaybackInfoChanged += OnPlaybackInfoChanged;
                selected.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
            }
        }
        if (selected is null) return MediaSessionSnapshot.Empty;
        var properties = await selected.TryGetMediaPropertiesAsync().AsTask(token).ConfigureAwait(false);
        var playback = selected.GetPlaybackInfo();
        var timeline = selected.GetTimelineProperties();
        var source = Bound(selected.SourceAppUserModelId);
        var title = Bound(properties.Title);
        var artist = Bound(properties.Artist);
        var album = Bound(properties.AlbumTitle);
        var previous = Current;
        var sameTrack = previous.SourceAppUserModelId == source && previous.TrackTitle == title && previous.Artist == artist && previous.AlbumTitle == album;
        var artwork = sameTrack && previous.Artwork is not null ? previous.Artwork : await ReadArtworkAsync(properties.Thumbnail, token).ConfigureAwait(false);
        return new(source, source, FriendlyName(source), title, artist, album, artwork,
            playback.PlaybackStatus switch
            {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaPlaybackState.Playing,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaPlaybackState.Paused,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => MediaPlaybackState.Stopped,
                _ => MediaPlaybackState.Unknown,
            }, playback.Controls.IsPlayEnabled, playback.Controls.IsPauseEnabled,
            playback.Controls.IsNextEnabled, playback.Controls.IsPreviousEnabled, playback.Controls.IsPlaybackPositionEnabled,
            new(timeline.Position, timeline.StartTime, timeline.EndTime, playback.PlaybackRate ?? 1, timeline.LastUpdatedTime), DateTimeOffset.UtcNow);
    }

    private static async Task<byte[]?> ReadArtworkAsync(IRandomAccessStreamReference? reference, CancellationToken token)
    {
        if (reference is null) return null;
        using var stream = await reference.OpenReadAsync().AsTask(token).ConfigureAwait(false);
        if (stream.Size is 0 or > MaximumArtworkBytes) return null;
        var length = checked((uint)stream.Size);
        using var reader = new DataReader(stream);
        if (await reader.LoadAsync(length).AsTask(token).ConfigureAwait(false) != length) return null;
        var bytes = new byte[length];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private async Task StopAsync()
    {
        _lifetime?.Cancel();
        _refresh?.Writer.TryComplete();
        await _consumer.ConfigureAwait(false);
        await _sessionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            DetachSession();
            if (_manager is not null)
            {
                _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
                _manager.SessionsChanged -= OnSessionsChanged;
            }
            _manager = null;
            _refresh = null;
            _lifetime?.Dispose();
            _lifetime = null;
            IsAvailable = false;
            AvailabilityReason = "Disabled";
            AvailableSources = [];
            Publish(MediaSessionSnapshot.Empty);
        }
        finally { _sessionGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            await StopAsync().ConfigureAwait(false);
        }
        finally { _lifecycle.Release(); }
    }

    private void DetachSession()
    {
        if (_session is null) return;
        _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
        _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        _session = null;
    }

    private void RequestRefresh() => _refresh?.Writer.TryWrite(true);
    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) => RequestRefresh();
    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args) => RequestRefresh();
    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => RequestRefresh();
    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => RequestRefresh();
    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) => RequestRefresh();
    private static bool IsRecoverable(Exception exception) => exception is COMException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException or IOException;
    private static string Bound(string? text) => text is null ? string.Empty : text[..Math.Min(text.Length, MaximumMetadataCharacters)];
    private static string FriendlyName(string identity)
    {
        var name = identity.Split('!')[0].Split('_')[0];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name[(name.LastIndexOf('.') + 1)..];
    }

    private void Publish(MediaSessionSnapshot snapshot)
    {
        Volatile.Write(ref _current, snapshot);
        if (Changed is not { } handlers) return;
        foreach (EventHandler<MediaSessionSnapshot> handler in handlers.GetInvocationList())
        {
            try { handler(this, snapshot); }
            catch (Exception exception) { logger.LogWarning("Media subscriber failed ({Category}).", exception.GetType().Name); }
        }
    }
}
