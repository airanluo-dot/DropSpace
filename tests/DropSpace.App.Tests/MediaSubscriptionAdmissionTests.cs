using DropSpace.App.Services.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class MediaSubscriptionAdmissionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task ManyRetiredRecoveryOwnersCannotConsumeManagerOrPrimaryAdmission()
    {
        var admission = new MediaSubscriptionAdmission(managerCapacity: 2, primaryCapacity: 2, recoveryCapacity: 4);
        using var release = new ManualResetEventSlim();
        var retired = new List<MediaEventSubscription>();
        var entered = Enumerable.Range(0, 4).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var detachCount = 0;
        try
        {
            foreach (var blocked in entered)
            {
                var subscription = new MediaEventSubscription();
                retired.Add(subscription);
                await subscription.StartAsync(admission.Recovery, new object(), () => { }, () =>
                {
                    Interlocked.Increment(ref detachCount);
                    blocked.TrySetResult();
                    release.Wait();
                }, Deadline, CancellationToken.None);
                subscription.Retire();
                subscription.Retire();
                await blocked.Task.WaitAsync(Deadline);
            }
            Assert.AreEqual(4, admission.Recovery.Outstanding);
            var rejectedStarts = 0;
            for (var attempt = 0; attempt < 32; attempt++)
            {
                var rejected = new MediaEventSubscription();
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => rejected.StartAsync(admission.Recovery,
                    new object(), () => rejectedStarts++, () => { }, Deadline, CancellationToken.None));
            }
            Assert.AreEqual(0, rejectedStarts, "A full recovery partition must not spawn more native work.");
            // Reconnect retains the same admission object. Old recovery detaches remain
            // owned across each restart, while healthy mandatory subscriptions can renew.
            for (var restart = 0; restart < 3; restart++)
            {
                var manager = new MediaEventSubscription();
                var primary = new MediaEventSubscription();
                try
                {
                    await manager.StartAsync(admission.Manager, new object(), () => { }, () => { }, Deadline, CancellationToken.None);
                    await primary.StartAsync(admission.Primary, new object(), () => { }, () => { }, Deadline, CancellationToken.None);
                    Assert.IsTrue(manager.IsActive);
                    Assert.IsTrue(primary.IsActive);
                    Assert.AreEqual(4, admission.Recovery.Outstanding);
                }
                finally { manager.Retire(); primary.Retire(); }
                await DrainAsync(admission.Manager);
                await DrainAsync(admission.Primary);
            }
            Assert.IsTrue(retired.All(subscription => !subscription.IsActive));
            Assert.AreEqual(4, admission.Recovery.Outstanding, "Retirement must not fake native detach completion.");
        }
        finally
        {
            foreach (var subscription in retired) subscription.Retire();
            release.Set();
        }
        await DrainAsync(admission.Recovery);
        Assert.AreEqual(4, detachCount, "Each owner must detach and release exactly once.");
        var recovered = new MediaEventSubscription();
        await recovered.StartAsync(admission.Recovery, new object(), () => { }, () => { }, Deadline, CancellationToken.None);
        recovered.Retire();
        await DrainAsync(admission.Recovery);
    }

    private static async Task DrainAsync(BoundedMediaOperation operations)
    {
        using var timeout = new CancellationTokenSource(Deadline);
        while (operations.Outstanding != 0) await Task.Delay(1, timeout.Token);
    }
}
