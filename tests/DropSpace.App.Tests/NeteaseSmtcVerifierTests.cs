using DropSpace.App.Services.NeteaseEnhancement;
using DropSpace.Core.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Media;
using static DropSpace.App.Services.NeteaseEnhancement.NeteaseSmtcVerifier;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class NeteaseSmtcVerifierTests
{
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
        Assert.IsTrue(categories.Contains("AutoPlayRejected"));
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
    public async Task OpenedSession_PlayInitializesStateAndPlaybackModes()
    {
        var clock = new Clock();
        var session = new Session(clock)
        {
            Progress = true, StateSpecific = true, InitializeOnPlay = true,
            State = Snapshot() with { Playing = false, Shuffle = null, Repeat = null,
                Capabilities = Snapshot().Capabilities with { PlaybackState = false, Shuffle = false, Repeat = false } },
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
    public async Task Exercise_RestoresPausedTrackAndPreferences()
    {
        var clock = new Clock();
        var session = new Session(clock) { Progress = true, State = Snapshot() with { Playing = false } };
        var result = await Verifier(new Manager(session), clock).VerifyAsync(TimeSpan.FromSeconds(2), true);
        Assert.IsTrue(result.Complete);
        Assert.IsFalse(session.State.Playing);
        Assert.AreEqual("one", session.State.TrackKey);
        Assert.AreEqual(TimeSpan.FromSeconds(20), session.State.Position);
        Assert.AreEqual(false, session.State.Shuffle);
        Assert.AreEqual(MediaPlaybackAutoRepeatMode.None, session.State.Repeat);
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
        DateTimeOffset.Parse("2026-09-21T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture), false, MediaPlaybackAutoRepeatMode.None,
        new(true, true, true, true, true, true, true, true, true, true, false, true, true, true));
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
        public bool FailDiscovery { get; set; }
        public void Signal() => Changed?.Invoke();
        public int Subscriptions { get; private set; }
        public IReadOnlyList<IProbeSession> GetSessions() => FailDiscovery ? throw new System.Runtime.InteropServices.COMException() : sessions;
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
        public ProbeSnapshot State { get; set; } = Snapshot();
        public bool Progress { get; init; }
        public bool IgnoreNext { get; init; }
        public bool IgnorePrevious { get; init; }
        public int Reads { get; private set; }
        public int Subscriptions { get; private set; }
        public List<ProbeCommand> Commands { get; } = [];
        public IDisposable Subscribe(Action changed)
        { if (FailSubscription) throw new System.Runtime.InteropServices.COMException(); Changed += changed; Subscriptions++; return new Lease(() => { Changed -= changed; Subscriptions--; }); }
        public Task<ProbeSnapshot> ReadAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Reads++;
            if (Progress && State.Playing)
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
            if (RejectPlay && command == ProbeCommand.Play) return Task.FromResult(false);
            if (StateSpecific && ((command == ProbeCommand.Play && State.Playing) || (command == ProbeCommand.Pause && !State.Playing)))
                return Task.FromResult(false);
            if (InitializeOnPlay && command == ProbeCommand.Play && !State.Capabilities.PlaybackState)
                State = State with { Shuffle = false, Repeat = MediaPlaybackAutoRepeatMode.None,
                    Capabilities = State.Capabilities with { PlaybackState = true, Shuffle = true, Repeat = true } };
            State = command switch
            {
                ProbeCommand.Play => State with { Playing = true },
                ProbeCommand.Pause => State with { Playing = false },
                ProbeCommand.Seek => State with { Position = TimeSpan.FromTicks(value) },
                ProbeCommand.Shuffle => State with { Shuffle = value != 0 },
                ProbeCommand.Repeat => State with { Repeat = (MediaPlaybackAutoRepeatMode)value },
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
