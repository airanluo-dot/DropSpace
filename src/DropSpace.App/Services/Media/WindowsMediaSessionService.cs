using System.Runtime.InteropServices;
using System.Threading.Channels;
using DropSpace.Core.Media;
using Microsoft.Extensions.Logging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DropSpace.App.Services.Media;

/// <summary>Owns SMTC subscriptions, a coalescing event consumer, and bounded metadata reads.</summary>
public sealed class WindowsMediaSessionService(ILogger<WindowsMediaSessionService> logger, DropSpace.Core.Abstractions.IAppStringLocalizer? strings = null) : IMediaSessionService
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
    private long _metadataRevision;
    private long _snapshotRevision;
    private long _artworkRevision = -1;
    private bool _restrictSources;
    private string _sessionIdentity = string.Empty;

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

    public void SetAllowedSources(IEnumerable<string> sourceIds, bool restrictToList = false)
    {
        Volatile.Write(ref _allowedSources, sourceIds.Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(128).ToArray());
        Volatile.Write(ref _restrictSources, restrictToList || _allowedSources.Length > 0);
        RequestRefresh();
    }

    public Task PlayPauseAsync(CancellationToken cancellationToken = default) => ControlAsync(async (session, token) =>
    {
        if (Current.PlaybackState == MediaPlaybackState.Playing)
        { EnsureAccepted(Current.CanPause && await session.TryPauseAsync().AsTask(token).ConfigureAwait(false)); }
        else EnsureAccepted(Current.CanPlay && await session.TryPlayAsync().AsTask(token).ConfigureAwait(false));
    }, cancellationToken);

    public Task SkipNextAsync(CancellationToken cancellationToken = default) => ControlAsync(async (session, token) =>
    { EnsureAccepted(Current.CanSkipNext && await session.TrySkipNextAsync().AsTask(token).ConfigureAwait(false)); }, cancellationToken);

    public Task SkipPreviousAsync(CancellationToken cancellationToken = default) => ControlAsync(async (session, token) =>
    { EnsureAccepted(Current.CanSkipPrevious && await session.TrySkipPreviousAsync().AsTask(token).ConfigureAwait(false)); }, cancellationToken);

    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) => ControlAsync(async (session, token) =>
    {
        EnsureAccepted(Current.CanSeek);
        var timeline = Current.Timeline;
        var ticks = Math.Clamp(position.Ticks, timeline.Start.Ticks, Math.Max(timeline.Start.Ticks, timeline.End.Ticks));
        EnsureAccepted(await session.TryChangePlaybackPositionAsync(ticks).AsTask(token).ConfigureAwait(false));
    }, cancellationToken);

    private static void EnsureAccepted(bool accepted)
    {
        if (!accepted) throw new InvalidOperationException("The media session rejected the requested control.");
    }

    private async Task ControlAsync(Func<GlobalSystemMediaTransportControlsSession, CancellationToken, Task> action, CancellationToken token)
    {
        await _sessionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_session is null || _lifetime is null) throw new InvalidOperationException("No media session is available.");
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
                    var current = Current;
                    Publish(_session is not null && current.IsActive ? current : MediaSessionSnapshot.Empty);
                }
                finally { _sessionGate.Release(); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task<MediaSessionSnapshot> ReadSnapshotAsync(CancellationToken token)
    {
        var observedAt = DateTimeOffset.UtcNow;
        var manager = _manager;
        if (manager is null) return MediaSessionSnapshot.Empty;
        var allowed = Volatile.Read(ref _allowedSources);
        bool Accept(GlobalSystemMediaTransportControlsSession value)
        {
            var source = TryReadSource(value);
            return source is not null &&
                (!Volatile.Read(ref _restrictSources) || allowed.Contains(source, StringComparer.OrdinalIgnoreCase));
        }
        GlobalSystemMediaTransportControlsSession? currentSession = null;
        try
        {
            currentSession = manager.GetCurrentSession();
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            logger.LogDebug("The current SMTC session could not be read ({Category}); fallback selection will use the session list.", exception.GetType().Name);
        }

        // Prefer an actively playing and explicitly current session before applying the safety
        // cap. The old first-128 slice could hide a valid Apple Music renderer behind unrelated
        // sessions, causing a false Empty snapshot.
        var rawSessions = manager.GetSessions().ToArray();
        var allSessions = rawSessions.Where(Accept).ToArray();
        var sessions = allSessions
            .OrderByDescending(value => ReferenceEquals(value, currentSession))
            .ThenByDescending(IsPlaying)
            .Take(512)
            .ToArray();
        AvailableSources = rawSessions
            .Take(512)
            .Select(TryReadSource)
            .Where(static source => !string.IsNullOrWhiteSpace(source))
            .Select(static source => source!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var selected = currentSession;
        if (selected is null || !Accept(selected))
            selected = sessions.FirstOrDefault() ?? allSessions.FirstOrDefault();
        var candidates = (selected is null ? sessions : new[] { selected }.Concat(sessions))
            .Distinct(new SessionReferenceComparer())
            .ToArray();
        if (candidates.Length == 0) return MediaSessionSnapshot.Empty;
        var previous = Current;
        foreach (var candidate in candidates)
        {
            if (!ReferenceEquals(candidate, _session))
            {
                DetachSession();
                _session = candidate;
                _sessionIdentity = Guid.NewGuid().ToString("N");
                candidate.MediaPropertiesChanged += OnMediaPropertiesChanged;
                candidate.PlaybackInfoChanged += OnPlaybackInfoChanged;
                candidate.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
            }
            try
            {
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var revision = Interlocked.Read(ref _snapshotRevision);
                    var metadataRevision = Interlocked.Read(ref _metadataRevision);
                    var properties = await candidate.TryGetMediaPropertiesAsync().AsTask(token).ConfigureAwait(false);
                    var playback = candidate.GetPlaybackInfo();
                    var timeline = candidate.GetTimelineProperties();
                    if (revision != Interlocked.Read(ref _snapshotRevision) && attempt == 0) continue;
                    if (revision != Interlocked.Read(ref _snapshotRevision))
                    {
                        RequestRefresh();
                        return previous.IsActive ? previous : MediaSessionSnapshot.Empty;
                    }
                    var source = Bound(candidate.SourceAppUserModelId);
                    var title = Bound(properties.Title);
                    var artist = Bound(properties.Artist);
                    var albumArtist = Bound(properties.AlbumArtist);
                    var album = Bound(properties.AlbumTitle);
                    var trackNumber = properties.TrackNumber;
                    var duration = timeline.EndTime > timeline.StartTime ? timeline.EndTime - timeline.StartTime : TimeSpan.Zero;
                    var durationConsistent = previous.Timeline.Duration <= TimeSpan.Zero || duration <= TimeSpan.Zero ||
                        Math.Abs((previous.Timeline.Duration - duration).TotalSeconds) <= 2;
                    var sameTrack = previous.SessionId == _sessionIdentity && previous.SourceAppUserModelId == source && previous.TrackTitle == title &&
                        previous.Artist == artist && previous.AlbumArtist == albumArtist && previous.AlbumTitle == album &&
                        previous.TrackNumber == trackNumber && durationConsistent;
                    var effectiveStart = timeline.StartTime;
                    var effectiveEnd = timeline.EndTime;
                    if (sameTrack && duration <= TimeSpan.Zero && previous.Timeline.Duration > TimeSpan.Zero)
                    {
                        // A short-lived zero timeline is an Apple Music metadata refresh, not a new
                        // track. Keep the last known bounds so the UI and lyric clock do not collapse
                        // to a one-second duration while the native session catches up.
                        effectiveStart = previous.Timeline.Start;
                        effectiveEnd = previous.Timeline.End;
                    }
                    // Players can deliver the new title before its thumbnail. Metadata events
                    // invalidate artwork even when the track key has not changed.
                    var artwork = sameTrack && _artworkRevision == metadataRevision && previous.Artwork is not null
                        ? previous.Artwork : await ReadArtworkAsync(properties.Thumbnail, token).ConfigureAwait(false);
                    if (sameTrack && artwork is not null && previous.Artwork is not null && artwork.AsSpan().SequenceEqual(previous.Artwork)) artwork = previous.Artwork;
                    if (revision != Interlocked.Read(ref _snapshotRevision))
                    {
                        RequestRefresh();
                        return previous.IsActive ? previous : MediaSessionSnapshot.Empty;
                    }
                    _artworkRevision = metadataRevision;
                    return new(_sessionIdentity, source, FriendlyName(source, strings), title, artist, album, artwork,
                        playback.PlaybackStatus switch
                        {
                            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaPlaybackState.Playing,
                            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaPlaybackState.Paused,
                            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => MediaPlaybackState.Stopped,
                            _ => MediaPlaybackState.Unknown,
                        }, playback.Controls.IsPlayEnabled, playback.Controls.IsPauseEnabled,
                        playback.Controls.IsNextEnabled, playback.Controls.IsPreviousEnabled, playback.Controls.IsPlaybackPositionEnabled,
                        new(timeline.Position, effectiveStart, effectiveEnd, playback.PlaybackRate ?? 1, timeline.LastUpdatedTime), observedAt,
                        albumArtist, trackNumber);
                }
            }
            catch (Exception exception) when (IsRecoverable(exception) && exception is not OperationCanceledException)
            {
                logger.LogDebug("SMTC candidate session could not be read ({Category}); trying the next session.", exception.GetType().Name);
                if (ReferenceEquals(_session, candidate)) DetachSession();
            }
        }
        return previous.IsActive ? previous : MediaSessionSnapshot.Empty;
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
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }
        _session = null;
        _sessionIdentity = string.Empty;
    }

    private void RequestRefresh() => _refresh?.Writer.TryWrite(true);
    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) => RequestRefresh();
    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args) => RequestRefresh();
    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, _session)) return;
        Interlocked.Increment(ref _metadataRevision);
        Interlocked.Increment(ref _snapshotRevision);
        RequestRefresh();
    }
    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, _session)) return;
        Interlocked.Increment(ref _snapshotRevision);
        RequestRefresh();
    }
    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, _session)) return;
        Interlocked.Increment(ref _snapshotRevision);
        RequestRefresh();
    }
    private static bool IsPlaying(GlobalSystemMediaTransportControlsSession value)
    {
        try { return value.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
        catch (Exception exception) when (IsRecoverable(exception)) { return false; }
    }
    private string? TryReadSource(GlobalSystemMediaTransportControlsSession value)
    {
        try
        {
            var source = Bound(value.SourceAppUserModelId);
            return string.IsNullOrWhiteSpace(source) ? null : source;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            logger.LogDebug("An SMTC session source could not be read ({Category}); the session was skipped.", exception.GetType().Name);
            return null;
        }
    }

    private static bool IsRecoverable(Exception exception) => exception is COMException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException or IOException or ArgumentException;
    private static string Bound(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= MaximumMetadataCharacters)
        {
            return text ?? string.Empty;
        }

        var length = MaximumMetadataCharacters;
        // Do not leave a dangling UTF-16 surrogate in a title/artist that is later used as a
        // lyrics key or displayed in XAML.
        if (length < text.Length && char.IsLowSurrogate(text[length]) && char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return text[..length];
    }
    public static string FriendlyName(string identity, DropSpace.Core.Abstractions.IAppStringLocalizer? strings = null)
    {
        if (identity.Equals("cloudmusic.exe", StringComparison.OrdinalIgnoreCase)) return strings?.Get("LyricsProviderNetEase") ?? "NetEase Cloud Music";
        if (identity.Contains("AppleMusic", StringComparison.OrdinalIgnoreCase)) return "Apple Music";
        if (identity.Contains("QQMusic", StringComparison.OrdinalIgnoreCase)) return "QQ Music";
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

    private sealed class SessionReferenceComparer : IEqualityComparer<GlobalSystemMediaTransportControlsSession>
    {
        public bool Equals(GlobalSystemMediaTransportControlsSession? x, GlobalSystemMediaTransportControlsSession? y) => ReferenceEquals(x, y);

        public int GetHashCode(GlobalSystemMediaTransportControlsSession obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
