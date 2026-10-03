using DropSpace.App.Services.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class MediaOperationLifetimeTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(40);
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource<T> Completion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [TestMethod]
    public async Task NonCooperativePublisherTimesOutWithoutReleasingNativeOwnership()
    {
        var operations = new BoundedMediaOperation();
        var never = Completion<string>();
        var cancellation = Completion<bool>();
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => operations.RunAsync(new object(), token =>
        {
            token.Register(() => cancellation.TrySetResult(true));
            return never.Task;
        }, Deadline, CancellationToken.None).WaitAsync(TestDeadline));
        Assert.AreEqual(1, operations.Outstanding);
        Assert.IsTrue(await cancellation.Task.WaitAsync(TestDeadline));
        never.SetResult("Late old title");
        await DrainAsync(operations);
    }

    [TestMethod]
    public async Task CancellationReleasesLifecycleWaitWithoutWaitingForNativeAcknowledgement()
    {
        var operations = new BoundedMediaOperation();
        var native = Completion<string>();
        using var lifetime = new CancellationTokenSource();
        var started = Completion<bool>();
        var wait = operations.RunAsync(new object(), _ => { started.TrySetResult(true); return native.Task; }, Timeout.InfiniteTimeSpan, lifetime.Token);
        await started.Task.WaitAsync(TestDeadline);
        await lifetime.CancelAsync();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => wait.WaitAsync(TestDeadline));
        Assert.AreEqual(1, operations.Outstanding);
        native.SetException(new IOException("Late native failure"));
        await DrainAsync(operations);
    }

    [TestMethod]
    public async Task TimedOutSameSessionCanRetryAndLateOldResultCannotOverwriteCurrentTitle()
    {
        var operations = new BoundedMediaOperation();
        var session = new object();
        var old = Completion<string>();
        var title = "Initial";
        async Task RefreshAsync(Task<string> provider)
        {
            title = await operations.RunAsync(session, _ => provider, Deadline, CancellationToken.None);
        }
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => RefreshAsync(old.Task));
        await RefreshAsync(Task.FromResult("New track"));
        Assert.AreEqual("New track", title);
        old.SetResult("Old track");
        await DrainAsync(operations);
        Assert.AreEqual("New track", title);
    }

    [TestMethod]
    public async Task RepeatedNaturalTransitionsStayLiveWithOneRetiredOperation()
    {
        var operations = new BoundedMediaOperation();
        var session = new object();
        var old = Completion<string>();
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => operations.RunAsync(session, _ => old.Task, Deadline, CancellationToken.None));
        for (var index = 1; index <= 256; index++)
        {
            var title = $"Track {index}";
            Assert.AreEqual(title, await operations.RunAsync(session, _ => Task.FromResult(title), Deadline, CancellationToken.None));
            Assert.AreEqual(1, operations.Outstanding);
        }
        old.SetResult("Retired track");
        await DrainAsync(operations);
    }

    [TestMethod]
    public async Task SaturatedSessionCannotSpawnUnboundedWorkOrBlockReplacementSession()
    {
        var operations = new BoundedMediaOperation(4, 2);
        var oldSession = new object();
        var first = Completion<string>();
        var second = Completion<string>();
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => operations.RunAsync(oldSession, _ => first.Task, Deadline, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => operations.RunAsync(oldSession, _ => second.Task, Deadline, CancellationToken.None));
        var starts = 0;
        for (var index = 0; index < 256; index++)
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => operations.RunAsync(oldSession,
                _ => { starts++; return first.Task; }, Deadline, CancellationToken.None));
        Assert.AreEqual(0, starts);
        Assert.AreEqual(2, operations.Outstanding);
        Assert.AreEqual("Replacement", await operations.RunAsync(new object(), _ => Task.FromResult("Replacement"), Deadline, CancellationToken.None));
        first.SetResult("Old"); second.SetResult("Old");
        await DrainAsync(operations);
        Assert.AreEqual("Retry recovered", await operations.RunAsync(oldSession, _ => Task.FromResult("Retry recovered"), Deadline, CancellationToken.None));
    }

    [TestMethod]
    public async Task GlobalLimitSurvivesRepeatedReconnects()
    {
        var operations = new BoundedMediaOperation(4, 2);
        var pending = Enumerable.Range(0, 4).Select(_ => Completion<string>()).ToArray();
        foreach (var completion in pending)
            await Assert.ThrowsExactlyAsync<TimeoutException>(() => operations.RunAsync(new object(), _ => completion.Task, Deadline, CancellationToken.None));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => operations.RunAsync(new object(), _ => Task.FromResult("Unbounded"), Deadline, CancellationToken.None));
        Assert.AreEqual(4, operations.Outstanding);
        foreach (var completion in pending) completion.SetResult("Retired");
        await DrainAsync(operations);
    }

    [TestMethod]
    public async Task LateResourceIsDisposedByItsOriginalOwnerAfterWaitTimesOut()
    {
        var operations = new BoundedMediaOperation();
        var acquired = Completion<OwnedResource>();
        var resource = new OwnedResource();
        await Assert.ThrowsExactlyAsync<TimeoutException>(() => operations.RunAsync(new object(), async _ =>
        {
            using var stream = await acquired.Task;
            return stream.Read();
        }, Deadline, CancellationToken.None));
        Assert.IsFalse(resource.Disposed);
        acquired.SetResult(resource);
        await DrainAsync(operations);
        Assert.IsTrue(resource.Disposed);
    }

    [TestMethod]
    public async Task SlowCancellationCallbackCannotBlockTheCallerOrReleaseItsOwnershipEarly()
    {
        var operations = new BoundedMediaOperation();
        var native = Completion<string>();
        using var callbackMayExit = new ManualResetEventSlim();
        var callbackEntered = Completion<bool>();
        var task = operations.RunAsync(new object(), token =>
        {
            token.Register(() => { callbackEntered.TrySetResult(true); callbackMayExit.Wait(TestDeadline); });
            return native.Task;
        }, Deadline, CancellationToken.None);
        try
        {
            await Assert.ThrowsExactlyAsync<TimeoutException>(() => task.WaitAsync(TestDeadline));
            await callbackEntered.Task.WaitAsync(TestDeadline);
            native.SetResult("Late");
            Assert.AreEqual(1, operations.Outstanding);
        }
        finally { callbackMayExit.Set(); }
        await DrainAsync(operations);
    }

    [TestMethod]
    public async Task SynchronousFailureAndAlreadyCanceledRequestDoNotConsumeSlots()
    {
        var operations = new BoundedMediaOperation();
        await Assert.ThrowsExactlyAsync<IOException>(() => operations.RunAsync<string>(new object(), _ => throw new IOException(), Deadline, CancellationToken.None));
        using var stop = new CancellationTokenSource();
        await stop.CancelAsync();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => operations.RunAsync(new object(), _ => Task.FromResult("Never"), Deadline, stop.Token));
        Assert.AreEqual(0, operations.Outstanding);
    }

    [TestMethod]
    public async Task SynchronousNativeFactoryCannotBlockTheCallingThread()
    {
        var operations = new BoundedMediaOperation();
        using var release = new ManualResetEventSlim();
        var started = Completion<bool>();
        var call = operations.RunAsync(new object(), _ =>
        {
            started.TrySetResult(true);
            release.Wait(TestDeadline);
            return Task.FromResult("Retired result");
        }, Deadline, CancellationToken.None);
        try
        {
            await started.Task.WaitAsync(TestDeadline);
            await Assert.ThrowsExactlyAsync<TimeoutException>(() => call.WaitAsync(TestDeadline));
            Assert.AreEqual(1, operations.Outstanding);
            Assert.AreEqual("Healthy publisher", await operations.RunAsync(new object(), _ => Task.FromResult("Healthy publisher"), TestDeadline, CancellationToken.None));
        }
        finally { release.Set(); }
        await DrainAsync(operations);
    }

    [TestMethod]
    public async Task LateSubscriptionSetupIsFencedAndRemovedAfterRetirement()
    {
        var operations = new BoundedMediaOperation(4, 2);
        var subscription = new MediaEventSubscription();
        using var release = new ManualResetEventSlim();
        var entered = Completion<bool>();
        var detached = Completion<bool>();
        var callbacks = 0;
        var call = subscription.StartAsync(operations, new object(), () =>
        {
            entered.TrySetResult(true);
            release.Wait(TestDeadline);
            if (subscription.IsActive) callbacks++;
        }, () => detached.TrySetResult(true), Deadline, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TestDeadline);
            await Assert.ThrowsExactlyAsync<TimeoutException>(() => call.WaitAsync(TestDeadline));
            Assert.IsFalse(subscription.IsActive);
            Assert.AreEqual(1, operations.Outstanding);
        }
        finally { release.Set(); }
        await detached.Task.WaitAsync(TestDeadline);
        await DrainAsync(operations);
        Assert.AreEqual(0, callbacks);
    }

    [TestMethod]
    public async Task RetiringSubscriptionDoesNotAwaitHungNativeRemove()
    {
        var operations = new BoundedMediaOperation(4, 2);
        var subscription = new MediaEventSubscription();
        using var release = new ManualResetEventSlim();
        var entered = Completion<bool>();
        await subscription.StartAsync(operations, new object(), () => { }, () =>
        {
            entered.TrySetResult(true);
            release.Wait(TestDeadline);
        }, TestDeadline, CancellationToken.None);
        try
        {
            subscription.Retire();
            await entered.Task.WaitAsync(TestDeadline);
            Assert.IsFalse(subscription.IsActive);
            Assert.AreEqual(1, operations.Outstanding);
            var replacement = new MediaEventSubscription();
            await replacement.StartAsync(operations, new object(), () => { }, () => { }, TestDeadline, CancellationToken.None);
            replacement.Retire();
        }
        finally { release.Set(); }
        await DrainAsync(operations);
    }

    private static async Task DrainAsync(BoundedMediaOperation operations)
    {
        using var timeout = new CancellationTokenSource(TestDeadline);
        while (operations.Outstanding != 0) await Task.Delay(1, timeout.Token);
    }

    private sealed class OwnedResource : IDisposable
    {
        public bool Disposed { get; private set; }
        public string Read() { Assert.IsFalse(Disposed); return "image bytes"; }
        public void Dispose() => Disposed = true;
    }
}
