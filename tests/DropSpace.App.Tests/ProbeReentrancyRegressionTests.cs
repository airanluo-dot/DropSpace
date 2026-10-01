using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using DropSpace.App.Services;
using DropSpace.Core.DragDrop;
using Microsoft.Extensions.Logging.Abstractions;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class ProbeReentrancyRegressionTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NestedSourceMessageCannotRetireProbeInsideDragEnter(bool directDispose)
    {
        var type = typeof(EphemeralOleDragProbe);
        var probe = (EphemeralOleDragProbe)RuntimeHelpers.GetUninitializedObject(type);
        Set(probe, "_completionGate", new object());
        Set(probe, "_logger", NullLogger.Instance);
        Set(probe, "_ownerThreadId", GetCurrentThreadId());
        Set(probe, "_createdTimestamp", Stopwatch.GetTimestamp());
        Set(probe, "_completed", (Action<OleDragProbeResult>)(_ => { }));
        uint queued = 0;
        Set(probe, "_postCompletion", (Func<nint, uint, nint, nint, bool>)((_, message, _, _) => { queued = message; return true; }));
        var targetType = type.GetNestedType("ProbeDropTarget", BindingFlags.NonPublic)!;
        var target = (IOleDropTarget)Activator.CreateInstance(targetType,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null,
            [probe, new OleFileDataClassifier(), NullLogger.Instance], null)!;
        bool? retiredInsideSource = null;
        var source = new ReentrantDataObject(() =>
        {
            if (directDispose) probe.Dispose();
            else
            {
                type.GetMethod("QueueCompletion", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(probe,
                    [OleDragProbeOutcome.TimedOut, OleFileDataClassification.None, new DragScreenPoint(0, 0)]);
                // IDataObject is foreign COM code; its nested pump may dispatch a posted message
                // before QueryGetData, and therefore DragEnter, returns.
                type.GetMethod("HandleOwnerThreadMessage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(probe, [queued]);
            }
            retiredInsideSource = probe.IsDisposed;
        });
        try
        {
            uint effect = 0;
            target.DragEnter(source, 0, default, ref effect);
            Assert.AreEqual(false, retiredInsideSource, "Native probe lifetime must cover the complete OLE callback, even during a nested message pump.");
            Assert.IsFalse(probe.IsDisposed);
            Assert.AreNotEqual(0u, queued);
            type.GetMethod("HandleOwnerThreadMessage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(probe, [queued]);
            Assert.IsTrue(probe.IsDisposed);
        }
        finally { probe.Dispose(); }
    }

    private static void Set(object value, string name, object field) =>
        value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(value, field);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    private sealed class ReentrantDataObject(Action callback) : IDataObject
    {
        private bool _called;
        public int QueryGetData(ref FORMATETC format) { if (!_called) { _called = true; callback(); } return 1; }
        public void GetData(ref FORMATETC format, out STGMEDIUM medium) => throw new NotSupportedException();
        public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new NotSupportedException();
        public int GetCanonicalFormatEtc(ref FORMATETC input, out FORMATETC output) { output = default; return 1; }
        public void SetData(ref FORMATETC format, ref STGMEDIUM medium, bool release) => throw new NotSupportedException();
        public IEnumFORMATETC EnumFormatEtc(DATADIR direction) => throw new NotSupportedException();
        public int DAdvise(ref FORMATETC format, ADVF flags, IAdviseSink sink, out int connection) { connection = 0; return 1; }
        public void DUnadvise(int connection) => throw new NotSupportedException();
        public int EnumDAdvise(out IEnumSTATDATA? advice) { advice = null; return 1; }
    }
}
