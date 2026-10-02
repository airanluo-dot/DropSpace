using System.Runtime.InteropServices;
using System.Threading.Channels;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Media;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DropSpace.App.Services.Media;

/// <summary>Owns SMTC subscriptions, a coalescing event consumer, and bounded metadata reads.</summary>
public sealed class WindowsMediaSessionService(ILogger<WindowsMediaSessionService> logger, DropSpace.Core.Abstractions.IAppStringLocalizer? strings = null, DispatcherQueue? dispatcher = null) : IMediaSessionService
{
    private const int MaximumArtworkBytes = 4 * 1024 * 1024;
    private const int MaximumMetadataCharacters = 2_048;
    private const int MaximumRecoverySessions = 8;
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _sessionGate = new(1, 1);
    // Pools survive disable/re-enable: retiring a waiter must not forget native ownership.
    private readonly BoundedMediaOperation _connections = new(2, 2);
    private readonly BoundedMediaOperation _metadataReads = new();
    private readonly BoundedMediaOperation _artworkReads = new();
    private readonly BoundedMediaOperation _controls = new();
    private readonly BoundedMediaOperation _discoveryReads = new();
    private readonly BoundedMediaOperation _subscriptions = new(32, 3);
    private MediaEventSubscription? _managerSubscription, _sessionSubscription;
    private readonly Dictionary<GlobalSystemMediaTransportControlsSession, MediaEventSubscription> _recoverySubscriptions = new(new SessionReferenceComparer());
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private GlobalSystemMediaTransportControlsSession[] _recoverySessions = [];
    private CancellationTokenSource? _lifetime;
    private Channel<bool>? _refresh;
    private Task _consumer = Task.CompletedTask;
    private bool _disposed;
    private MediaSessionSnapshot _current = MediaSessionSnapshot.Empty;
    private string[] _allowedSources = [];
    private long _metadataRevision;
    private long _artworkRevision = -1;
    private bool _restrictSources;
    private string _sessionIdentity = string.Empty;

    public event EventHandler<MediaSessionSnapshot>? Changed;
    public MediaSessionSnapshot Current => Volatile.Read(ref _current);
    public bool IsAvailable { get; private set; }
    public string AvailabilityReason { get; private set; } = "NotInitialized";
    public IReadOnlyList<string> AvailableSources { get; private set; } = [];

    public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
        dispatcher is not null && !dispatcher.HasThreadAccess
            ? dispatcher.EnqueueAsync(() => SetEnabledCoreAsync(enabled, cancellationToken))
            : SetEnabledCoreAsync(enabled, cancellationToken);

    public Task RestartAsync(CancellationToken cancellationToken = default) =>
        dispatcher is not null && !dispatcher.HasThreadAccess
            ? dispatcher.EnqueueAsync(() => SetEnabledCoreAsync(true, cancellationToken, restart: true))
            : SetEnabledCoreAsync(true, cancellationToken, restart: true);

    // Serialize service state on the dispatcher. SMTC manager/session objects are
    // documented Agile; native calls run in bounded workers, never on the UI thread.
    private async Task SetEnabledCoreAsync(bool enabled, CancellationToken cancellationToken, bool restart = false)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (restart) await StopAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (!enabled) { await StopAsync(); return; }
            if (_manager is not null) { RequestRefresh(); return; }
            try
            {
                var manager = await _connections.RunAsync(this,
                    readToken => AwaitNativeAsync(GlobalSystemMediaTransportControlsSessionManager.RequestAsync(), readToken),
                    OperationTimeout, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                _lifetime = new();
                _refresh = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
                { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
                _manager = manager;
                _managerSubscription = await SubscribeManagerAsync(manager, cancellationToken);
                IsAvailable = true;
                AvailabilityReason = "Available";
                var firstRefresh = restart ? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) : null;
                if (firstRefresh is not null)
                    _ = firstRefresh.Task.ContinueWith(static failed => { _ = failed.Exception; }, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                _consumer = ConsumeAsync(_refresh.Reader, _lifetime.Token, ReadSnapshotAsync, firstRefresh);
                RequestRefresh();
                // A manual reconnect is successful only after the new consumer has actually
                // read a snapshot. An empty session list is a valid successful result.
                if (firstRefresh is not null) await firstRefresh.Task.WaitAsync(cancellationToken);
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                await StopAsync();
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
        { EnsureAccepted(Current.CanPause && await AwaitNativeAsync(session.TryPauseAsync(), token)); }
        else EnsureAccepted(Current.CanPlay && await AwaitNativeAsync(session.TryPlayAsync(), token));
    }, cancellationToken);

    public Task SkipNextAsync(CancellationToken cancellationToken = default) => ControlAsync(async (session, token) =>
    { EnsureAccepted(Current.CanSkipNext && await AwaitNativeAsync(session.TrySkipNextAsync(), token)); }, cancellationToken);

    public Task SkipPreviousAsync(CancellationToken cancellationToken = default) => ControlAsync(async (session, token) =>
    { EnsureAccepted(Current.CanSkipPrevious && await AwaitNativeAsync(session.TrySkipPreviousAsync(), token)); }, cancellationToken);

    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) => ControlAsync(async (session, token) =>
    {
        EnsureAccepted(Current.CanSeek);
        var timeline = Current.Timeline;
        var ticks = Math.Clamp(position.Ticks, timeline.Start.Ticks, Math.Max(timeline.Start.Ticks, timeline.End.Ticks));
        EnsureAccepted(await AwaitNativeAsync(session.TryChangePlaybackPositionAsync(ticks), token));
    }, cancellationToken);

