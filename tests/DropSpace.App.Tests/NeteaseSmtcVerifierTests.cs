using DropSpace.App.Services.NeteaseEnhancement;
using DropSpace.Core.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static DropSpace.App.Services.NeteaseEnhancement.NeteaseSmtcVerifier;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class NeteaseSmtcVerifierTests
{
    [TestMethod]
    public void PassiveInspection_RequiresPriorVerificationAndHealthyCurrentSession()
    {
        var paused = Snapshot().Capabilities with { LiveProgress = false, Pause = false };
        Assert.IsTrue(NeteaseEnhancementService.CanRetainVerifiedState(true, paused));
        Assert.IsFalse(NeteaseEnhancementService.CanRetainVerifiedState(false, paused));
        Assert.IsFalse(NeteaseEnhancementService.CanRetainVerifiedState(true, NeteaseMediaCapabilities.Empty));
        Assert.IsFalse(NeteaseEnhancementService.CanRetainVerifiedState(true, paused with { Seek = false }));
        Assert.IsFalse(NeteaseEnhancementService.CanRetainVerifiedState(true, paused with { Play = false }));
        Assert.IsFalse(NeteaseEnhancementService.CanRetainVerifiedState(true, paused with { Artwork = false }));
    }

    [TestMethod]
    public async Task RestartDiscoveryFailure_RetiresLastKnownSessionGeneration()
    {
        var clock = new Clock();
        var old = new Session(clock) { Progress = true };
        var fresh = new Session(clock) { Progress = true };
        var manager = new Manager(old);
        using var verifier = Verifier(manager, clock);
        Assert.IsTrue((await verifier.VerifyAsync(TimeSpan.FromSeconds(1), false)).Complete);
        var reads = old.Reads;
        manager.FailDiscovery = true;
        await verifier.InvalidateBeforeRestartAsync();
        manager.FailDiscovery = false; manager.Sessions = [old, fresh];
        Assert.IsTrue((await verifier.VerifyAsync(TimeSpan.FromSeconds(1), false)).Complete);
        Assert.AreEqual(reads, old.Reads);
    }

    [TestMethod]
    public async Task Restore_TrackLoadOverwritesFirstSeek_RetriesBeforeRejectingVerifiedCapabilities()
    {
        var clock = new Clock();
        var session = new Session(clock) { Progress = true, OverwriteFirstRestoreSeek = true,
            State = Snapshot() with { Playing = false } };
        Assert.IsTrue((await Verifier(new Manager(session), clock).VerifyAsync(TimeSpan.FromSeconds(2), true)).Complete);
        Assert.IsFalse(session.State.Playing);
        Assert.AreEqual(TimeSpan.FromSeconds(20), session.State.Position);
        Assert.AreEqual(3, session.Commands.Count(command => command == ProbeCommand.Seek));
    }

    [TestMethod]
    public async Task Seek_ObservedSubsecondOffsetStillProvesActualJump()
    {
        var clock = new Clock();
        var session = new Session(clock) { Progress = true, SeekOffset = TimeSpan.FromMilliseconds(786),
            State = Snapshot() with { Playing = false } };
        Assert.IsTrue((await Verifier(new Manager(session), clock).VerifyAsync(TimeSpan.FromSeconds(2), true)).Complete);
        Assert.IsFalse(session.State.Playing);
    }

    [TestMethod]
    public async Task Seek_AcceptedWithoutMovementDoesNotPass()
    {
        var clock = new Clock();
        var session = new Session(clock) { Progress = true, IgnoreSeek = true, State = Snapshot() with { Playing = false } };
        Assert.IsFalse((await Verifier(new Manager(session), clock).VerifyAsync(TimeSpan.FromSeconds(2), true)).Complete);
        Assert.IsFalse(session.Commands.Contains(ProbeCommand.Next));
    }

    [TestMethod]
    public async Task Seek_OutsideOneSecondDoesNotPass()
    {
        var clock = new Clock();
        var session = new Session(clock) { Progress = true, SeekOffset = TimeSpan.FromSeconds(2),
            State = Snapshot() with { Playing = false } };
        Assert.IsFalse((await Verifier(new Manager(session), clock).VerifyAsync(TimeSpan.FromSeconds(2), true)).Complete);
    }

    [TestMethod]
    public async Task NotReadyRead_IsRediscoveredAfterInitializationEvent()
    {
        var clock = new Clock();
        var session = new Session(clock) { Progress = true, NotReady = true };
        var manager = new Manager(session);
        using var verifier = Verifier(manager, clock);
        var pending = verifier.VerifyAsync(TimeSpan.FromSeconds(2), false);
        Assert.IsFalse(pending.IsCompleted);
        Assert.AreEqual(2, session.Reads); // one immediate rediscovery, no busy loop
        session.NotReady = false;
        manager.Signal();
        Assert.IsTrue((await pending).Complete);
    }

    [TestMethod]
    public async Task Restart_RetiresOldObjectEvenWhenManagerStillEnumeratesIt()
    {
        var clock = new Clock();
        var old = new Session(clock) { Progress = true };
        var manager = new Manager(old);
        using var verifier = Verifier(manager, clock);
        await verifier.InvalidateBeforeRestartAsync();
        var fresh = new Session(clock) { Progress = true, State = Snapshot() with { Playing = false } };
        manager.Sessions = [old, fresh];
        Assert.IsTrue((await verifier.VerifyAsync(TimeSpan.FromSeconds(2), true)).Complete);
        Assert.AreEqual(0, old.Reads);
        Assert.AreEqual(0, old.Commands.Count);
        Assert.IsTrue(fresh.Commands.Contains(ProbeCommand.Seek));
    }

    [TestMethod]
    public async Task DisconnectedCommand_RebindsAndNeverRestoresRetiredObject()
    {
        var clock = new Clock();
        var old = new Session(clock) { Progress = true, State = Snapshot() with { Playing = false } };
        var fresh = new Session(clock) { Progress = true, State = Snapshot() with { Playing = false } };
        var manager = new Manager(old);
        old.OnCommand = () => { manager.Sessions = [old, fresh]; throw new System.Runtime.InteropServices.COMException("private", unchecked((int)0x80010108)); };
        using var verifier = Verifier(manager, clock);
        var diagnostics = new List<string>(); verifier.Diagnostic = diagnostics.Add;
        Assert.IsTrue((await verifier.VerifyAsync(TimeSpan.FromSeconds(2), true)).Complete);
        Assert.AreEqual(1, old.Commands.Count);
        Assert.AreEqual(1, old.Reads);
        Assert.IsFalse(fresh.State.Playing);
        Assert.IsTrue(diagnostics.Any(value => value.StartsWith("Command.Play;HRESULT=0x80010108", StringComparison.Ordinal)));
        Assert.IsTrue(diagnostics.Contains("StaleSessionDiscarded"));
        Assert.IsFalse(diagnostics.Any(value => value.Contains("private", StringComparison.Ordinal)));
        Assert.AreEqual(0, manager.Subscriptions + old.Subscriptions + fresh.Subscriptions);
    }

    [TestMethod]
    public async Task DisconnectedReadAfterCommand_RebindsNewSession()
    {
        var clock = new Clock();
        var old = new Session(clock) { Progress = true, State = Snapshot() with { Playing = false } };
        var fresh = new Session(clock) { Progress = true, State = Snapshot() with { Playing = false } };
        var manager = new Manager(old);
        old.OnCommand = () => { old.FailRead = true; manager.Sessions = [old, fresh]; };
        using var verifier = Verifier(manager, clock);
        var diagnostics = new List<string>(); verifier.Diagnostic = diagnostics.Add;
        Assert.IsTrue((await verifier.VerifyAsync(TimeSpan.FromSeconds(2), true)).Complete);
        Assert.AreEqual(1, old.Commands.Count);
        Assert.IsTrue(diagnostics.Any(value => value.StartsWith("Read.CommandOrRestore;HRESULT=", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Connection_IsReusedAcrossRestartVerification()
    {
        var clock = new Clock();
        var session = new Session(clock) { Progress = true };
        var manager = new Manager(session);
        var connects = 0;
        using var verifier = new NeteaseSmtcVerifier(_ =>
        {
            connects++;
            if (connects > 1) throw new System.Runtime.InteropServices.COMException();
            return Task.FromResult<IProbeManager>(manager);
        }, clock, TimeSpan.FromMilliseconds(80));
        Assert.IsTrue((await verifier.VerifyAsync(TimeSpan.FromSeconds(1), false)).Complete);
        Assert.IsTrue((await verifier.VerifyAsync(TimeSpan.FromSeconds(1), false)).Complete);
        Assert.AreEqual(1, connects);
        Assert.AreEqual(0, manager.Subscriptions);
    }

    [TestMethod]
    public async Task ConnectionFailure_IsNotReportedAsMissingCapabilities()
    {
        var categories = new List<string>();
        using var verifier = new NeteaseSmtcVerifier(_ => throw new System.Runtime.InteropServices.COMException("private"),
            new Clock(), TimeSpan.FromMilliseconds(80)) { Diagnostic = categories.Add };
        try { await verifier.VerifyAsync(TimeSpan.FromSeconds(1), false); Assert.Fail("Connection failure must propagate"); }
        catch (InvalidOperationException exception) { Assert.AreEqual("MediaVerificationConnectionUnavailable", exception.Message); }
        Assert.IsTrue(categories.Single().StartsWith("ManagerConnectionUnavailable;HRESULT=", StringComparison.Ordinal));
        Assert.IsFalse(categories.Single().Contains("private", StringComparison.Ordinal));
    }
    [TestMethod]
    public async Task WeakPausedSession_IsNotAutomaticallyPlayed()
    {
        var clock = new Clock();
        var session = new Session(clock) { State = Snapshot() with { Playing = false,
            Capabilities = Snapshot().Capabilities with { Timeline = false, Seek = false } } };
        Assert.IsFalse((await Verifier(new Manager(session), clock).VerifyAsync(TimeSpan.FromMilliseconds(80), true)).Complete);
        Assert.AreEqual(0, session.Commands.Count);
    }

    [TestMethod]
    public async Task RejectedStartupPlay_WaitsForEventThenRetriesAndRestores()
    {
        var clock = new Clock();
        var session = new Session(clock) { Progress = true, RejectPlay = true, State = Snapshot() with { Playing = false } };
        var manager = new Manager(session);
        var verifier = Verifier(manager, clock);
        var categories = new List<string>();
        verifier.Diagnostic = categories.Add;
        var pending = verifier.VerifyAsync(TimeSpan.FromSeconds(2), true);
        Assert.IsFalse(pending.IsCompleted);
        Assert.AreEqual(1, session.Commands.Count);
        Assert.IsTrue(categories.Any(value => value.StartsWith("AutoPlayRejected;", StringComparison.Ordinal)));
        session.RejectPlay = false;
        manager.Signal();
        Assert.IsTrue((await pending).Complete);
        Assert.IsFalse(session.State.Playing);
        Assert.AreEqual(TimeSpan.FromSeconds(20), session.State.Position);
        Assert.AreEqual(0, session.Subscriptions + manager.Subscriptions);
    }

    [TestMethod]
    public async Task RestartDiscoveryFailure_WaitsForManagerEvent()
    {
        var clock = new Clock();
        var session = new Session(clock) { Progress = true };
        var manager = new Manager(session) { FailDiscovery = true };
        var pending = Verifier(manager, clock).VerifyAsync(TimeSpan.FromSeconds(2), false);
        Assert.IsFalse(pending.IsCompleted);
        manager.FailDiscovery = false;
        manager.Signal();
        Assert.IsTrue((await pending).Complete);
        Assert.AreEqual(0, manager.Subscriptions + session.Subscriptions);
    }

    [TestMethod]
    public async Task StaleSourceAndSubscription_DoNotBlockHealthyCandidate()
    {
        var clock = new Clock();
        var session = new Session(clock) { Progress = true };
        var manager = new Manager(new Session(clock) { FailSource = true },
            new Session(clock) { FailSubscription = true }, session);
        Assert.IsTrue((await Verifier(manager, clock).VerifyAsync(TimeSpan.FromSeconds(2), false)).Complete);
        Assert.AreEqual(0, manager.Subscriptions + session.Subscriptions);
    }

    [TestMethod]
    public async Task StateSpecificPlayPause_ProvesBothThroughCommands()
    {
        var clock = new Clock();
        var session = new Session(clock) { Progress = true, StateSpecific = true, State = Snapshot() with { Playing = false } };
        var result = await Verifier(new Manager(session), clock).VerifyAsync(TimeSpan.FromSeconds(2), true);
        Assert.IsTrue(result.Complete);
        Assert.IsFalse(session.State.Playing);
        Assert.IsTrue(session.Commands.Contains(ProbeCommand.Play));
        Assert.IsTrue(session.Commands.Contains(ProbeCommand.Pause));
    }

    [TestMethod]
    public async Task OpenedSession_PlayInitializesState()
    {
        var clock = new Clock();
        var session = new Session(clock)
        {
            Progress = true, StateSpecific = true, InitializeOnPlay = true,
            State = Snapshot() with { Playing = false,
                Capabilities = Snapshot().Capabilities with { PlaybackState = false } },
        };
        Assert.IsTrue((await Verifier(new Manager(session), clock).VerifyAsync(TimeSpan.FromSeconds(2), true)).Complete);
        Assert.IsFalse(session.State.Playing);
    }

    [TestMethod]
    public async Task ReadOnly_SelectsCompleteDuplicateAndReleasesSubscriptions()
    {
        var clock = new Clock();
        var weak = new Session(clock) { Source = "cloudmusic.exe", State = Snapshot() with { Capabilities = NeteaseMediaCapabilities.Empty } };
        var strong = new Session(clock) { Source = "cloudmusic.exe", Progress = true };
        var unrelated = new Session(clock) { Source = "Spotify.exe" };
        var manager = new Manager(weak, strong, unrelated);
        var result = await Verifier(manager, clock).VerifyAsync(TimeSpan.FromSeconds(1), false);
        Assert.IsTrue(result.Complete);
        Assert.AreEqual(0, strong.Commands.Count);
        Assert.AreEqual(0, unrelated.Reads);
        Assert.AreEqual(0, strong.Subscriptions + weak.Subscriptions + manager.Subscriptions);
    }

    [TestMethod]
    public async Task FrozenClock_DoesNotProveLiveProgress()
    {
        var clock = new Clock();
        var session = new Session(clock);
        var manager = new Manager(session);
        var result = await Verifier(manager, clock).VerifyAsync(TimeSpan.FromMilliseconds(80), false);
        Assert.IsFalse(result.Complete);
        Assert.IsFalse(result.LiveProgress);
        Assert.AreEqual(0, session.Subscriptions + manager.Subscriptions);
    }

    [TestMethod]
    public async Task Exercise_RestoresPausedTrackAndPosition()
    {
        var clock = new Clock();
        var session = new Session(clock) { Progress = true, State = Snapshot() with { Playing = false } };
        var result = await Verifier(new Manager(session), clock).VerifyAsync(TimeSpan.FromSeconds(2), true);
        Assert.IsTrue(result.Complete);
        Assert.IsFalse(session.State.Playing);
        Assert.AreEqual("one", session.State.TrackKey);
        Assert.AreEqual(TimeSpan.FromSeconds(20), session.State.Position);
        Assert.AreEqual(0, session.Subscriptions);
    }

    [TestMethod]
    public async Task AcceptedNextWithoutTrackChange_Fails()
    {
        var clock = new Clock();
        var session = new Session(clock) { Progress = true, IgnoreNext = true, State = Snapshot() with { Playing = false } };
        var result = await Verifier(new Manager(session), clock).VerifyAsync(TimeSpan.FromSeconds(2), true);
        Assert.IsFalse(result.Complete);
        Assert.IsFalse(session.State.Playing);
        Assert.AreEqual(0, session.Subscriptions);
    }

    [TestMethod]
    public async Task FailedPrevious_DoesNotSeekOriginalPositionIntoOtherTrack()
    {
        var clock = new Clock();
        var session = new Session(clock) { Progress = true, IgnorePrevious = true, State = Snapshot() with { Playing = false } };
        var result = await Verifier(new Manager(session), clock).VerifyAsync(TimeSpan.FromSeconds(2), true);
        Assert.IsFalse(result.Complete);
        Assert.AreEqual("two", session.State.TrackKey);
        Assert.IsFalse(session.State.Playing);
        Assert.AreEqual(1, session.Commands.Count(command => command == ProbeCommand.Seek));
    }

    [TestMethod]
    public async Task Cancellation_ReleasesSubscriptions()
    {
        var clock = new Clock();
        var session = new Session(clock);
        var manager = new Manager(session);
        using var cancellation = new CancellationTokenSource();
        var pending = Verifier(manager, clock).VerifyAsync(TimeSpan.FromSeconds(2), false, cancellation.Token);
        cancellation.Cancel();
        try { await pending; Assert.Fail("Cancellation must propagate"); }
        catch (OperationCanceledException) { }
        Assert.AreEqual(0, session.Subscriptions + manager.Subscriptions);
    }

    [TestMethod]
    public void SourceMatching_RejectsSubstringImpersonation()
    {
        Assert.IsTrue(IsNeteaseSource("InfLink"));
        Assert.IsTrue(IsNeteaseSource(@"C:\Player\cloudmusic.exe"));
        Assert.IsFalse(IsNeteaseSource("not-cloudmusic.exe"));
        Assert.IsFalse(IsNeteaseSource("other.inflink.application"));
    }

    private static NeteaseSmtcVerifier Verifier(Manager manager, Clock clock) => new(_ => Task.FromResult<IProbeManager>(manager), clock, TimeSpan.FromMilliseconds(80));
    private static ProbeSnapshot Snapshot() => new("one", true, TimeSpan.Zero, TimeSpan.FromMinutes(3), TimeSpan.FromSeconds(20),
        DateTimeOffset.Parse("2026-09-21T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
        new(true, true, true, true, true, true, true, true, true, true, false, true));
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance() => _ticks += TimeSpan.TicksPerSecond;
    }
    private sealed class Manager(params Session[] sessions) : IProbeManager
    {
        private event Action? Changed;
        public Session[] Sessions { get; set; } = sessions;
        public bool FailDiscovery { get; set; }
        public void Signal() => Changed?.Invoke();
        public int Subscriptions { get; private set; }
        public IReadOnlyList<IProbeSession> GetSessions() => FailDiscovery ? throw new System.Runtime.InteropServices.COMException() : Sessions;
        public IDisposable Subscribe(Action changed) { Subscriptions++; Changed += changed; return new Lease(() => { Subscriptions--; Changed -= changed; }); }
    }
    private sealed class Session(Clock clock) : IProbeSession
    {
        private event Action? Changed;
        public object Identity { get; } = new();
        private readonly string _source = "InfLink";
        public string Source { get => FailSource ? throw new System.Runtime.InteropServices.COMException() : _source; init => _source = value; }
        public bool FailSource { get; init; }
        public bool FailSubscription { get; init; }
        public bool StateSpecific { get; init; }
        public bool InitializeOnPlay { get; init; }
        public bool RejectPlay { get; set; }
        public bool FailRead { get; set; }
        public bool NotReady { get; set; }
        public Action? OnCommand { get; set; }
        public ProbeSnapshot State { get; set; } = Snapshot();
        public bool Progress { get; init; }
        public bool IgnoreNext { get; init; }
        public bool IgnoreSeek { get; init; }
        public bool OverwriteFirstRestoreSeek { get; init; }
        private bool _restoreLoadPending;
        public TimeSpan SeekOffset { get; init; }
        public bool IgnorePrevious { get; init; }
        public int Reads { get; private set; }
        public int Subscriptions { get; private set; }
        public List<ProbeCommand> Commands { get; } = [];
        public IDisposable Subscribe(Action changed)
        { if (FailSubscription) throw new System.Runtime.InteropServices.COMException(); Changed += changed; Subscriptions++; return new Lease(() => { Changed -= changed; Subscriptions--; }); }
        public Task<ProbeSnapshot> ReadAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Reads++;
            if (NotReady) throw new System.Runtime.InteropServices.COMException("private", unchecked((int)0x80070015));
            if (FailRead) throw new System.Runtime.InteropServices.COMException("private", unchecked((int)0x80010108));
            if (Progress && State.Playing && !_restoreLoadPending)
            {
                clock.Advance();
                State = State with { Position = State.Position + TimeSpan.FromSeconds(1), Updated = State.Updated.AddSeconds(1) };
                Changed?.Invoke();
            }
            return Task.FromResult(StateSpecific ? State with { Capabilities = State.Capabilities with { Play = !State.Playing, Pause = State.Playing } } : State);
        }
        public Task<bool> CommandAsync(ProbeCommand command, long value, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Commands.Add(command);
            OnCommand?.Invoke();
            if (OverwriteFirstRestoreSeek && command == ProbeCommand.Seek && Commands.Count(value => value == ProbeCommand.Seek) == 2)
            {
                _restoreLoadPending = true;
                State = State with { Position = TimeSpan.Zero, Playing = true };
                Changed?.Invoke();
                return Task.FromResult(true);
            }
            if (command == ProbeCommand.Pause) _restoreLoadPending = false;
            if (RejectPlay && command == ProbeCommand.Play) return Task.FromResult(false);
            if (StateSpecific && ((command == ProbeCommand.Play && State.Playing) || (command == ProbeCommand.Pause && !State.Playing)))
                return Task.FromResult(false);
            if (InitializeOnPlay && command == ProbeCommand.Play && !State.Capabilities.PlaybackState)
                State = State with { Capabilities = State.Capabilities with { PlaybackState = true } };
            State = command switch
            {
                ProbeCommand.Play => State with { Playing = true },
                ProbeCommand.Pause => State with { Playing = false },
                ProbeCommand.Seek when !IgnoreSeek => State with { Position = TimeSpan.FromTicks(value) + SeekOffset },
                ProbeCommand.Next when !IgnoreNext => State with { TrackKey = "two", Position = TimeSpan.Zero },
                ProbeCommand.Previous when !IgnorePrevious => State with { TrackKey = "one", Position = TimeSpan.Zero },
                _ => State,
            };
            Changed?.Invoke();
            return Task.FromResult(true);
        }
    }
    private sealed class Lease(Action dispose) : IDisposable
    { public void Dispose() => dispose(); }
}
