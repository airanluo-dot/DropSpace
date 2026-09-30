using System.Reflection;
using System.Text.Json;
using DropSpace.App.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class ClipboardDiagnosticsTests
{
    private static ClipboardDiagnosticState State(long observed = 0) => new(
        DateTimeOffset.UtcNow, 12, 12, 11, new(observed, 0, 0, 0, 0, 0, 0, 0),
        true, false, 0, true, false, TaskStatus.WaitingForActivation);

    [TestMethod]
    public void OverflowRetainsLatestEventsAndFailureBeforeCleanup()
    {
        var trace = new ClipboardDiagnosticTrace();
        trace.Enable();
        trace.BeginStage(ClipboardSmokeStage.SecondTextCapture, State(), ClipboardSmokeProfile.Immediate, 4);
        trace.Record(ClipboardDiagnosticDecision.SmokeFailed, State(9), failure: ClipboardDiagnosticFailure.Timeout);
        for (var i = 0; i < ClipboardDiagnosticTrace.EventCapacity + 5; i++)
            trace.Record(ClipboardDiagnosticDecision.Notification, State(i));
        var snapshot = trace.Snapshot(State(99));
        Assert.AreEqual(ClipboardDiagnosticTrace.EventCapacity, snapshot.Events.Count);
        Assert.AreEqual(7L, snapshot.DiscardedEvents);
        Assert.IsTrue(snapshot.Failed);
        Assert.AreEqual(9L, snapshot.Failure!.State.Counters.Observed);
        Assert.AreEqual(ClipboardSmokeProfile.Immediate, snapshot.Failure.Profile);
        Assert.AreEqual(4, snapshot.Failure.Cycle);
    }

    [TestMethod]
    public void MetadataSeparatesReadAndSignalRetriesWithoutPayloadFields()
    {
        var trace = new ClipboardDiagnosticTrace();
        trace.Enable();
        trace.Record(ClipboardDiagnosticDecision.ReadFailed, State(), 12, signalAttempt: 2, readAttempt: 7,
            failure: ClipboardDiagnosticTrace.Classify(new InvalidOperationException("private clipboard text/token/path")));
        var snapshot = trace.Snapshot(State());
        Assert.AreEqual(Environment.ProcessId, snapshot.ProcessId);
        var entry = snapshot.Events.Single();
        Assert.AreEqual(2, entry.SignalAttempt);
        Assert.AreEqual(7, entry.ReadAttempt);
        Assert.AreEqual(ClipboardDiagnosticFailure.InvalidOperation, entry.Failure);
        var json = JsonSerializer.Serialize(snapshot);
        Assert.IsFalse(json.Contains("private clipboard", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("Fingerprint", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("Message", StringComparison.Ordinal));
        Assert.AreEqual(ClipboardDiagnosticFailure.Timeout, ClipboardDiagnosticTrace.Classify(new TimeoutException()));
        Assert.AreEqual(ClipboardDiagnosticFailure.Win32, ClipboardDiagnosticTrace.Classify(new System.ComponentModel.Win32Exception(5)));
    }

    [TestMethod]
    public void DiagnosticWriteFailureDoesNotReplaceTheSmokeFailure()
    {
        var path = Path.Combine(Path.GetTempPath(), "DropSpace-diagnostics-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(path, "not a directory");
            var trace = new ClipboardDiagnosticTrace();
            trace.Enable();
            trace.Record(ClipboardDiagnosticDecision.SmokeFailed, State(), failure: ClipboardDiagnosticFailure.Timeout);
            Assert.IsFalse(ClipboardDiagnosticWriter.TryWrite(path, trace.Snapshot(State())));
            Assert.IsFalse(ClipboardDiagnosticWriter.TryWrite(null!, trace.Snapshot(State())));
            Assert.AreEqual(ClipboardDiagnosticFailure.Timeout, trace.Snapshot(State()).Failure!.Failure);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task BackgroundCheckpointPersistsWhileTheOwnerDoesNotPumpItsContext()
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-diagnostics-" + Guid.NewGuid().ToString("N"));
        var trace = new ClipboardDiagnosticTrace();
        trace.Enable();
        trace.BeginStage(ClipboardSmokeStage.SecondTextCapture, State());
        trace.Record(ClipboardDiagnosticDecision.TextReadStarted, State(17), signalSequence: 12);
        using var ownerRelease = new ManualResetEventSlim();
        var created = new TaskCompletionSource<ClipboardDiagnosticSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            created.SetResult(new ClipboardDiagnosticSession(root, () => trace.Snapshot(State(17))));
            ownerRelease.Wait();
        }) { IsBackground = true };
        owner.Start();
        try
        {
            await using var session = await created.Task.WaitAsync(TimeSpan.FromSeconds(4));
            // No request: the independent one-second checkpoint must retain an in-flight read.
            var file = Path.Combine(root, ClipboardDiagnosticWriter.FileName);
            var deadline = DateTimeOffset.UtcNow.AddSeconds(4);
            while (!File.Exists(file) && DateTimeOffset.UtcNow < deadline) await Task.Delay(20);
            Assert.IsTrue(File.Exists(file));
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(file));
            Assert.AreEqual(17, document.RootElement.GetProperty("current").GetProperty("counters").GetProperty("observed").GetInt32());
            Assert.AreEqual("TextReadStarted", document.RootElement.GetProperty("events")[1].GetProperty("decision").GetString());
            Assert.IsTrue(owner.IsAlive);
        }
        finally
        {
            ownerRelease.Set();
            owner.Join();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public async Task InstrumentedNotificationAdmissionRetainsRealQueueDropAccounting()
    {
        await using var capture = new ClipboardCaptureService(null!, null!, null!, null!, null!, null!, null!, null!,
            NullLogger<ClipboardCaptureService>.Instance);
        capture.BeginDiagnosticSession();
        var callback = typeof(ClipboardCaptureService).GetMethod("OnClipboardChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (uint sequence = 1; sequence <= 200; sequence++)
            callback.Invoke(capture, [null, new ClipboardNotification(sequence, DateTimeOffset.UtcNow)]);
        var snapshot = capture.DiagnosticSnapshot;
        Assert.AreEqual(200L, snapshot.Current.Counters.Observed);
        Assert.AreEqual(200L, snapshot.Current.Counters.EnqueuedSignals);
        Assert.AreEqual(72L, snapshot.Current.Counters.DroppedSignals);
        Assert.AreEqual(200u, snapshot.Current.LastNotificationSequence);
        Assert.AreEqual(72, snapshot.Events.Count(entry => entry.Decision == ClipboardDiagnosticDecision.SignalDropped));
    }
}