    private static void EnsureAccepted(bool accepted)
    {
        if (!accepted) throw new InvalidOperationException("The media session rejected the requested control.");
    }

    private Task ControlAsync(Func<GlobalSystemMediaTransportControlsSession, CancellationToken, Task> action, CancellationToken token) =>
        dispatcher is not null && !dispatcher.HasThreadAccess
            ? dispatcher.EnqueueAsync(() => ControlCoreAsync(action, token)) : ControlCoreAsync(action, token);

    private async Task ControlCoreAsync(Func<GlobalSystemMediaTransportControlsSession, CancellationToken, Task> action, CancellationToken token)
    {
        await _sessionGate.WaitAsync(token);
        try
        {
            if (_session is null || _lifetime is null) throw new InvalidOperationException("No media session is available.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            timeout.CancelAfter(OperationTimeout);
            var session = _session;
            await _controls.RunAsync(session, async controlToken =>
            {
                await action(session, controlToken);
                return true;
            }, OperationTimeout, timeout.Token);
        }
        finally { _sessionGate.Release(); }
        RequestRefresh();
    }

    internal async Task ConsumeAsync(ChannelReader<bool> reader, CancellationToken token,
        Func<CancellationToken, Task<MediaSessionSnapshot>> readSnapshot, TaskCompletionSource? firstRefresh = null)
    {
        var consecutiveFailures = 0;
        try
        {
            await foreach (var _ in reader.ReadAllAsync(token))
            {
                var retry = false;
                await _sessionGate.WaitAsync(token);
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(OperationTimeout);
                    var snapshot = await readSnapshot(timeout.Token);
                    token.ThrowIfCancellationRequested();
                    IsAvailable = true;
                    AvailabilityReason = "Available";
                    Publish(snapshot);
                    firstRefresh?.TrySetResult();
                    consecutiveFailures = 0;
                }
                catch (Exception exception) when (IsRecoverable(exception))
                {
                    if (token.IsCancellationRequested) break;
                    logger.LogDebug("SMTC refresh failed ({Category}).", exception.GetType().Name);
                    retry = ++consecutiveFailures <= 3;
                    IsAvailable = false;
                    AvailabilityReason = exception.GetType().Name;
                    // A dead publisher is not evidence that the previous song is still current.
                    // Clear stale UI while keeping bounded recovery and manual refresh usable.
                    Publish(MediaSessionSnapshot.Empty);
                    firstRefresh?.TrySetException(exception);
                }
                finally { _sessionGate.Release(); }
                if (retry)
                {
                    // The last track-change notification may coincide with a transient
                    // native failure. Recover without waiting indefinitely for another event,
                    // but stop after three retries instead of polling a broken publisher.
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * consecutiveFailures), token);
                    RequestRefresh();
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { firstRefresh?.TrySetCanceled(token); }
        catch (Exception exception)
        {
            firstRefresh?.TrySetException(exception);
            throw;
        }
    }

    private async Task<MediaSessionSnapshot> ReadSnapshotAsync(CancellationToken token)
    {
        var observedAt = DateTimeOffset.UtcNow;
        var manager = _manager;
        if (manager is null) return MediaSessionSnapshot.Empty;
        var allowed = Volatile.Read(ref _allowedSources);
        var restrict = Volatile.Read(ref _restrictSources);
        var discovery = await _discoveryReads.RunAsync(manager, _ =>
        {
            GlobalSystemMediaTransportControlsSession? current = null;
            try { current = manager.GetCurrentSession(); }
            catch (Exception exception) when (IsRecoverable(exception))
            { logger.LogDebug("The current SMTC session is unavailable ({Category}).", exception.GetType().Name); }
            return Task.FromResult((Current: current, Sessions: manager.GetSessions().ToArray()));
        }, TimeSpan.FromSeconds(2), token);
        var ordered = (discovery.Current is null ? discovery.Sessions : new[] { discovery.Current }.Concat(discovery.Sessions))
            .Distinct(new SessionReferenceComparer())
            .OrderByDescending(value => ReferenceEquals(value, discovery.Current))
            .ThenByDescending(value => ReferenceEquals(value, _session))
            .ThenByDescending(IsRecoverySession).ToArray();
        var descriptors = await ReadCandidatesAsync(ordered,
            async (value, readToken) => await _discoveryReads.RunAsync(value, _ =>
                Task.FromResult((Source: TryReadSource(value), Playing: IsPlaying(value))), TimeSpan.FromMilliseconds(500), readToken),
            TimeSpan.FromSeconds(1), token);
        var sources = descriptors.Where(value => value.Value.Source is not null)
            .ToDictionary(value => value.Candidate, value => value.Value.Source!, new SessionReferenceComparer());
        AvailableSources = sources.Values.Distinct(StringComparer.OrdinalIgnoreCase).Take(512).ToArray();
        string? Source(GlobalSystemMediaTransportControlsSession value) => sources.GetValueOrDefault(value);
        var candidates = descriptors.Where(value => value.Value.Source is { } source &&
                (!restrict || allowed.Contains(source, StringComparer.OrdinalIgnoreCase)))
            .OrderByDescending(value => ReferenceEquals(value.Candidate, discovery.Current))
            .ThenByDescending(value => value.Value.Playing).Select(value => value.Candidate).Take(512).ToArray();
        if (candidates.Length == 0)
        {
            await ObserveRecoverySessionsAsync([], token);
            DetachSession();
            return MediaSessionSnapshot.Empty;
        }
        // NetEase can publish a weak native renderer and a richer InfLink renderer whose
        // track changes arrive in either order. Keep the bounded same-source candidate set
        // observed so a temporarily divergent richer renderer can announce that it caught up.
        // Otherwise we can switch to the weak renderer, unsubscribe from InfLink and never
        // regain its controls until the OS happens to raise a manager-level event.
        await ObserveRecoverySessionsAsync(SelectRecoverySessions(candidates, Source, MaximumRecoverySessions), token);
        // Preserve the OS/player priority. Only replace its renderer with a richer
        // renderer from the same application reporting the same track.
        var richer = await SelectRicherSessionAsync(candidates, Source, ReadSelectionAsync, token, _session, TimeSpan.FromMilliseconds(500));
        candidates = new[] { richer }.Concat(candidates).Distinct(new SessionReferenceComparer()).Take(MaximumRecoverySessions).ToArray();
        var previous = Current;
        Exception? lastFailure = null;
        foreach (var candidate in candidates)
        {
            try
            {
                if (!ReferenceEquals(candidate, _session) || _sessionSubscription is null)
                {
                    DetachSession();
                    _session = candidate;
                    _sessionIdentity = Guid.NewGuid().ToString("N");
                    try { _sessionSubscription = await SubscribeSessionAsync(candidate, false, token); }
                    catch (Exception exception) when (IsRecoverable(exception) && exception is not OperationCanceledException)
                    { logger.LogDebug("SMTC primary subscription unavailable ({Category}).", exception.GetType().Name); }
                }
                var (properties, metadataRevision) = await ReadStableMetadataAsync(
                    readToken => _metadataReads.RunAsync(candidate,
                        nativeToken => ReadNativeTrackAsync(candidate, nativeToken),
                        TimeSpan.FromSeconds(2), readToken),
                    () => Interlocked.Read(ref _metadataRevision), token,
                    static (left, right) => left.Snapshot.IsSameTrack(right.Snapshot));
                if (properties is null)
                    throw new InvalidOperationException("The media publisher changed tracks throughout the metadata read.");
                var native = properties.Snapshot;
                var timeline = native.Timeline;
                var source = native.SourceAppUserModelId;
                var title = native.TrackTitle;
                var artist = native.Artist;
                var albumArtist = native.AlbumArtist;
                var album = native.AlbumTitle;
                var trackNumber = native.TrackNumber;
                var duration = timeline.Duration;
                var durationConsistent = previous.Timeline.Duration <= TimeSpan.Zero || duration <= TimeSpan.Zero ||
                    Math.Abs((previous.Timeline.Duration - duration).TotalSeconds) <= 2;
                var sameTrack = previous.SessionId == _sessionIdentity && previous.SourceAppUserModelId == source && previous.TrackTitle == title &&
                    previous.Artist == artist && previous.AlbumArtist == albumArtist && previous.AlbumTitle == album &&
                    previous.TrackNumber == trackNumber && durationConsistent;
                var effectiveStart = timeline.Start;
                var effectiveEnd = timeline.End;
                if (sameTrack && duration <= TimeSpan.Zero && previous.Timeline.Duration > TimeSpan.Zero)
                {
                    // A short-lived zero timeline is an Apple Music metadata refresh, not a new
                    // track. Keep the last known bounds so the UI and lyric clock do not collapse
                    // to a one-second duration while the native session catches up.
                    effectiveStart = previous.Timeline.Start;
                    effectiveEnd = previous.Timeline.End;
                }
                if (metadataRevision != Interlocked.Read(ref _metadataRevision))
                {
                    RequestRefresh();
                    return Current;
                }
                // Publish current metadata before optional artwork. A thumbnail publisher
                // may ignore cancellation, but must never hold back the title or lyrics.
                var cachedArtwork = sameTrack && _artworkRevision == metadataRevision ? previous.Artwork : null;
                var snapshot = native with
                {
                    SessionId = _sessionIdentity, SourceDisplayName = FriendlyName(source, strings),
                    Artwork = cachedArtwork, LastUpdated = observedAt,
                    Timeline = timeline with { Start = effectiveStart, End = effectiveEnd },
                };
                return await CompleteArtworkAsync(snapshot, metadataRevision,
                    artworkToken => ReadArtworkAsync(properties.Thumbnail, artworkToken), candidate, token);
            }
            catch (OperationCanceledException)
            {
                if (_sessionSubscription is null && ReferenceEquals(_session, candidate)) DetachSession();
                throw;
            }
            catch (Exception exception) when (IsRecoverable(exception) && exception is not OperationCanceledException)
            {
                logger.LogDebug("SMTC candidate session could not be read ({Category}); trying the next session.", exception.GetType().Name);
                lastFailure = exception;
                if (ReferenceEquals(_session, candidate)) DetachSession();
            }
        }
        if (lastFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(lastFailure).Throw();
        return MediaSessionSnapshot.Empty;
    }

    internal static async Task<IReadOnlyList<(T Candidate, TValue Value)>> ReadCandidatesAsync<T, TValue>(IEnumerable<T> candidates,
        Func<T, CancellationToken, Task<TValue>> read, TimeSpan budget, CancellationToken token) where T : class
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        stop.CancelAfter(budget);
        var completed = new List<(T Candidate, TValue Value)>();
        Exception? lastFailure = null;
        foreach (var candidate in candidates)
        {
            try
            {
                stop.Token.ThrowIfCancellationRequested();
                completed.Add((candidate, await read(candidate, stop.Token).WaitAsync(stop.Token)));
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { lastFailure = new TimeoutException("Optional media discovery exceeded its budget."); break; }
            catch (Exception exception) when (IsRecoverable(exception) && exception is not OperationCanceledException)
            { lastFailure = exception; }
        }
        token.ThrowIfCancellationRequested();
        if (completed.Count == 0 && lastFailure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(lastFailure).Throw();
        return completed;
    }

    internal async Task<MediaSessionSnapshot> CompleteArtworkAsync(MediaSessionSnapshot snapshot, long metadataRevision,
        Func<CancellationToken, Task<byte[]?>> read, object owner, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (metadataRevision != Interlocked.Read(ref _metadataRevision)) return Current;
        var previous = Current;
        Publish(snapshot);
        byte[]? artwork;
        try { artwork = snapshot.Artwork ?? await ReadOptionalArtworkAsync(read, token, owner); }
        catch (OperationCanceledException) when (_lifetime is { IsCancellationRequested: false })
        {
            // The aggregate refresh budget may expire after metadata succeeded. Optional
            // artwork must not erase that successful publication. Shutdown still cancels.
            return snapshot;
        }
        if (metadataRevision != Interlocked.Read(ref _metadataRevision))
        {
            RequestRefresh();
            return Current;
        }
        if (snapshot.IsSameTrack(previous) && artwork is not null && previous.Artwork is not null && artwork.AsSpan().SequenceEqual(previous.Artwork))
            artwork = previous.Artwork;
        _artworkRevision = metadataRevision;
        return snapshot with { Artwork = artwork };
    }

    // Only metadata notifications invalidate an in-flight metadata read. Continuous
    // playback/timeline events must not starve title and artwork updates.
    internal static async Task<(T? Value, long Revision)> ReadStableMetadataAsync<T>(
        Func<CancellationToken, Task<T>> read, Func<long> metadataRevision, CancellationToken token,
        Func<T, T, bool>? equivalent = null) where T : class
    {
        T? previous = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            token.ThrowIfCancellationRequested();
            var revision = metadataRevision();
            var value = await read(token).WaitAsync(token);
            token.ThrowIfCancellationRequested();
            var latestRevision = metadataRevision();
            if (revision == latestRevision || (previous is not null && equivalent?.Invoke(previous, value) == true))
                return (value, latestRevision);
            previous = value;
        }
        return (null, metadataRevision());
    }

    internal sealed record SessionSelection(string Title, string Artist, string Album, TimeSpan Duration,
        bool CanSeek, bool HasArtwork, bool IsPlaying = false);

    internal static async Task<T> SelectRicherSessionAsync<T>(IReadOnlyList<T> candidates,
        Func<T, string?> source, Func<T, CancellationToken, Task<SessionSelection?>> read,
        CancellationToken token, T? retained = null, TimeSpan? selectionBudget = null) where T : class
    {
        var preferred = candidates[0];
        var sourceId = source(preferred);
        var siblings = candidates.Skip(1).Where(value => string.Equals(source(value), sourceId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (string.IsNullOrWhiteSpace(sourceId) || siblings.Length == 0) return preferred;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(selectionBudget ?? TimeSpan.FromSeconds(2));
        var selected = preferred;
        try
        {
            var baseline = await read(preferred, budget.Token).WaitAsync(budget.Token);
            if (baseline is null || string.IsNullOrWhiteSpace(baseline.Title) || string.IsNullOrWhiteSpace(baseline.Artist))
            {
                // Some publishers clear the preferred renderer between songs. Preserve the
                // still-live selected object during that short transition instead of replacing
                // it with an empty, uncontrollable snapshot. Its recovery events remain watched.
                if (retained is not null && !ReferenceEquals(retained, preferred) &&
                    candidates.Any(value => ReferenceEquals(value, retained)) &&
                    string.Equals(source(retained), sourceId, StringComparison.OrdinalIgnoreCase))
                {
                    var retainedValue = await read(retained, budget.Token).WaitAsync(budget.Token);
                    if (retainedValue is not null && !string.IsNullOrWhiteSpace(retainedValue.Title) &&
                        !string.IsNullOrWhiteSpace(retainedValue.Artist)) return retained;
                }
                // At startup there may be no retained renderer. An empty OS-selected
                // wrapper must not hide the single playing, readable renderer from the
                // same app. Keep the preferred choice when multiple tracks are ambiguous.
                if (baseline is null || string.IsNullOrWhiteSpace(baseline.Title))
                {
                    T? readable = null;
                    foreach (var sibling in siblings.Take(MaximumRecoverySessions))
                    {
                        var value = await read(sibling, budget.Token).WaitAsync(budget.Token);
                        if (value is null || !value.IsPlaying || string.IsNullOrWhiteSpace(value.Title)) continue;
                        if (readable is not null) return preferred;
                        readable = sibling;
                    }
                    if (readable is not null) return readable;
                }
                return preferred;
            }
            var best = Score(baseline);
            foreach (var sibling in siblings)
            {
                var value = await read(sibling, budget.Token).WaitAsync(budget.Token);
                if (value is null || !SameTrack(baseline, value)) continue;
                var score = Score(value);
                if (score > best) { best = score; selected = sibling; }
            }
        }
        catch (OperationCanceledException) { token.ThrowIfCancellationRequested(); }
        return selected;

        static int Score(SessionSelection value) =>
            (value.CanSeek && value.Duration > TimeSpan.Zero ? 16 : 0) +
            (value.Duration > TimeSpan.Zero ? 8 : 0) + (!string.IsNullOrWhiteSpace(value.Album) ? 4 : 0) +
            (value.HasArtwork ? 2 : 0);
        static bool SameTrack(SessionSelection a, SessionSelection b) =>
            LyricsMatcher.AreTitlesEquivalent(a.Title, b.Title) &&
            LyricsMatcher.AreArtistCreditsCompatible(a.Artist, b.Artist) &&
            (string.IsNullOrWhiteSpace(a.Album) || string.IsNullOrWhiteSpace(b.Album) || string.Equals(a.Album.Trim(), b.Album.Trim(), StringComparison.OrdinalIgnoreCase)) &&
            (a.Duration <= TimeSpan.Zero || b.Duration <= TimeSpan.Zero || Math.Abs((a.Duration - b.Duration).TotalSeconds) <= 2);
    }

    internal static IReadOnlyList<T> SelectRecoverySessions<T>(IReadOnlyList<T> candidates,
        Func<T, string?> source, int maximum) where T : class
    {
        if (candidates.Count == 0 || maximum <= 0) return [];
        var sourceId = source(candidates[0]);
        if (string.IsNullOrWhiteSpace(sourceId)) return [];
        var selected = new List<T>(Math.Min(maximum, candidates.Count));
        foreach (var candidate in candidates)
        {
            if (!string.Equals(source(candidate), sourceId, StringComparison.OrdinalIgnoreCase) ||
                selected.Any(value => ReferenceEquals(value, candidate))) continue;
            selected.Add(candidate);
            if (selected.Count == maximum) break;
        }
        return selected;
    }

    // AsTask(token) in the SDK projection cancels its managed bridge immediately;
    // that is not proof that the WinRT operation (or a stream read) has completed.
    // Keep ownership tied to Completed, and request native cancellation separately.
    internal static Task<T> AwaitNativeAsync<T>(Windows.Foundation.IAsyncOperation<T> operation, CancellationToken token) =>
        AwaitNativeCompletionAsync(operation.AsTask(CancellationToken.None), operation.Cancel, token);

    internal static Task AwaitNativeAsync(Windows.Foundation.IAsyncAction operation, CancellationToken token) =>
        AwaitNativeCompletionAsync(CompleteActionAsync(operation), operation.Cancel, token);

    private static async Task<bool> CompleteActionAsync(Windows.Foundation.IAsyncAction operation)
    {
        await operation.AsTask(CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    private static async Task<T> AwaitNativeCompletionAsync<T>(Task<T> completion, Action cancel, CancellationToken token)
    {
        Task cancellation = Task.CompletedTask;
        var registration = token.Register(() =>
        {
            // Register can invoke inline for an already-canceled token, including on the
            // WinUI image dispatcher. Never run a publisher's Cancel on that thread.
            Volatile.Write(ref cancellation, Task.Run(() =>
            {
                try { cancel(); }
                catch (Exception) { /* A failing Cancel is not native completion. */ }
            }));
        });
        try { return await completion.ConfigureAwait(false); }
        finally
        {
            registration.Dispose();
            // Do not release native ownership while its cancellation callback is using it.
            await Volatile.Read(ref cancellation).ConfigureAwait(false);
        }
    }

    private sealed record NativeTrack(MediaSessionSnapshot Snapshot, IRandomAccessStreamReference? Thumbnail);

    private static async Task<NativeTrack> ReadNativeTrackAsync(GlobalSystemMediaTransportControlsSession session, CancellationToken token)
    {
        var properties = await AwaitNativeAsync(session.TryGetMediaPropertiesAsync(), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var playback = session.GetPlaybackInfo();
        token.ThrowIfCancellationRequested();
        var timeline = session.GetTimelineProperties();
        token.ThrowIfCancellationRequested();
        var controls = playback.Controls;
        return new(new(string.Empty, Bound(session.SourceAppUserModelId), string.Empty,
            Bound(properties.Title), Bound(properties.Artist), Bound(properties.AlbumTitle), null,
            playback.PlaybackStatus switch
            {
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaPlaybackState.Playing,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaPlaybackState.Paused,
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => MediaPlaybackState.Stopped,
                _ => MediaPlaybackState.Unknown,
            }, controls.IsPlayEnabled, controls.IsPauseEnabled, controls.IsNextEnabled, controls.IsPreviousEnabled,
            controls.IsPlaybackPositionEnabled,
            new(timeline.Position, timeline.StartTime, timeline.EndTime, playback.PlaybackRate ?? 1, timeline.LastUpdatedTime),
            DateTimeOffset.UtcNow, Bound(properties.AlbumArtist), properties.TrackNumber), properties.Thumbnail);
    }

    private async Task<SessionSelection?> ReadSelectionAsync(GlobalSystemMediaTransportControlsSession session, CancellationToken token)
    {
        try
        {
            var track = await _metadataReads.RunAsync(session,
                nativeToken => ReadNativeTrackAsync(session, nativeToken), TimeSpan.FromMilliseconds(500), token);
            var value = track.Snapshot;
            return new(value.TrackTitle, value.Artist, value.AlbumTitle, value.Timeline.Duration,
                value.CanSeek, track.Thumbnail is not null, value.PlaybackState == MediaPlaybackState.Playing);
        }
        catch (Exception exception) when (IsRecoverable(exception) && exception is not OperationCanceledException)
        {
            logger.LogDebug("SMTC duplicate candidate could not be read ({Category}).", exception.GetType().Name);
            return null;
        }
    }

    internal async Task<byte[]?> ReadOptionalArtworkAsync(Func<CancellationToken, Task<byte[]?>> read, CancellationToken token, object? owner = null)
    {
        try
        {
            return await _artworkReads.RunAsync(owner ?? this, read, TimeSpan.FromSeconds(1), token);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            token.ThrowIfCancellationRequested();
            logger.LogDebug("SMTC artwork unavailable ({Category}).", exception.GetType().Name);
            return null;
        }
    }

    private static async Task<byte[]?> ReadArtworkAsync(IRandomAccessStreamReference? reference, CancellationToken token)
    {
        if (reference is null) return null;
        token.ThrowIfCancellationRequested();
        using var stream = await AwaitNativeAsync(reference.OpenReadAsync(), token);
        token.ThrowIfCancellationRequested();
        if (stream.Size is 0 or > MaximumArtworkBytes) return null;
        var length = checked((uint)stream.Size);
        using var reader = new DataReader(stream);
        token.ThrowIfCancellationRequested();
        if (await AwaitNativeAsync(reader.LoadAsync(length), token) != length) return null;
        token.ThrowIfCancellationRequested();
        var bytes = new byte[length];
        reader.ReadBytes(bytes);
        return bytes;
    }

    private async Task StopAsync()
    {
        _lifetime?.Cancel();
        _refresh?.Writer.TryComplete();
        await _consumer;
        await _sessionGate.WaitAsync();
        try
        {
            RetireRecoverySessions();
            DetachSession();
            _managerSubscription?.Retire();
            _managerSubscription = null;
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

    public ValueTask DisposeAsync() => new(dispatcher is not null && !dispatcher.HasThreadAccess
        ? dispatcher.EnqueueAsync(DisposeCoreAsync) : DisposeCoreAsync());

    private async Task DisposeCoreAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            await StopAsync();
        }
        finally { _lifecycle.Release(); }
    }

    private void DetachSession()
    {
        _sessionSubscription?.Retire();
        _sessionSubscription = null;
        _session = null;
        _sessionIdentity = string.Empty;
    }

    private async Task ObserveRecoverySessionsAsync(IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions, CancellationToken token)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromMilliseconds(500));
        var nextSessions = sessions.ToArray();
        Volatile.Write(ref _recoverySessions, nextSessions);
        foreach (var previous in _recoverySubscriptions.Keys.ToArray())
        {
            if (nextSessions.Any(value => ReferenceEquals(value, previous))) continue;
            _recoverySubscriptions[previous].Retire();
            _recoverySubscriptions.Remove(previous);
        }
        foreach (var session in nextSessions)
        {
            if (_recoverySubscriptions.ContainsKey(session)) continue;
            try { _recoverySubscriptions[session] = await SubscribeSessionAsync(session, true, budget.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { break; }
            catch (Exception exception) when (IsRecoverable(exception) && exception is not OperationCanceledException)
            { logger.LogDebug("SMTC recovery subscription unavailable ({Category}).", exception.GetType().Name); }
        }
    }

    private void RetireRecoverySessions()
    {
        Volatile.Write(ref _recoverySessions, []);
        foreach (var subscription in _recoverySubscriptions.Values) subscription.Retire();
        _recoverySubscriptions.Clear();
    }

    private async Task<MediaEventSubscription> SubscribeManagerAsync(GlobalSystemMediaTransportControlsSessionManager manager, CancellationToken token)
    {
        var subscription = new MediaEventSubscription();
        Windows.Foundation.TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, CurrentSessionChangedEventArgs> current =
            (sender, args) => { if (subscription.IsActive) OnCurrentSessionChanged(sender, args); };
        Windows.Foundation.TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, SessionsChangedEventArgs> sessions =
            (sender, args) => { if (subscription.IsActive) OnSessionsChanged(sender, args); };
        await subscription.StartAsync(_subscriptions, manager,
            () => { manager.CurrentSessionChanged += current; manager.SessionsChanged += sessions; },
            () => { Unsubscribe(() => manager.CurrentSessionChanged -= current); Unsubscribe(() => manager.SessionsChanged -= sessions); },
            OperationTimeout, token);
        return subscription;
    }

    private async Task<MediaEventSubscription> SubscribeSessionAsync(GlobalSystemMediaTransportControlsSession session, bool recovery, CancellationToken token)
    {
        var subscription = new MediaEventSubscription();
        Windows.Foundation.TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs> media =
            (sender, args) => { if (subscription.IsActive) { if (recovery) OnRecoveryMediaPropertiesChanged(sender, args); else OnMediaPropertiesChanged(sender, args); } };
        Windows.Foundation.TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs> playback =
            (sender, args) => { if (subscription.IsActive) { if (recovery) OnRecoveryPlaybackInfoChanged(sender, args); else OnPlaybackInfoChanged(sender, args); } };
        Windows.Foundation.TypedEventHandler<GlobalSystemMediaTransportControlsSession, TimelinePropertiesChangedEventArgs> timeline =
            (sender, args) => { if (subscription.IsActive) { if (recovery) OnRecoveryTimelinePropertiesChanged(sender, args); else OnTimelinePropertiesChanged(sender, args); } };
        await subscription.StartAsync(_subscriptions, session,
            () => { session.MediaPropertiesChanged += media; session.PlaybackInfoChanged += playback; session.TimelinePropertiesChanged += timeline; },
            () => { Unsubscribe(() => session.MediaPropertiesChanged -= media); Unsubscribe(() => session.PlaybackInfoChanged -= playback); Unsubscribe(() => session.TimelinePropertiesChanged -= timeline); },
            TimeSpan.FromMilliseconds(500), token);
        return subscription;
    }

    private void Unsubscribe(Action remove)
    {
        try { remove(); }
        catch (Exception exception) when (IsRecoverable(exception))
        { logger.LogDebug("SMTC subscription cleanup failed ({Category}).", exception.GetType().Name); }
    }

    private void OnRecoveryMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        if (!IsRecoverySession(sender)) return;
        if (!ReferenceEquals(sender, _session)) RequestRefresh();
        else if (_sessionSubscription?.IsActive != true) InvalidateMetadata();
    }
    private void OnRecoveryPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    { if (IsRecoverySession(sender) && (!ReferenceEquals(sender, _session) || _sessionSubscription?.IsActive != true)) RequestRefresh(); }
    private void OnRecoveryTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    { if (IsRecoverySession(sender) && (!ReferenceEquals(sender, _session) || _sessionSubscription?.IsActive != true)) RequestRefresh(); }

    private bool IsRecoverySession(GlobalSystemMediaTransportControlsSession session) =>
        Volatile.Read(ref _recoverySessions).Any(value => ReferenceEquals(value, session));

    private void RequestRefresh() => _refresh?.Writer.TryWrite(true);
    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) => OnManagerChanged(sender);
    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args) => OnManagerChanged(sender);
    private void OnManagerChanged(GlobalSystemMediaTransportControlsSessionManager sender)
    {
        if (!ReferenceEquals(sender, _manager)) return;
        InvalidateMetadata();
    }
    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, _session)) return;
        InvalidateMetadata();
    }
    internal void InvalidateMetadata()
    {
        Interlocked.Increment(ref _metadataRevision);
        RequestRefresh();
    }
    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, _session)) return;
        RequestRefresh();
    }
    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        if (!ReferenceEquals(sender, _session)) return;
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

    private static bool IsRecoverable(Exception exception) => exception is COMException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException or TimeoutException or IOException or ArgumentException;
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
