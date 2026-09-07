using DropSpace.Core.Collections;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class SerializedProjectionRefreshCoordinatorTests
{
    [TestMethod]
    public async Task FiveHundredConcurrentRequestsAreCoalescedWithoutOverlappingApply()
    {
        var activeLoads = 0;
        var activeApplies = 0;
        var maxLoads = 0;
        var maxApplies = 0;
        var applied = new List<long>();
        await using var coordinator = new SerializedProjectionRefreshCoordinator<int>(
            async cancellationToken =>
            {
                var active = Interlocked.Increment(ref activeLoads);
                maxLoads = Math.Max(maxLoads, active);
                try
                {
                    await Task.Yield();
                    cancellationToken.ThrowIfCancellationRequested();
                    return [1];
                }
                finally
                {
                    Interlocked.Decrement(ref activeLoads);
                }
            },
            async (_, revision, cancellationToken) =>
            {
                var active = Interlocked.Increment(ref activeApplies);
                maxApplies = Math.Max(maxApplies, active);
                try
                {
                    await Task.Yield();
                    cancellationToken.ThrowIfCancellationRequested();
                    lock (applied)
                    {
                        applied.Add(revision);
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref activeApplies);
                }
            });

        var requests = Enumerable.Range(0, 500)
            .Select(index => coordinator.RequestAsync(index))
            .ToArray();
        await Task.WhenAll(requests);

        Assert.AreEqual(1, maxLoads);
        Assert.AreEqual(1, maxApplies);
        Assert.AreEqual(499, coordinator.AppliedRevision);
        Assert.IsTrue(applied.SequenceEqual(applied.Order()));
        Assert.AreEqual(499, applied[^1]);
    }

    [TestMethod]
    public async Task AFailedRevisionDoesNotPreventANewerRefresh()
    {
        var attempts = 0;
        await using var coordinator = new SerializedProjectionRefreshCoordinator<int>(
            _ => Task.FromResult<IReadOnlyList<int>>([Interlocked.Increment(ref attempts)]),
            (_, revision, _) => revision == 1
                ? Task.FromException(new InvalidOperationException("injected projection failure"))
                : Task.CompletedTask);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.RequestAsync(1));
        await coordinator.RequestAsync(2);

        Assert.AreEqual(2, coordinator.AppliedRevision);
        Assert.IsTrue(attempts >= 2);
    }
    [TestMethod]
    public async Task FailedApplyDoesNotClaimSuccessAndSameRevisionCanRetry()
    {
        var attempts = 0;
        await using var coordinator = new SerializedProjectionRefreshCoordinator<int>(
            _ => Task.FromResult<IReadOnlyList<int>>([1]),
            (_, _, _) => Interlocked.Increment(ref attempts) == 1
                ? Task.FromException(new InvalidOperationException("injected transient failure"))
                : Task.CompletedTask);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.RequestAsync(7));
        Assert.AreEqual(-1, coordinator.AppliedRevision);
        await coordinator.RequestAsync(7);
        Assert.AreEqual(7, coordinator.AppliedRevision);
        Assert.AreEqual(2, attempts);
    }

    [TestMethod]
    public async Task FailedLoadStopsUntilExplicitRetry()
    {
        var attempts = 0;
        await using var coordinator = new SerializedProjectionRefreshCoordinator<int>(
            _ => Interlocked.Increment(ref attempts) == 1
                ? Task.FromException<IReadOnlyList<int>>(new IOException("injected read failure"))
                : Task.FromResult<IReadOnlyList<int>>([1]),
            (_, _, _) => Task.CompletedTask);

        await Assert.ThrowsExactlyAsync<IOException>(() => coordinator.RequestAsync(3));
        Assert.AreEqual(1, attempts);
        Assert.AreEqual(-1, coordinator.AppliedRevision);
        await coordinator.RequestAsync(3);
        Assert.AreEqual(3, coordinator.AppliedRevision);
    }

    [TestMethod]
    public async Task ImmediateConcurrentRetryAfterAsyncFailureAlwaysHasAWorker()
    {
        var attempts = 0;
        await using var coordinator = new SerializedProjectionRefreshCoordinator<int>(
            async _ =>
            {
                await Task.Yield();
                if (Interlocked.Increment(ref attempts) % 2 == 1)
                {
                    throw new IOException("injected transient read failure");
                }
                return [1];
            },
            (_, _, _) => Task.CompletedTask);

        for (var revision = 0; revision < 100; revision++)
        {
            await Assert.ThrowsExactlyAsync<IOException>(() => coordinator.RequestAsync(revision));
            await coordinator.RequestAsync(revision).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(revision, coordinator.AppliedRevision);
        }
    }

}
