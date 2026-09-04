using DropSpace.Infrastructure.Network;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class DropLinkSingleFlightTests
{
    [TestMethod]
    public async Task ConcurrentCompletionRequestsShareOneStableTask()
    {
        var coordinator = new DropLinkSingleFlight<string>();
        var started = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryCalls = 0;

        async Task<string> CompleteAsync()
        {
            Interlocked.Increment(ref factoryCalls);
            started.TrySetResult(null);
            await release.Task;
            return "completed";
        }

        var first = coordinator.GetOrStart(CompleteAsync);
        await started.Task;
        var callers = Enumerable.Range(0, 99)
            .Select(_ => Task.Run(() => coordinator.GetOrStart(CompleteAsync)))
            .ToArray();

        release.SetResult(null);
        var results = await Task.WhenAll(new[] { first }.Concat(callers));

        Assert.AreEqual(1, factoryCalls);
        CollectionAssert.AreEqual(
            Enumerable.Repeat("completed", 100).ToArray(),
            results);
        Assert.AreSame(first, coordinator.GetOrStart(CompleteAsync));
    }
}
