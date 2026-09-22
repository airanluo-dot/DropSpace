using System.Runtime.InteropServices;
using System.Threading.Channels;
using DropSpace.Core.Media;
using Windows.Media.Control;
using Microsoft.UI.Dispatching;
using Windows.Storage.Streams;

namespace DropSpace.App.Services.NeteaseEnhancement;

/// <summary>Short-lived, event-driven verification of the player's public Windows contract.</summary>
public sealed class NeteaseSmtcVerifier : IDisposable
{
    // SMTC position notifications are asynchronous and the product displays seconds.
    // The live player reports sub-second seek offsets; require the requested jump
    // within one display unit, not an undocumented 250 ms precision guarantee.
    private static readonly TimeSpan SeekTolerance = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(8);
    private readonly Func<CancellationToken, Task<IProbeManager>> _createManager;
    private readonly TimeProvider _time;
    private readonly DispatcherQueue? _dispatcher;
    private readonly TimeSpan _controlTimeout;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private IProbeManager? _manager;
    private readonly HashSet<object> _retired = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<object> _lastDiscovered = new(ReferenceEqualityComparer.Instance);
    private bool _disposed;
    internal Action<string>? Diagnostic { get; set; }

    public NeteaseSmtcVerifier(DispatcherQueue dispatcher) : this(CreateManagerAsync, TimeProvider.System, ControlTimeout)
    { _dispatcher = dispatcher; }
    internal NeteaseSmtcVerifier(Func<CancellationToken, Task<IProbeManager>> createManager,
        TimeProvider time, TimeSpan controlTimeout)
    { _createManager = createManager; _time = time; _controlTimeout = controlTimeout; }

    // The manager outlives player processes; session COM objects do not. Capture
    // the old generation before stopping the player, never dispatch to it again.
    public Task InvalidateBeforeRestartAsync(CancellationToken token = default) =>
        _dispatcher is not null && !_dispatcher.HasThreadAccess
            ? _dispatcher.EnqueueAsync(() => InvalidateCoreAsync(token)) : InvalidateCoreAsync(token);

    private async Task InvalidateCoreAsync(CancellationToken token)
    {
        var manager = await GetManagerAsync(token);
        _retired.Clear();
        _retired.UnionWith(_lastDiscovered);
        _lastDiscovered.Clear();
        try
        {
            foreach (var session in manager.GetSessions().Take(512))
            {
                try { if (IsNeteaseSource(session.Source)) _retired.Add(session.Identity); }
                catch (Exception exception) when (IsRecoverable(exception)) { _retired.Add(session.Identity); Report("Discovery.BeforeRestart", exception); }
            }
        }
        catch (Exception exception) when (IsRecoverable(exception)) { Report("Discovery.BeforeRestart", exception); }
        Diagnostic?.Invoke("SessionGenerationRetired");
    }

    public Task<NeteaseMediaCapabilities> VerifyAsync(TimeSpan timeout, bool exerciseControls,
        CancellationToken cancellationToken = default) =>
        _dispatcher is not null && !_dispatcher.HasThreadAccess
            ? _dispatcher.EnqueueAsync(() => VerifyCoreAsync(timeout, exerciseControls, cancellationToken))
            : VerifyCoreAsync(timeout, exerciseControls, cancellationToken);

