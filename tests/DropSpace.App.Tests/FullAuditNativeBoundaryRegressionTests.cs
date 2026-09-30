using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DropSpace.App.Services;
using DropSpace.Core.DragDrop;
using Microsoft.Extensions.Logging.Abstractions;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class FullAuditNativeBoundaryRegressionTests
{
    [TestMethod]
    public void TrayCallbackContinuesAfterSubscriberFailure()
    {
        var tray = (NativeTrayService)RuntimeHelpers.GetUninitializedObject(typeof(NativeTrayService));
        SetField(tray, "_logger", NullLogger<NativeTrayService>.Instance);
        var calls = 0;
        tray.OpenRequested += (_, _) => throw new InvalidOperationException("subscriber failure");
        tray.OpenRequested += (_, _) => calls++;
        var callback = typeof(NativeTrayService).GetMethod("WindowSubclassProc", BindingFlags.Instance | BindingFlags.NonPublic)!;
        callback.Invoke(tray, [nint.Zero, 0x8001u, nint.Zero, new nint(0x0202), UIntPtr.Zero, UIntPtr.Zero]);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public void DisplayCallbackContinuesAfterSubscriberFailure()
    {
        var watcher = (DisplayTopologyWatcher)RuntimeHelpers.GetUninitializedObject(typeof(DisplayTopologyWatcher));
        typeof(DisplayTopologyWatcher).GetField("_logger", BindingFlags.Instance | BindingFlags.NonPublic)?
            .SetValue(watcher, NullLogger<DisplayTopologyWatcher>.Instance);
        var calls = 0;
        watcher.Changed += (_, _) => throw new InvalidOperationException("subscriber failure");
        watcher.Changed += (_, _) => calls++;
        var type = typeof(DisplayTopologyWatcher);
        var watchers = (IDictionary)type.GetField("Watchers", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var handle = new nint(0x1234);
        watchers.Add(handle, watcher);
        try
        {
            type.GetMethod("StaticWindowProcedure", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [handle, 0x007Eu, nint.Zero, nint.Zero]);
            Assert.AreEqual(1, calls);
        }
        finally { watchers.Remove(handle); }
    }

    [TestMethod]
    public void ProbeCompletionWaitsForPostedOwnerMessageEvenOnOwnerThread()
    {
        var probe = (EphemeralOleDragProbe)RuntimeHelpers.GetUninitializedObject(typeof(EphemeralOleDragProbe));
        SetField(probe, "_completionGate", new object());
        SetField(probe, "_logger", NullLogger.Instance);
        SetField(probe, "_ownerThreadId", GetCurrentThreadId());
        SetField(probe, "_createdTimestamp", Stopwatch.GetTimestamp());
        var completions = 0;
        uint postedMessage = 0;
        SetField(probe, "_completed", (Action<OleDragProbeResult>)(_ => completions++));
        SetField(probe, "_postCompletion", (Func<nint, uint, nint, nint, bool>)((_, message, _, _) =>
        { postedMessage = message; return true; }));
        try
        {
            typeof(EphemeralOleDragProbe).GetMethod("QueueCompletion", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(probe, [OleDragProbeOutcome.Rejected, OleFileDataClassification.None, new DragScreenPoint(0, 0)]);
            Assert.AreEqual(0, completions, "The OLE callback must return before result handling and native teardown.");
            Assert.IsFalse(probe.IsDisposed);
            Assert.AreNotEqual(0u, postedMessage);
            typeof(EphemeralOleDragProbe).GetMethod("HandleOwnerThreadMessage", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(probe, [postedMessage]);
            Assert.AreEqual(1, completions);
            Assert.IsTrue(probe.IsDisposed);
        }
        finally { probe.Dispose(); }
    }

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
