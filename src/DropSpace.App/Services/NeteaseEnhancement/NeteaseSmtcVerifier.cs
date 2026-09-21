using System.Runtime.InteropServices;
using System.Threading.Channels;
using DropSpace.Core.Media;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DropSpace.App.Services.NeteaseEnhancement;

/// <summary>Short-lived, event-driven verification of the player's public Windows contract.</summary>
public sealed class NeteaseSmtcVerifier : IDisposable
{
    private static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(8);
    private readonly Func<CancellationToken, Task<IProbeManager>> _createManager;
    private readonly TimeProvider _time;
    private readonly TimeSpan _controlTimeout;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private IProbeManager? _manager;
    private bool _disposed;
    internal Action<string>? Diagnostic { get; set; }

    public NeteaseSmtcVerifier() : this(CreateManagerAsync, TimeProvider.System, ControlTimeout) { }
    internal NeteaseSmtcVerifier(Func<CancellationToken, Task<IProbeManager>> createManager,
        TimeProvider time, TimeSpan controlTimeout)
    { _createManager = createManager; _time = time; _controlTimeout = controlTimeout; }

    public async Task<NeteaseMediaCapabilities> VerifyAsync(TimeSpan timeout, bool exerciseControls,
        CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;
        var evidence = NeteaseMediaCapabilities.Empty;
        var changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
            { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
        void Signal() => changes.Writer.TryWrite(true);
        var samples = new Dictionary<object, ProgressSample>();
        var playAttempts = new Dictionary<object, int>();
        IProbeSession? controlled = null;
        ProbeSnapshot? original = null;
        var mutationsStarted = false;
        // Connect before the capability-failure boundary. A broken Windows
        // connection is an infrastructure error, not proof a plugin is needed.
        var manager = await GetManagerAsync(token).ConfigureAwait(false);
        try
        {
            using var managerEvents = manager.Subscribe(Signal);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var sessions = new List<IProbeSession>();
                try
                {
                    foreach (var session in manager.GetSessions().Take(512))
                    {
                        try { if (IsNeteaseSource(session.Source)) sessions.Add(session); }
                        catch (Exception exception) when (IsRecoverable(exception)) { Diagnostic?.Invoke("SourceUnavailable"); }
                        if (sessions.Count == 16) break;
                    }
                }
                catch (Exception exception) when (IsRecoverable(exception))
                {
                    Diagnostic?.Invoke("DiscoveryUnavailable");
                    // A terminated player's stale COM object can outlive its process.
                    // Keep the manager subscription alive until discovery changes.
                    evidence = NeteaseMediaCapabilities.Empty;
                    await changes.Reader.ReadAsync(token).ConfigureAwait(false);
                    continue;
                }
                foreach (var removed in samples.Keys.Where(key => !sessions.Any(session => Equals(session.Identity, key))).ToArray())
                    samples.Remove(removed);
                var subscriptions = new List<IDisposable>();
                try
                {
                    // Observe every candidate, including a complete InfLink session behind
                    // an incomplete native session. Source/AUMID is not session identity.
                    var candidates = new List<(IProbeSession Session, ProbeSnapshot Snapshot, NeteaseMediaCapabilities Evidence)>();
                    foreach (var session in sessions)
                    {
                        try
                        {
                            subscriptions.Add(session.Subscribe(Signal));
                            using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                            readDeadline.CancelAfter(TimeSpan.FromSeconds(2));
                            var snapshot = await session.ReadAsync(readDeadline.Token).ConfigureAwait(false);
                            var live = ObserveProgress(samples, session.Identity, snapshot);
                            candidates.Add((session, snapshot, snapshot.Capabilities with { LiveProgress = live }));
                        }
                        catch (Exception exception) when (IsRecoverable(exception))
                        { Diagnostic?.Invoke("CandidateUnavailable"); samples.Remove(session.Identity); }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested)
                        { Diagnostic?.Invoke("CandidateReadTimeout"); samples.Remove(session.Identity); }
                    }
                    var candidate = candidates.OrderByDescending(value => Ready(value.Evidence, exerciseControls))
                        .ThenByDescending(value => Ready(value.Evidence with { LiveProgress = true }, exerciseControls))
                        .ThenByDescending(value => Score(value.Evidence)).FirstOrDefault();
                    if (candidate.Session is not null)
                    {
                        if (controlled is not null && !Equals(controlled.Identity, candidate.Session.Identity))
                        {
                            if (original is null || !await RestoreAsync(controlled, original).ConfigureAwait(false))
                                throw new InvalidOperationException("MediaVerificationRestoreFailed");
                            controlled = null;
                            original = null;
                            mutationsStarted = false;
                        }
                        evidence = candidate.Evidence;
                        if (Ready(evidence, exerciseControls))
                        {
                            if (exerciseControls)
                            {
                                controlled ??= candidate.Session;
                                original ??= candidate.Snapshot;
                                // Opened sessions may publish mode values only after
                                // Play. Capture them before testing any mode changes.
                                original = original with
                                {
                                    Shuffle = original.Shuffle ?? candidate.Snapshot.Shuffle,
                                    Repeat = original.Repeat ?? candidate.Snapshot.Repeat,
                                    Capabilities = original.Capabilities with
                                    {
                                        Shuffle = candidate.Snapshot.Capabilities.Shuffle,
                                        Repeat = candidate.Snapshot.Capabilities.Repeat,
                                    },
                                };
                                mutationsStarted = true;
                                await ExerciseAsync(controlled, candidate.Snapshot, token).ConfigureAwait(false);
                                // Enabled flags are state-specific; only the completed
                                // pause/play round trip proves both operations.
                                evidence = evidence with { Play = true, Pause = true };
                            }
                            break;
                        }
                        if (exerciseControls && evidence.Play && !candidate.Snapshot.Playing &&
                            evidence.Timeline && evidence.Seek && HasTrack(candidate.Snapshot) &&
                            playAttempts.GetValueOrDefault(candidate.Session.Identity) < 3)
                        {
                            controlled ??= candidate.Session;
                            original ??= candidate.Snapshot;
                            mutationsStarted = true;
                            playAttempts[candidate.Session.Identity] = playAttempts.GetValueOrDefault(candidate.Session.Identity) + 1;
                            // Do not consume an old queued notification as permission
                            // to retry a rejected command without a new player event.
                            while (changes.Reader.TryRead(out _)) { }
                            try
                            {
                                await CommandAsync(controlled, ProbeCommand.Play, 0,
                                    snapshot => snapshot.Playing, token).ConfigureAwait(false);
                                Diagnostic?.Invoke("AutoPlayObserved");
                            }
                            catch (OperationCanceledException) when (!token.IsCancellationRequested)
                            { Diagnostic?.Invoke("AutoPlayTimeout"); }
                            catch (Exception exception) when (IsRecoverable(exception))
                            { Diagnostic?.Invoke(exception is COMException ? "AutoPlaySessionUnavailable" : "AutoPlayRejected"); }
                            samples.Remove(controlled.Identity); // playback commands cannot themselves prove a live clock
                        }
                    }
                    else evidence = NeteaseMediaCapabilities.Empty;
                    await changes.Reader.ReadAsync(token).ConfigureAwait(false);
                }
                finally
                {
                    DisposeSubscriptions(subscriptions);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { Diagnostic?.Invoke("VerificationTimeout"); evidence = evidence with { LiveProgress = false }; }
        catch (Exception exception) when (IsRecoverable(exception))
        { Report(exception is COMException ? "VerificationSessionUnavailable" : "VerificationRejected", exception); evidence = NeteaseMediaCapabilities.Empty; }
        finally
        {
            if (mutationsStarted && controlled is not null && original is not null &&
                !await RestoreAsync(controlled, original).ConfigureAwait(false))
            {
                Diagnostic?.Invoke("RestorationFailed");
                evidence = NeteaseMediaCapabilities.Empty;
            }
            changes.Writer.TryComplete();
        }
        cancellationToken.ThrowIfCancellationRequested();
        return evidence;
    }

    private async Task<IProbeManager> GetManagerAsync(CancellationToken token)
    {
        await _connectionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_manager is not null) return _manager;
            try { return _manager = await _createManager(token).ConfigureAwait(false); }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                Report("ManagerConnectionUnavailable", exception);
                throw new InvalidOperationException("MediaVerificationConnectionUnavailable", exception);
            }
        }
        finally { _connectionGate.Release(); }
    }

    private void Report(string category, Exception exception) => Diagnostic?.Invoke(
        $"{category};HRESULT=0x{exception.HResult:X8};Operation={exception.TargetSite?.Name ?? "Unknown"}");

    public void Dispose()
    {
        _connectionGate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            if (_manager is IDisposable disposable) disposable.Dispose();
            _manager = null;
        }
        finally { _connectionGate.Release(); }
    }

    private bool ObserveProgress(Dictionary<object, ProgressSample> samples, object identity, ProbeSnapshot snapshot)
    {
        var now = _time.GetTimestamp();
        if (!snapshot.Capabilities.Timeline || !samples.TryGetValue(identity, out var previous) ||
            !SameTrack(snapshot, previous.Snapshot))
        {
            samples[identity] = new(snapshot, now, false);
            return false;
        }
        if (previous.Live) return true;
        var elapsed = _time.GetElapsedTime(previous.Observed, now);
        var advance = snapshot.Position - previous.Snapshot.Position;
        var live = snapshot.Playing && previous.Snapshot.Playing && advance > TimeSpan.Zero &&
            elapsed >= TimeSpan.FromMilliseconds(100) && advance.TotalSeconds <= elapsed.TotalSeconds * 2 + 0.25 &&
            snapshot.Updated > previous.Snapshot.Updated;
        if (live || snapshot.Position != previous.Snapshot.Position || snapshot.Playing != previous.Snapshot.Playing)
            samples[identity] = new(snapshot, now, live);
        return live;
    }

    private async Task ExerciseAsync(IProbeSession session, ProbeSnapshot track, CancellationToken token)
    {
        await CommandAsync(session, ProbeCommand.Pause, 0, snapshot => !snapshot.Playing && SameTrack(snapshot, track), token).ConfigureAwait(false);
        await CommandAsync(session, ProbeCommand.Play, 0, snapshot => snapshot.Playing && SameTrack(snapshot, track), token).ConfigureAwait(false);
        await CommandAsync(session, ProbeCommand.Pause, 0, snapshot => !snapshot.Playing && SameTrack(snapshot, track), token).ConfigureAwait(false);
        var beforeSeek = await session.ReadAsync(token).ConfigureAwait(false);
        Require(SameTrack(beforeSeek, track));
        var seek = beforeSeek.Position + TimeSpan.FromSeconds(3) <= beforeSeek.End
            ? beforeSeek.Position + TimeSpan.FromSeconds(3) : beforeSeek.Position - TimeSpan.FromSeconds(3);
        Require(seek >= beforeSeek.Start && seek <= beforeSeek.End);
        await CommandAsync(session, ProbeCommand.Seek, seek.Ticks,
            snapshot => SameTrack(snapshot, track) && Math.Abs((snapshot.Position - seek).TotalMilliseconds) <= 250, token).ConfigureAwait(false);
        await CommandAsync(session, ProbeCommand.Shuffle, track.Shuffle == true ? 0 : 1,
            snapshot => snapshot.Shuffle == !track.Shuffle && SameTrack(snapshot, track), token).ConfigureAwait(false);
        await CommandAsync(session, ProbeCommand.Shuffle, 0,
            snapshot => snapshot.Shuffle == false && SameTrack(snapshot, track), token).ConfigureAwait(false);
        var repeat = track.Repeat == MediaPlaybackAutoRepeatMode.Track ? MediaPlaybackAutoRepeatMode.List : MediaPlaybackAutoRepeatMode.Track;
        await CommandAsync(session, ProbeCommand.Repeat, (long)repeat,
            snapshot => snapshot.Repeat == repeat && SameTrack(snapshot, track), token).ConfigureAwait(false);
        // Remove single-track repeat before navigation. Accepted commands are not
        // evidence of next/previous: observe a different identity and its return.
        await CommandAsync(session, ProbeCommand.Repeat, (long)MediaPlaybackAutoRepeatMode.List,
            snapshot => snapshot.Repeat == MediaPlaybackAutoRepeatMode.List && SameTrack(snapshot, track), token).ConfigureAwait(false);
        await CommandAsync(session, ProbeCommand.Next, 0, snapshot => HasTrack(snapshot) && !SameTrack(snapshot, track), token).ConfigureAwait(false);
        await CommandAsync(session, ProbeCommand.Previous, 0, snapshot => SameTrack(snapshot, track), token).ConfigureAwait(false);
    }

    private async Task<bool> RestoreAsync(IProbeSession session, ProbeSnapshot original)
    {
        // Each bounded restoration is attempted independently. A failed preference
        // write must not prevent restoration of the user's paused state.
        var restored = await RestoreCommandAsync(session, original.Playing ? ProbeCommand.Play : ProbeCommand.Pause, 0,
            snapshot => snapshot.Playing == original.Playing).ConfigureAwait(false);
        if (original.Capabilities.Shuffle && original.Shuffle.HasValue)
            restored &= await RestoreCommandAsync(session, ProbeCommand.Shuffle, 0, snapshot => snapshot.Shuffle == false).ConfigureAwait(false);
        if (original.Capabilities.Repeat && original.Repeat is { } repeat)
            restored &= await RestoreCommandAsync(session, ProbeCommand.Repeat, (long)repeat, snapshot => snapshot.Repeat == repeat).ConfigureAwait(false);
        if (original.Capabilities.Shuffle && original.Shuffle is { } shuffle)
            restored &= await RestoreCommandAsync(session, ProbeCommand.Shuffle, shuffle ? 1 : 0, snapshot => snapshot.Shuffle == shuffle).ConfigureAwait(false);
        try
        {
            using var deadline = new CancellationTokenSource(_controlTimeout);
            var current = await session.ReadAsync(deadline.Token).ConfigureAwait(false);
            restored &= current.Playing == original.Playing &&
                (!original.Capabilities.Shuffle || current.Shuffle == original.Shuffle) &&
                (!original.Capabilities.Repeat || current.Repeat == original.Repeat);
            // Starting a paused session may be what first creates its metadata.
            // With no original track/timeline there is no saved position to apply.
            if (!HasTrack(original) || !original.Capabilities.Timeline || !original.Capabilities.Seek) return restored;
            // Skip/Previous may have been accepted without returning to the original
            // song. Never apply that song's saved position to a different song.
            if (!SameTrack(current, original) || original.Position < current.Start || original.Position > current.End)
                return false;
            restored &= await RestoreCommandAsync(session, ProbeCommand.Seek, original.Position.Ticks,
                snapshot => SameTrack(snapshot, original) && Math.Abs((snapshot.Position - original.Position).TotalMilliseconds) <= 250).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsRecoverable(exception) || exception is OperationCanceledException) { restored = false; }
        return restored;
    }

    private async Task<bool> RestoreCommandAsync(IProbeSession session, ProbeCommand command, long value, Func<ProbeSnapshot, bool> predicate)
    {
        try
        {
            using var deadline = new CancellationTokenSource(_controlTimeout);
            if (predicate(await session.ReadAsync(deadline.Token).ConfigureAwait(false))) return true;
            await CommandAsync(session, command, value, predicate, deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (IsRecoverable(exception) || exception is OperationCanceledException) { return false; }
    }

    private async Task CommandAsync(IProbeSession session, ProbeCommand command, long value,
        Func<ProbeSnapshot, bool> predicate, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(_controlTimeout);
        var changed = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        using var subscription = session.Subscribe(() => changed.Writer.TryWrite(true));
        // Subscribe before dispatch so synchronous completion events cannot be lost.
        Require(await session.CommandAsync(command, value, deadline.Token).ConfigureAwait(false));
        changed.Writer.TryWrite(true);
        await foreach (var _ in changed.Reader.ReadAllAsync(deadline.Token).ConfigureAwait(false))
            if (predicate(await session.ReadAsync(deadline.Token).ConfigureAwait(false))) return;
    }

    internal static bool IsNetease(GlobalSystemMediaTransportControlsSession session) => IsNeteaseSource(session.SourceAppUserModelId);
    internal static bool IsNeteaseSource(string source)
    {
        var name = Path.GetFileName(source);
        return name.Equals("cloudmusic.exe", StringComparison.OrdinalIgnoreCase) ||
            source.Equals("cloudmusic", StringComparison.OrdinalIgnoreCase) ||
            source.Equals("com.netease.cloudmusic", StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith("NetEase.CloudMusic_", StringComparison.OrdinalIgnoreCase) ||
            source.Equals("InfLink", StringComparison.OrdinalIgnoreCase) ||
            source.Equals("InfLink-rs", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRecoverable(Exception exception) => exception is COMException or IOException or
        InvalidOperationException or UnauthorizedAccessException or ArgumentException;
    private static bool Ready(NeteaseMediaCapabilities value, bool exerciseControls) => exerciseControls
        ? (value with { Play = true, Pause = true }).Complete && (value.Play || value.Pause)
        : value.Complete;
    private static void Require(bool accepted) { if (!accepted) throw new InvalidOperationException("MediaVerificationRejected"); }
    private static void DisposeSubscriptions(List<IDisposable> subscriptions)
    {
        foreach (var subscription in subscriptions)
        {
            try { subscription.Dispose(); }
            // A disconnected COM event source cannot be detached successfully;
            // still release every remaining lease and await new session events.
            catch (Exception exception) when (IsRecoverable(exception)) { }
        }
    }
    private static bool HasTrack(ProbeSnapshot value) => !string.IsNullOrWhiteSpace(value.TrackKey);
    private static bool SameTrack(ProbeSnapshot left, ProbeSnapshot right) => HasTrack(left) && left.TrackKey == right.TrackKey;
    private static int Score(NeteaseMediaCapabilities value) => new[] { value.Title, value.Artist, value.Album, value.Artwork,
        value.PlaybackState, value.Play, value.Pause, value.Previous, value.Next, value.Timeline, value.LiveProgress, value.Seek, value.Shuffle, value.Repeat }.Count(flag => flag);
    private sealed record ProgressSample(ProbeSnapshot Snapshot, long Observed, bool Live);

    internal enum ProbeCommand { Play, Pause, Seek, Shuffle, Repeat, Next, Previous }
    internal sealed record ProbeSnapshot(string TrackKey, bool Playing, TimeSpan Start, TimeSpan End,
        TimeSpan Position, DateTimeOffset Updated, bool? Shuffle, MediaPlaybackAutoRepeatMode? Repeat,
        NeteaseMediaCapabilities Capabilities);
    internal interface IProbeSession
    {
        object Identity { get; }
        string Source { get; }
        IDisposable Subscribe(Action changed);
        Task<ProbeSnapshot> ReadAsync(CancellationToken token);
        Task<bool> CommandAsync(ProbeCommand command, long value, CancellationToken token);
    }
    internal interface IProbeManager
    {
        IReadOnlyList<IProbeSession> GetSessions();
        IDisposable Subscribe(Action changed);
    }
    private static async Task<IProbeManager> CreateManagerAsync(CancellationToken token) =>
        new WindowsProbeManager(await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(token).ConfigureAwait(false));

    private sealed class WindowsProbeManager : IProbeManager, IDisposable
    {
        private readonly GlobalSystemMediaTransportControlsSessionManager _manager;
        private event Action? Changed;
        public WindowsProbeManager(GlobalSystemMediaTransportControlsSessionManager manager)
        {
            _manager = manager;
            // Keep the valid Windows connection/event registration across the
            // install restart. Per-probe subscriptions below are managed only.
            _manager.SessionsChanged += OnSessionsChanged;
        }
        private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager _, SessionsChangedEventArgs __) => Changed?.Invoke();
        public IReadOnlyList<IProbeSession> GetSessions() => _manager.GetSessions().Take(512).Select(session => (IProbeSession)new WindowsProbeSession(session)).ToArray();
        public IDisposable Subscribe(Action changed)
        {
            Changed += changed;
            return new Subscription(() => Changed -= changed);
        }
        public void Dispose() { Changed = null; _manager.SessionsChanged -= OnSessionsChanged; }
    }
    private sealed class WindowsProbeSession(GlobalSystemMediaTransportControlsSession session) : IProbeSession
    {
        public object Identity => session;
        public string Source => session.SourceAppUserModelId;
        public IDisposable Subscribe(Action changed)
        {
            void Playback(GlobalSystemMediaTransportControlsSession _, PlaybackInfoChangedEventArgs __) => changed();
            void Timeline(GlobalSystemMediaTransportControlsSession _, TimelinePropertiesChangedEventArgs __) => changed();
            void Metadata(GlobalSystemMediaTransportControlsSession _, MediaPropertiesChangedEventArgs __) => changed();
            var playback = false; var timeline = false; var metadata = false;
            var lease = new Subscription(() =>
            {
                try { if (playback) session.PlaybackInfoChanged -= Playback; }
                finally
                {
                    try { if (timeline) session.TimelinePropertiesChanged -= Timeline; }
                    finally { if (metadata) session.MediaPropertiesChanged -= Metadata; }
                }
            });
            try
            {
                session.PlaybackInfoChanged += Playback; playback = true;
                session.TimelinePropertiesChanged += Timeline; timeline = true;
                session.MediaPropertiesChanged += Metadata; metadata = true;
                return lease;
            }
            catch { lease.Dispose(); throw; }
        }
        public async Task<ProbeSnapshot> ReadAsync(CancellationToken token)
        {
            var metadata = await session.TryGetMediaPropertiesAsync().AsTask(token).ConfigureAwait(false);
            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            var controls = playback.Controls;
            var valid = timeline.EndTime > timeline.StartTime && timeline.Position >= timeline.StartTime &&
                timeline.Position <= timeline.EndTime && timeline.LastUpdatedTime.Year >= 2000 &&
                timeline.LastUpdatedTime <= DateTimeOffset.UtcNow.AddSeconds(2);
            var key = string.IsNullOrWhiteSpace(metadata.Title) ? string.Empty :
                string.Join('\u001f', metadata.Title, metadata.Artist, metadata.AlbumTitle,
                    string.Join('\u001e', metadata.Genres.Where(value => value.StartsWith("NCM-", StringComparison.Ordinal))),
                    (timeline.EndTime - timeline.StartTime).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var capabilities = new NeteaseMediaCapabilities(!string.IsNullOrWhiteSpace(metadata.Title), !string.IsNullOrWhiteSpace(metadata.Artist),
                !string.IsNullOrWhiteSpace(metadata.AlbumTitle), await HasArtworkAsync(metadata.Thumbnail, token).ConfigureAwait(false),
                playback.PlaybackStatus is GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
                controls.IsPlayEnabled, controls.IsPauseEnabled, controls.IsPreviousEnabled, controls.IsNextEnabled, valid, false,
                controls.IsPlaybackPositionEnabled, controls.IsShuffleEnabled && playback.IsShuffleActive.HasValue,
                controls.IsRepeatEnabled && playback.AutoRepeatMode.HasValue);
            return new(key, playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                timeline.StartTime, timeline.EndTime, timeline.Position, timeline.LastUpdatedTime,
                playback.IsShuffleActive, playback.AutoRepeatMode, capabilities);
        }
        public Task<bool> CommandAsync(ProbeCommand command, long value, CancellationToken token) => (command switch
        {
            ProbeCommand.Play => session.TryPlayAsync(),
            ProbeCommand.Pause => session.TryPauseAsync(),
            ProbeCommand.Seek => session.TryChangePlaybackPositionAsync(value),
            ProbeCommand.Shuffle => session.TryChangeShuffleActiveAsync(value != 0),
            ProbeCommand.Repeat => session.TryChangeAutoRepeatModeAsync((MediaPlaybackAutoRepeatMode)value),
            ProbeCommand.Next => session.TrySkipNextAsync(),
            ProbeCommand.Previous => session.TrySkipPreviousAsync(),
            _ => throw new ArgumentOutOfRangeException(nameof(command)),
        }).AsTask(token);
    }
    private static async Task<bool> HasArtworkAsync(IRandomAccessStreamReference? reference, CancellationToken token)
    {
        if (reference is null) return false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var stream = await reference.OpenReadAsync().AsTask(deadline.Token).ConfigureAwait(false);
            if (stream.Size is 0 or > 4 * 1024 * 1024) return false;
            using var reader = new DataReader(stream);
            return await reader.LoadAsync(1).AsTask(deadline.Token).ConfigureAwait(false) == 1;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
        catch (Exception exception) when (IsRecoverable(exception)) { return false; }
    }
    private sealed class Subscription(Action remove) : IDisposable
    {
        private Action? _remove = remove;
        public void Dispose() => Interlocked.Exchange(ref _remove, null)?.Invoke();
    }
}