    // Preserve this context across awaits: native async operations and session event
    // registration belong to the same apartment throughout discovery and control.
    // All waits and artwork reads remain asynchronous; deployment never runs here.
    private async Task<NeteaseMediaCapabilities> VerifyCoreAsync(TimeSpan timeout, bool exerciseControls,
        CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var token = deadline.Token;
        var evidence = NeteaseMediaCapabilities.Empty;
        var changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
        void Signal() => changes.Writer.TryWrite(true);
        var samples = new Dictionary<object, ProgressSample>(ReferenceEqualityComparer.Instance);
        var playAttempts = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        var invalid = new HashSet<object>(_retired, ReferenceEqualityComparer.Instance);
        var rediscovered = new HashSet<object>(ReferenceEqualityComparer.Instance);
        IProbeSession? controlled = null;
        ProbeSnapshot? original = null;
        var mutationsStarted = false;
        var discoveryFailures = 0;
        void Retire(IProbeSession session, Exception? error = null)
        {
            if (error is null || IsDisconnected(error)) invalid.Add(session.Identity);
            if (rediscovered.Add(session.Identity)) Signal(); // immediate rediscovery once, then await real events
            samples.Remove(session.Identity);
            if (controlled is not null && ReferenceEquals(controlled.Identity, session.Identity))
            {
                controlled = null; original = null; mutationsStarted = false;
            }
            Diagnostic?.Invoke("StaleSessionDiscarded");
        }
        // Connect before the capability-failure boundary. A broken Windows
        // connection is an infrastructure error, not proof a plugin is needed.
        var manager = await GetManagerAsync(token);
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
                        try { if (!invalid.Contains(session.Identity) && IsNeteaseSource(session.Source)) sessions.Add(session); }
                        catch (Exception exception) when (IsRecoverable(exception)) { Report("Discovery.Source", exception); Retire(session, exception); }
                        if (sessions.Count == 16) break;
                    }
                }
                catch (Exception exception) when (IsRecoverable(exception))
                {
                    Report("Discovery", exception);
                    if (controlled is not null) Retire(controlled);
                    if (++discoveryFailures == 1) continue;
                    // A terminated player's stale COM object can outlive its process.
                    // Keep the manager subscription alive until discovery changes.
                    evidence = NeteaseMediaCapabilities.Empty;
                    await changes.Reader.ReadAsync(token);
                    continue;
                }
                discoveryFailures = 0;
                _lastDiscovered.Clear();
                _lastDiscovered.UnionWith(sessions.Select(session => session.Identity));
                if (controlled is not null && !sessions.Any(session => ReferenceEquals(session.Identity, controlled.Identity)))
                    Retire(controlled);
                foreach (var removed in samples.Keys.Where(key => !sessions.Any(session => ReferenceEquals(session.Identity, key))).ToArray())
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
                            subscriptions.Add(Subscribe(session, Signal, "Subscribe.Candidate"));
                            using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                            readDeadline.CancelAfter(TimeSpan.FromSeconds(2));
                            var snapshot = await ReadAsync(session, readDeadline.Token, "Read.Candidate");
                            var live = ObserveProgress(samples, session.Identity, snapshot);
                            candidates.Add((session, snapshot, snapshot.Capabilities with { LiveProgress = live }));
                        }
                        catch (Exception exception) when (IsRecoverable(exception))
                        { Retire(session, exception); }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested)
                        { Diagnostic?.Invoke("CandidateReadTimeout"); samples.Remove(session.Identity); }
                    }
                    var candidate = candidates.OrderByDescending(value => Ready(value.Evidence, exerciseControls))
                        .ThenByDescending(value => Ready(value.Evidence with { LiveProgress = true }, exerciseControls))
                        .ThenByDescending(value => Score(value.Evidence)).FirstOrDefault();
                    if (candidate.Session is not null)
                    {
                        if (controlled is not null && !ReferenceEquals(controlled.Identity, candidate.Session.Identity))
                        {
                            // A healthy superseded session may be restored. A disconnected
                            // object is discarded; never make recovery of that object a gate
                            // for binding the new process's session.
                            if (original is not null)
                            {
                                try { await RestoreAsync(controlled, original); }
                                catch (Exception exception) when (IsStale(exception)) { Retire(controlled, exception); }
                            }
                            controlled = null;
                            original = null;
                            mutationsStarted = false;
                        }
                        evidence = candidate.Evidence;
                        if (Ready(evidence, exerciseControls))
                        {
                            if (exerciseControls)
                            {
                                controlled = candidate.Session;
                                original ??= candidate.Snapshot;
                                mutationsStarted = true;
                                try
                                {
                                    await ExerciseAsync(controlled, candidate.Snapshot, token);
                                    Require(await RestoreAsync(controlled, original));
                                    mutationsStarted = false;
                                }
                                catch (Exception exception) when (IsStale(exception))
                                {
                                    Retire(candidate.Session, exception);
                                    continue;
                                }
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
                            controlled = candidate.Session;
                            original ??= candidate.Snapshot;
                            mutationsStarted = true;
                            playAttempts[candidate.Session.Identity] = playAttempts.GetValueOrDefault(candidate.Session.Identity) + 1;
                            // Do not consume an old queued notification as permission
                            // to retry a rejected command without a new player event.
                            while (changes.Reader.TryRead(out _)) { }
                            try
                            {
                                await CommandAsync(controlled, ProbeCommand.Play, 0,
                                    snapshot => snapshot.Playing, token);
                                Diagnostic?.Invoke("AutoPlayObserved");
                            }
                            catch (OperationCanceledException) when (!token.IsCancellationRequested)
                            { Diagnostic?.Invoke("AutoPlayTimeout"); }
                            catch (Exception exception) when (IsStale(exception)) { Retire(candidate.Session, exception); continue; }
                            catch (Exception exception) when (IsRecoverable(exception))
                            { Report("AutoPlayRejected", exception); }
                            samples.Remove(candidate.Session.Identity); // playback commands cannot themselves prove a live clock
                        }
                    }
                    else evidence = NeteaseMediaCapabilities.Empty;
                    await changes.Reader.ReadAsync(token);
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
            if (mutationsStarted && controlled is not null && original is not null)
            {
                try
                {
                    if (!await RestoreAsync(controlled, original))
                    { Diagnostic?.Invoke("RestorationFailed"); evidence = NeteaseMediaCapabilities.Empty; }
                }
                catch (Exception exception) when (IsStale(exception))
                { Retire(controlled, exception); evidence = NeteaseMediaCapabilities.Empty; }
            }
            changes.Writer.TryComplete();
        }
        cancellationToken.ThrowIfCancellationRequested();
        return evidence;
    }

    private async Task<IProbeManager> GetManagerAsync(CancellationToken token)
    {
        await _connectionGate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_manager is not null) return _manager;
            try { return _manager = await _createManager(token); }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                Report("ManagerConnectionUnavailable", exception);
                throw new InvalidOperationException("MediaVerificationConnectionUnavailable", exception);
            }
        }
        finally { _connectionGate.Release(); }
    }

    private IDisposable Subscribe(IProbeSession session, Action changed, string phase)
    {
        try
        {
            var lease = session.Subscribe(changed);
            return new Subscription(() =>
            {
                try { lease.Dispose(); }
                catch (Exception exception) when (IsRecoverable(exception)) { Report("Unsubscribe", exception); }
            });
        }
        catch (Exception exception) when (IsRecoverable(exception)) { Report(phase, exception); throw; }
    }

    private async Task<ProbeSnapshot> ReadAsync(IProbeSession session, CancellationToken token, string phase)
    {
        try { return await session.ReadAsync(token); }
        catch (Exception exception) when (IsRecoverable(exception)) { Report(phase, exception); throw; }
    }

    private void Report(string category, Exception exception) => Diagnostic?.Invoke(
        $"{category};HRESULT=0x{exception.HResult:X8};Operation={exception.TargetSite?.Name ?? "Unknown"};NativeStage={exception.Data["SmtcStage"] ?? "Unspecified"}");

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
            elapsed >= TimeSpan.FromMilliseconds(100) && advance.TotalSeconds <= elapsed.TotalSeconds * 2 + 0.25;
        if (live || snapshot.Position != previous.Snapshot.Position || snapshot.Playing != previous.Snapshot.Playing)
            samples[identity] = new(snapshot, now, live);
        return live;
    }

    private async Task ExerciseAsync(IProbeSession session, ProbeSnapshot track, CancellationToken token)
    {
        await CommandAsync(session, ProbeCommand.Pause, 0, snapshot => !snapshot.Playing && SameTrack(snapshot, track), token);
        await CommandAsync(session, ProbeCommand.Play, 0, snapshot => snapshot.Playing && SameTrack(snapshot, track), token);
        await CommandAsync(session, ProbeCommand.Pause, 0, snapshot => !snapshot.Playing && SameTrack(snapshot, track), token);
        var beforeSeek = await ReadAsync(session, token, "Read.BeforeSeek");
        Require(SameTrack(beforeSeek, track));
        var seek = beforeSeek.Position + TimeSpan.FromSeconds(3) <= beforeSeek.End
            ? beforeSeek.Position + TimeSpan.FromSeconds(3) : beforeSeek.Position - TimeSpan.FromSeconds(3);
        Require(seek >= beforeSeek.Start && seek <= beforeSeek.End);
        await CommandAsync(session, ProbeCommand.Seek, seek.Ticks,
            snapshot => SameTrack(snapshot, track) && (snapshot.Position - seek).Duration() <= SeekTolerance, token);
        await CommandAsync(session, ProbeCommand.Next, 0, snapshot => HasTrack(snapshot) && !SameTrack(snapshot, track), token);
        await CommandAsync(session, ProbeCommand.Previous, 0, snapshot => SameTrack(snapshot, track), token);
    }

    private async Task<bool> RestoreAsync(IProbeSession session, ProbeSnapshot original)
    {
        // Metadata can return before the player's asynchronous track load finishes.
        // A seek accepted during that load can be overwritten by its initial position.
        // Complete one bounded event-observed retry before deciding restoration failed.
        if (await RestorePassAsync(session, original)) return true;
        Diagnostic?.Invoke("Restore.RetryAfterUnobservedState");
        return await RestorePassAsync(session, original);
    }

    private async Task<bool> RestorePassAsync(IProbeSession session, ProbeSnapshot original)
    {
        // Restore playback and position without changing the user's playback preferences.
        var restored = await RestoreCommandAsync(session, original.Playing ? ProbeCommand.Play : ProbeCommand.Pause, 0,
            snapshot => snapshot.Playing == original.Playing);
        try
        {
            using var deadline = new CancellationTokenSource(_controlTimeout);
            var current = await ReadAsync(session, deadline.Token, "Read.CommandOrRestore");
            restored &= current.Playing == original.Playing;
            // Starting a paused session may be what first creates its metadata.
            // With no original track/timeline there is no saved position to apply.
            if (!HasTrack(original) || !original.Capabilities.Timeline || !original.Capabilities.Seek) return restored;
            // Skip/Previous may have been accepted without returning to the original
            // song. Never apply that song's saved position to a different song.
            if (!SameTrack(current, original) || original.Position < current.Start || original.Position > current.End)
                return false;
            restored &= await RestoreCommandAsync(session, ProbeCommand.Seek, original.Position.Ticks,
                snapshot => SameTrack(snapshot, original) && (snapshot.Position - original.Position).Duration() <= SeekTolerance);
            // The load completing after Previous can resume playback after the
            // earlier pause. Restore state again after the observed seek.
            restored &= await RestoreCommandAsync(session, original.Playing ? ProbeCommand.Play : ProbeCommand.Pause, 0,
                snapshot => snapshot.Playing == original.Playing);
        }
        catch (Exception exception) when (!IsStale(exception) && (IsRecoverable(exception) || exception is OperationCanceledException)) { Report("Restore.Read", exception); restored = false; }
        return restored;
    }

    private async Task<bool> RestoreCommandAsync(IProbeSession session, ProbeCommand command, long value, Func<ProbeSnapshot, bool> predicate)
    {
        try
        {
            using var deadline = new CancellationTokenSource(_controlTimeout);
            if (predicate(await ReadAsync(session, deadline.Token, "Read.CommandOrRestore"))) return true;
            await CommandAsync(session, command, value, predicate, deadline.Token);
            return true;
        }
        catch (Exception exception) when (!IsStale(exception) && (IsRecoverable(exception) || exception is OperationCanceledException)) { Report("Restore." + command, exception); return false; }
    }

    private async Task CommandAsync(IProbeSession session, ProbeCommand command, long value,
        Func<ProbeSnapshot, bool> predicate, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(_controlTimeout);
        var changed = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        using var subscription = Subscribe(session, () => changed.Writer.TryWrite(true), "Subscribe.Command." + command);
        // Subscribe before dispatch so synchronous completion events cannot be lost.
        try { Require(await session.CommandAsync(command, value, deadline.Token)); }
        catch (Exception exception) when (IsRecoverable(exception)) { Report("Command." + command, exception); throw; }
        changed.Writer.TryWrite(true);
        ProbeSnapshot? last = null;
        try
        {
            await foreach (var _ in changed.Reader.ReadAllAsync(deadline.Token))
            {
                last = await ReadAsync(session, deadline.Token, "Read.CommandOrRestore");
                if (predicate(last)) { Diagnostic?.Invoke("Command.Observed." + command); return; }
            }
        }
        catch (OperationCanceledException)
        {
            Diagnostic?.Invoke($"Command.Timeout.{command};PositionDeltaMs={(command == ProbeCommand.Seek && last is not null ? (last.Position - TimeSpan.FromTicks(value)).TotalMilliseconds : 0):F0}");
            throw;
        }
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

    private static bool IsDisconnected(Exception exception) => exception.HResult is
        unchecked((int)0x80010108) or unchecked((int)0x800401FD) or unchecked((int)0x800706BA);
    private static bool IsStale(Exception exception) => exception is COMException or ObjectDisposedException ||
        exception is InvalidOperationException && exception.Message != "MediaVerificationRejected";
    private static bool IsRecoverable(Exception exception) => exception is COMException or IOException or
        InvalidOperationException or UnauthorizedAccessException or ArgumentException;
    private static bool Ready(NeteaseMediaCapabilities value, bool exerciseControls) => exerciseControls
        ? (value with { Play = true, Pause = true }).Complete && (value.Play || value.Pause)
        : value.Complete;
    private static void Require(bool accepted) { if (!accepted) throw new InvalidOperationException("MediaVerificationRejected"); }
    private void DisposeSubscriptions(List<IDisposable> subscriptions)
    {
        foreach (var subscription in subscriptions)
        {
            try { subscription.Dispose(); }
            // A disconnected COM event source cannot be detached successfully;
            // still release every remaining lease and await new session events.
            catch (Exception exception) when (IsRecoverable(exception)) { Report("Unsubscribe", exception); }
        }
    }
    private static bool HasTrack(ProbeSnapshot value) => !string.IsNullOrWhiteSpace(value.TrackKey);
    private static bool SameTrack(ProbeSnapshot left, ProbeSnapshot right) => HasTrack(left) && left.TrackKey == right.TrackKey;
    private static int Score(NeteaseMediaCapabilities value) => new[] { value.Title, value.Artist, value.Album, value.Artwork,
        value.PlaybackState, value.Play, value.Pause, value.Previous, value.Next, value.Timeline, value.LiveProgress, value.Seek }.Count(flag => flag);
    private sealed record ProgressSample(ProbeSnapshot Snapshot, long Observed, bool Live);

    internal enum ProbeCommand { Play, Pause, Seek, Next, Previous }
    internal sealed record ProbeSnapshot(string TrackKey, bool Playing, TimeSpan Start, TimeSpan End,
        TimeSpan Position, DateTimeOffset Updated,
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
        new WindowsProbeManager(await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(token));

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
            catch
            {
                try { lease.Dispose(); }
                catch (Exception cleanup) when (IsRecoverable(cleanup)) { /* Preserve the original subscription failure. */ }
                throw;
            }
        }
        public async Task<ProbeSnapshot> ReadAsync(CancellationToken token)
        {
            var phase = "Read.Metadata";
            try
            {
                var metadata = await session.TryGetMediaPropertiesAsync().AsTask(token);
                phase = "Read.Playback";
                var playback = session.GetPlaybackInfo();
                phase = "Read.Timeline";
                var timeline = session.GetTimelineProperties();
                phase = "Read.Values";
                var controls = playback.Controls;
                var start = timeline.StartTime; var end = timeline.EndTime; var position = timeline.Position;
                var updated = timeline.LastUpdatedTime;
                var valid = end > start && position >= start && position <= end && updated.Year >= 2000 &&
                    updated <= DateTimeOffset.UtcNow.AddSeconds(2);
                var title = metadata.Title; var artist = metadata.Artist; var album = metadata.AlbumTitle;
                phase = "Read.TrackIdentity";
                var key = string.IsNullOrWhiteSpace(title) ? string.Empty :
                    string.Join('\u001f', title, artist, album,
                        string.Join('\u001e', metadata.Genres.Where(value => value.StartsWith("NCM-", StringComparison.Ordinal))),
                        (end - start).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
                phase = "Read.Controls";
                var playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                var capabilities = new NeteaseMediaCapabilities(!string.IsNullOrWhiteSpace(title), !string.IsNullOrWhiteSpace(artist),
                    !string.IsNullOrWhiteSpace(album), false,
                    playback.PlaybackStatus is GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused,
                    controls.IsPlayEnabled, controls.IsPauseEnabled, controls.IsPreviousEnabled, controls.IsNextEnabled, valid, false,
                    controls.IsPlaybackPositionEnabled);
                phase = "Read.Artwork";
                capabilities = capabilities with { Artwork = await HasArtworkAsync(metadata.Thumbnail, token) };
                return new(key, playing, start, end, position, updated, capabilities);
            }
            catch (Exception exception) when (IsRecoverable(exception)) { exception.Data["SmtcStage"] = phase; throw; }
        }
        public Task<bool> CommandAsync(ProbeCommand command, long value, CancellationToken token) => (command switch
        {
            ProbeCommand.Play => session.TryPlayAsync(),
            ProbeCommand.Pause => session.TryPauseAsync(),
            ProbeCommand.Seek => session.TryChangePlaybackPositionAsync(value),
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
            using var stream = await reference.OpenReadAsync().AsTask(deadline.Token);
            if (stream.Size is 0 or > 4 * 1024 * 1024) return false;
            using var reader = new DataReader(stream);
            return await reader.LoadAsync(1).AsTask(deadline.Token) == 1;
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
