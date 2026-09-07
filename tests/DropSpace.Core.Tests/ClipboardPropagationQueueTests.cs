using DropSpace.Core.Models;
using DropSpace.Core.Transfer;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class ClipboardPropagationQueueTests
{
    [TestMethod]
    public async Task OverloadIsBoundedRetainsNewestAndNeverOverlapsSends()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<string>();
        var active = 0;
        var maximumActive = 0;
        await using var queue = new ClipboardPropagationQueue(async (item, token) =>
        {
            maximumActive = Math.Max(maximumActive, Interlocked.Increment(ref active));
            try
            {
                if (item.Title == "first")
                {
                    started.SetResult();
                    await release.Task.WaitAsync(token);
                }
                seen.Add(item.Title);
                if (item.Title == "newest") complete.SetResult();
            }
            finally { Interlocked.Decrement(ref active); }
        }, exception => Assert.Fail(exception.ToString()));
        queue.TryEnqueue(Item("first"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (var i = 0; i < 100; i++) queue.TryEnqueue(Item("superseded"));
        queue.TryEnqueue(Item("newest"));
        release.SetResult();
        await complete.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await queue.DisposeAsync();
        Assert.AreEqual(1, maximumActive);
        Assert.IsTrue(seen.Count <= ClipboardPropagationQueue.Capacity + 1);
        Assert.AreEqual("newest", seen[^1]);
        Assert.IsFalse(queue.TryEnqueue(Item("after-stop")));
    }

    [TestMethod]
    public async Task FailureDoesNotKillWorkerAndShutdownWaitsForCancellationCleanup()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failures = 0;
        var queue = new ClipboardPropagationQueue(async (item, token) =>
        {
            if (item.Title == "fail") throw new IOException("peer disconnected");
            started.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally
            {
                cancelled.SetResult();
                await cleanup.Task;
            }
        }, _ => Interlocked.Increment(ref failures));
        queue.TryEnqueue(Item("fail"));
        queue.TryEnqueue(Item("wait"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var first = queue.DisposeAsync().AsTask();
        var second = queue.DisposeAsync().AsTask();
        Assert.AreSame(first, second);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(first.IsCompleted);
        cleanup.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, failures);
    }

    private static DropItem Item(string title) => new(Guid.NewGuid(), ItemSource.Clipboard, ItemKind.Text,
        title, DateTimeOffset.UtcNow, null, false, ItemStatus.Available, title, 1, null, null, null, null, null, null, null);
}
