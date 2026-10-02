using System.Reflection;
using System.Runtime.CompilerServices;
using DropSpace.App.Services;
using DropSpace.Core.DragDrop;
using Microsoft.Extensions.Logging.Abstractions;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class OleCallbackBoundaryTests
{
    [TestMethod]
    public async Task LateDropFailureCannotCancelAReplacementGesture()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var generation = 1;
        var left = 0;
        var visualCompletions = 0;
        var callbackReached = false;
        var target = CreateTarget(new DragActivationCallbacks(_ => { }, (_, _) => { }, _ => left++,
            (_, _) => throw new AssertFailedException("Guarded callback must be used."),
            CaptureGuard: () => { var captured = generation; return () => captured == generation; },
            GuardedDropped: async (_, _, current) =>
            {
                callbackReached = true;
                await release.Task;
                if (current()) visualCompletions++;
                throw new IOException("Delayed original drop failure.");
            }));
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var guard = (Func<bool>)typeof(OleDropTargetRegistration).GetMethod("CaptureCompletionGuard", flags)!.Invoke(target, null)!;
        var completion = (Task)typeof(OleDropTargetRegistration).GetMethod("CompleteDropAsync", flags)!.Invoke(target, [Array.Empty<string>(), guard])!;
        Assert.IsTrue(callbackReached);
        generation++;
        release.SetResult();
        await completion;
        Assert.AreEqual(0, visualCompletions);
        Assert.AreEqual(0, left);
    }

    [TestMethod]
    public void DragEnterCallbackFailureRejectsAndClearsNativeOwnership()
    {
        var target = CreateTarget(new DragActivationCallbacks(
            _ => throw new InvalidOperationException("Cannot reveal the target."),
            (_, _) => { }, _ => { }, (_, _) => Task.CompletedTask));
        using var data = new CfHDropDataObject([@"C:\DropSpace-boundary-test.txt"]);
        uint effect = 1;
        Assert.AreEqual(0, target.DragEnter(data, 0, new NativePoint(0, 0), ref effect));
        Assert.AreEqual(0u, effect);
        effect = 1;
        Assert.AreEqual(0, target.DragOver(0, new NativePoint(0, 0), ref effect));
        Assert.AreEqual(0u, effect, "A failed reveal cannot retain accepted OLE ownership.");
    }

    [TestMethod]
    public void DragOverCallbackFailureRejectsAndContainsFailingCleanup()
    {
        var target = CreateTarget(new DragActivationCallbacks(_ => { },
            (_, _) => throw new InvalidOperationException("Cannot update the target."),
            _ => throw new InvalidOperationException("Cannot reset the target."),
            (_, _) => Task.CompletedTask));
        SetField(target, "_canAccept", true);
        uint effect = 1;
        Assert.AreEqual(0, target.DragOver(0, new NativePoint(0, 0), ref effect));
        Assert.AreEqual(0u, effect);
    }

    [TestMethod]
    public void DragLeaveCallbackFailureStillReleasesOwnership()
    {
        var target = CreateTarget(new DragActivationCallbacks(_ => { }, (_, _) => { },
            _ => throw new InvalidOperationException("Cannot reset the target."),
            (_, _) => Task.CompletedTask));
        SetField(target, "_canAccept", true);
        Assert.AreEqual(0, target.DragLeave());
        uint effect = 1;
        target.DragOver(0, new NativePoint(0, 0), ref effect);
        Assert.AreEqual(0u, effect);
    }

    [TestMethod]
    public void RejectedDropContainsFailingCleanup()
    {
        var target = CreateTarget(new DragActivationCallbacks(_ => { }, (_, _) => { },
            _ => throw new InvalidOperationException("Cannot reset the target."),
            (_, _) => Task.CompletedTask));
        using var data = new CfHDropDataObject([@"C:\DropSpace-boundary-test.txt"]);
        uint effect = 1;
        Assert.AreEqual(0, target.Drop(data, 0, new NativePoint(0, 0), ref effect));
        Assert.AreEqual(0u, effect);
    }

    [TestMethod]
    public void HealthyDragOverRetainsCopyAcceptance()
    {
        var readyCalls = 0;
        var target = CreateTarget(new DragActivationCallbacks(_ => { }, (_, ready) =>
            { Assert.IsTrue(ready); readyCalls++; }, _ => { }, (_, _) => Task.CompletedTask));
        SetField(target, "_canAccept", true);
        uint effect = 0;
        Assert.AreEqual(0, target.DragOver(0, new NativePoint(0, 0), ref effect));
        Assert.AreEqual(1u, effect);
        Assert.AreEqual(1, readyCalls);
    }

    [TestMethod]
    public void DisplayChangeNotifiesLaterSubscriberAfterOneSubscriberFails()
    {
        // Test the callback entry point without creating a second native activation HWND.
        var host = (DragActivationHost)RuntimeHelpers.GetUninitializedObject(typeof(DragActivationHost));
        SetField(host, "_logger", NullLogger<DragActivationHost>.Instance);
        var notifications = 0;
        host.DisplayTopologyChanged += (_, _) => throw new InvalidOperationException("Subscriber failed.");
        host.DisplayTopologyChanged += (_, _) => notifications++;
        var hosts = (Dictionary<nint, DragActivationHost>)typeof(DragActivationHost)
            .GetField("Hosts", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var classGate = typeof(DragActivationHost)
            .GetField("ClassGate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var window = new nint(-4453);
        lock (classGate) hosts.Add(window, host);
        try
        {
            var callback = typeof(DragActivationHost)
                .GetMethod("StaticWindowProcedure", BindingFlags.Static | BindingFlags.NonPublic)!;
            callback.Invoke(null, [window, (uint)0x007E, nint.Zero, nint.Zero]);
            Assert.AreEqual(1, notifications);
        }
        finally { lock (classGate) hosts.Remove(window); }
    }

    [TestMethod]
    public void RetiredTargetRejectsAllLateNativeCallbacksWithoutReadingData()
    {
        var target = CreateTarget(new DragActivationCallbacks(
            _ => Assert.Fail("Retired target must not reveal."),
            (_, _) => Assert.Fail("Retired target must not update."),
            _ => Assert.Fail("Retired target must not notify."),
            (_, _) => throw new AssertFailedException("Retired target must not drop.")));
        SetField(target, "_disposed", true);
        SetField(target, "_canAccept", true);
        uint effect = 1;
        Assert.AreEqual(0, target.DragEnter(null!, 0, default, ref effect));
        Assert.AreEqual(0u, effect);
        effect = 1;
        Assert.AreEqual(0, target.DragOver(0, default, ref effect));
        Assert.AreEqual(0u, effect);
        effect = 1;
        Assert.AreEqual(0, target.Drop(null!, 0, default, ref effect));
        Assert.AreEqual(0u, effect);
        Assert.AreEqual(0, target.DragLeave());
    }

    private static OleDropTargetRegistration CreateTarget(DragActivationCallbacks callbacks)
    {
        // Native registration is unrelated to failure containment inside the callback body.
        var target = (OleDropTargetRegistration)RuntimeHelpers.GetUninitializedObject(typeof(OleDropTargetRegistration));
        SetField(target, "_monitorId", "boundary-test");
        SetField(target, "_surfaceKind", "boundary-test");
        SetField(target, "_callbacks", callbacks);
        SetField(target, "_logger", NullLogger.Instance);
        SetField(target, "_isReady", (Func<NativePoint, bool>)(_ => true));
        SetField(target, "_fileDataClassifier", new OleFileDataClassifier());
        return target;
    }

    private static void SetField<T>(object owner, string name, T value) =>
        owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, value);
}
