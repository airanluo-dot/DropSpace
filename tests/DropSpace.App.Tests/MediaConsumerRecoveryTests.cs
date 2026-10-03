using System.Reflection;
using System.Threading.Channels;
using DropSpace.App.Services.Media;
using DropSpace.Core.Media;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.Media.Control;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class MediaConsumerRecoveryTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [TestMethod]
    public void PlaybackProbeAcceptsMissingSessionAndPlaybackInformation()
    {
        Assert.IsFalse(WindowsMediaSessionService.IsPlaying<object>(null, _ => throw new AssertFailedException("No session should be read.")));
        Assert.IsFalse(WindowsMediaSessionService.IsPlaying(new object(), _ => null));
        Assert.IsTrue(WindowsMediaSessionService.IsPlaying(new object(), _ => GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing));
        Assert.IsFalse(WindowsMediaSessionService.IsPlaying(new object(), _ => GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused));
        Assert.ThrowsExactly<NullReferenceException>(() => WindowsMediaSessionService.IsPlaying(new object(), _ => throw new NullReferenceException("Programming faults must remain visible.")));
    }

    [TestMethod]
    public async Task MissingPlaybackOrControlsDoesNotHideHealthyCandidate()
    {
        foreach (var category in new[] { "playback information", "playback controls", "timeline", "media properties" })
        {
            var broken = new object(); var healthy = new object();
            var result = await WindowsMediaSessionService.ReadCandidatesAsync(new[] { broken, healthy },
                (candidate, _) => Task.FromResult(WindowsMediaSessionService.RequireNativeValue(
                    ReferenceEquals(candidate, broken) ? null : "New song", category)), Deadline, CancellationToken.None);
            Assert.AreEqual(1, result.Count);
            Assert.AreSame(healthy, result[0].Candidate);
            Assert.AreEqual("New song", result[0].Value);
        }
    }

    [TestMethod]
    public async Task UnexpectedConsumerFaultClearsOldSongAndFailsRestartReadiness()
    {
        await using var service = NewService();
        using var lifetime = new CancellationTokenSource();
        var changes = Channel.CreateUnbounded<bool>();
        var ready = NewSignal();
        var reads = 0;
        var old = MediaSessionSnapshot.Empty with { SessionId = "old", TrackTitle = "Old song" };
        var consumer = service.ConsumeAsync(changes.Reader, lifetime.Token, _ =>
            ++reads == 1 ? Task.FromResult(old) : Task.FromException<MediaSessionSnapshot>(new NullReferenceException("Unexpected read fault")), ready);
        changes.Writer.TryWrite(true);
        await ready.Task.WaitAsync(Deadline);
        Assert.AreEqual("Old song", service.Current.TrackTitle);
        changes.Writer.TryWrite(true);
        await Assert.ThrowsExactlyAsync<NullReferenceException>(() => consumer.WaitAsync(Deadline));
        Assert.IsFalse(service.IsAvailable);
        Assert.AreEqual(nameof(NullReferenceException), service.AvailabilityReason);
        Assert.AreEqual(MediaSessionSnapshot.Empty, service.Current);

        var failedReady = NewSignal();
        var failed = service.ConsumeAsync(changes.Reader, lifetime.Token,
            _ => Task.FromException<MediaSessionSnapshot>(new NullReferenceException("First read fault")), failedReady);
        changes.Writer.TryWrite(true);
        await Assert.ThrowsExactlyAsync<NullReferenceException>(() => failedReady.Task.WaitAsync(Deadline));
        await Assert.ThrowsExactlyAsync<NullReferenceException>(() => failed.WaitAsync(Deadline));
    }

    [TestMethod]
    public async Task StopAfterFaultRetiresSubscriptionsAndAllowsFreshConsumerWithoutReleasingNativeOwnership()
    {
        await using var service = NewService();
        var lifetime = new CancellationTokenSource();
        var changes = Channel.CreateUnbounded<bool>();
        var admission = GetField<MediaSubscriptionAdmission>(service, "_subscriptions");
        var subscription = new MediaEventSubscription();
        var detachEntered = NewSignal();
        using var releaseDetach = new ManualResetEventSlim();
        await subscription.StartAsync(admission.Manager, new object(), () => { }, () =>
        {
            detachEntered.TrySetResult(); releaseDetach.Wait();
        }, Deadline, CancellationToken.None);
        SetField(service, "_lifetime", lifetime);
        SetField(service, "_refresh", changes);
        SetField(service, "_managerSubscription", subscription);
        SetField(service, "_consumer", Task.FromException(new NullReferenceException("Old consumer fault")));
        try
        {
            await service.SetEnabledAsync(false).WaitAsync(Deadline);
            await detachEntered.Task.WaitAsync(Deadline);
            Assert.IsFalse(subscription.IsActive);
            Assert.AreEqual(1, admission.Manager.Outstanding, "Retired detach still owns its native slot.");
            Assert.IsNull(GetField<object?>(service, "_lifetime"));
            Assert.IsNull(GetField<object?>(service, "_refresh"));
            Assert.IsNull(GetField<object?>(service, "_managerSubscription"));
            Assert.AreEqual(Task.CompletedTask, GetField<Task>(service, "_consumer"));
            Assert.AreEqual(MediaSessionSnapshot.Empty, service.Current);
            Assert.AreEqual(0, service.AvailableSources.Count);
            Assert.ThrowsExactly<ObjectDisposedException>(() => lifetime.Cancel());

            // Exercise the same consumer/readiness path as a reconnect after Stop.
            var nextLifetime = new CancellationTokenSource();
            var nextChanges = Channel.CreateUnbounded<bool>();
            var ready = NewSignal();
            SetField(service, "_lifetime", nextLifetime);
            SetField(service, "_refresh", nextChanges);
            var next = service.ConsumeAsync(nextChanges.Reader, nextLifetime.Token,
                _ => Task.FromResult(MediaSessionSnapshot.Empty with { SessionId = "new", TrackTitle = "New song" }), ready);
            SetField(service, "_consumer", next);
            nextChanges.Writer.TryWrite(true);
            await ready.Task.WaitAsync(Deadline);
            Assert.IsTrue(service.IsAvailable);
            Assert.AreEqual("New song", service.Current.TrackTitle);
            await service.DisposeAsync().AsTask().WaitAsync(Deadline);
            await service.DisposeAsync().AsTask().WaitAsync(Deadline);
            Assert.AreEqual(MediaSessionSnapshot.Empty, service.Current);
        }
        finally { releaseDetach.Set(); }
        using var deadline = new CancellationTokenSource(Deadline);
        while (admission.Manager.Outstanding != 0) await Task.Delay(1, deadline.Token);
    }

    private static WindowsMediaSessionService NewService() => new(NullLogger<WindowsMediaSessionService>.Instance);
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void SetField(object owner, string name, object value) =>
        owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);
    private static T GetField<T>(object owner, string name) =>
        (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
}
