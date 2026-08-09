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

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => coordinator.RequestAsync(1));
        await coordinator.RequestAsync(2);

        Assert.AreEqual(2, coordinator.AppliedRevision);
        Assert.IsTrue(attempts >= 2);
    }
}
