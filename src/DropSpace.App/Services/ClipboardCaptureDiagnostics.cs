using System.Text.Json;
using System.Text.Json.Serialization;

namespace DropSpace.App.Services;

internal enum ClipboardSmokeStage
{
    Initial, FirstTextWrite, FirstTextNotification, FirstTextCapture,
    ConsecutiveTextWrite, ConsecutiveTextNotification, ConsecutiveSuppression,
    SecondTextWrite, SecondTextCapture, NonConsecutiveTextWrite, NonConsecutiveTextCapture,
    FileWrite, FileCapture, Pause, PausedTextWrite, PausedTextNotification, PauseVerification,
    Resume, ResumedTextWrite, ResumedTextCapture, SelfWrite, SelfWriteNotification,
    SelfWriteVerification, Complete,
}

internal enum ClipboardDiagnosticDecision
{
    StageStarted, WaitStarted, WaitProgress, WaitCompleted, SmokeCompleted, SmokeFailed,
    Notification, SignalQueued, SignalDropped, SignalDequeued, PausedSignal,
    DuplicateSequence, SequenceConsumed, ReadAttempt, DispatcherQueued, ClipboardViewReadStarted, ClipboardViewRead,
    TextReadStarted, TextOperationCreated, TextReadCompleted, StorageItemsRead, BitmapRead,
    EmptyText, UnsupportedFormat, ReadCompleted, ReadFailed, RetrySequenceAdvanced,
    SnapshotUnavailable, StaleSequence, SelfWriteSuppressed, CommitGateWaiting,
    CommitGateAcquired, RepositoryCommitStarted, RepositoryCommitCompleted,
    ConsecutiveSuppressed, NoItemsCommitted, ItemsCaptured, CaptureFailed,
    WorkerStarted, WorkerCancelled, WorkerFailed, WorkerCompleted,
}

internal enum ClipboardDiagnosticFailure { None, Cancelled, Timeout, InvalidOperation, Win32, Com, Access, Io, Other }
internal enum ClipboardSmokeProfile { Baseline, Immediate, DispatcherYield, FixedDelay }

internal sealed record ClipboardDiagnosticCounters(
    long Observed, long ReadAttempts, long Captured, long ConsecutiveSuppressed,
    long FailedReads, long DroppedSignals, long EnqueuedSignals, long DequeuedSignals,
    int OutstandingNativeTextReads = 0, int RetiredNativeTextReads = 0);

internal sealed record ClipboardDiagnosticState(
    DateTimeOffset AtUtc, uint? NativeSequence, uint LastNotificationSequence,
    uint LastProcessedSequence, ClipboardDiagnosticCounters Counters,
    bool ListenerRegistered, bool Paused, int PauseGeneration, bool Initialized,
    bool Disposing, TaskStatus? WorkerStatus);

internal sealed record ClipboardDiagnosticEvent(
    long Index, ClipboardSmokeProfile Profile, int Cycle, ClipboardSmokeStage Stage, ClipboardDiagnosticDecision Decision,
    uint? SignalSequence, int? SignalAttempt, int? ReadAttempt, ClipboardDiagnosticFailure Failure,
    int? HResult, ClipboardDiagnosticState State);

internal sealed record ClipboardDiagnosticStep(
    ClipboardSmokeProfile Profile, int Cycle, ClipboardSmokeStage Stage, ClipboardDiagnosticState Started, ClipboardDiagnosticState Latest);

internal sealed record ClipboardDiagnosticDocument(
    int SchemaVersion, int ProcessId, int EventCapacity, int SignalQueueCapacity, long DiscardedEvents,
    ClipboardSmokeProfile Profile, int Cycle, ClipboardSmokeStage Stage, bool Completed, bool Failed,
    ClipboardDiagnosticState Current, ClipboardDiagnosticEvent? Failure,
    IReadOnlyList<ClipboardDiagnosticStep> Steps, IReadOnlyList<ClipboardDiagnosticEvent> Events);

/// <summary>Smoke-only bounded metadata. No payload, path, fingerprint or exception message is accepted.</summary>
internal sealed class ClipboardDiagnosticTrace
{
    // Retain the complete fixed smoke plan while staying below the 4 MiB retention bound.
    internal const int EventCapacity = 2_048;
    private readonly object _gate = new();
    private readonly Queue<ClipboardDiagnosticEvent> _events = new();
    private readonly List<ClipboardDiagnosticStep> _steps = [];
    private int _enabled;
    private long _nextIndex;
    private long _discarded;
    private ClipboardSmokeStage _stage;
    private ClipboardSmokeProfile _profile;
    private int _cycle;
    private bool _completed;
    private ClipboardDiagnosticEvent? _failure;

    internal bool IsEnabled => Volatile.Read(ref _enabled) != 0;

    internal void Disable() => Volatile.Write(ref _enabled, 0);

    internal void Enable()
    {
        lock (_gate)
        {
            _events.Clear();
            _steps.Clear();
            _nextIndex = _discarded = 0;
            _stage = ClipboardSmokeStage.Initial;
            _profile = ClipboardSmokeProfile.Baseline;
            _cycle = 0;
            _completed = false;
            _failure = null;
            Volatile.Write(ref _enabled, 1);
        }
    }

    internal void BeginStage(ClipboardSmokeStage stage, ClipboardDiagnosticState state,
        ClipboardSmokeProfile profile = ClipboardSmokeProfile.Baseline, int cycle = 0)
    {
        lock (_gate)
        {
            _stage = stage;
            _profile = profile;
            _cycle = cycle;
            // Original stages plus the fixed twelve controlled cases remain bounded.
            if (_steps.Count < 256) _steps.Add(new(profile, cycle, stage, state, state));
            RecordCore(ClipboardDiagnosticDecision.StageStarted, state, null, null, null, ClipboardDiagnosticFailure.None, null);
        }
    }

    internal void Record(ClipboardDiagnosticDecision decision, ClipboardDiagnosticState state,
        uint? signalSequence = null, int? signalAttempt = null, int? readAttempt = null,
        ClipboardDiagnosticFailure failure = ClipboardDiagnosticFailure.None, int? hResult = null)
    {
        lock (_gate) RecordCore(decision, state, signalSequence, signalAttempt, readAttempt, failure, hResult);
    }

    private void RecordCore(ClipboardDiagnosticDecision decision, ClipboardDiagnosticState state,
        uint? signalSequence, int? signalAttempt, int? readAttempt, ClipboardDiagnosticFailure failure, int? hResult)
    {
        var entry = new ClipboardDiagnosticEvent(++_nextIndex, _profile, _cycle, _stage, decision, signalSequence, signalAttempt, readAttempt, failure, hResult, state);
        if (_events.Count == EventCapacity) { _events.Dequeue(); _discarded++; }
        _events.Enqueue(entry);
        if (_steps.Count > 0) _steps[^1] = _steps[^1] with { Latest = state };
        if (decision == ClipboardDiagnosticDecision.SmokeFailed) _failure ??= entry;
        if (decision == ClipboardDiagnosticDecision.SmokeCompleted) _completed = true;
    }

    internal ClipboardDiagnosticDocument Snapshot(ClipboardDiagnosticState current)
    {
        lock (_gate) return new(1, Environment.ProcessId, EventCapacity, 128, _discarded, _profile, _cycle, _stage, _completed,
            _failure is not null, current, _failure, _steps.ToArray(), _events.ToArray());
    }

    internal static ClipboardDiagnosticFailure Classify(Exception? exception) => exception switch
    {
        null => ClipboardDiagnosticFailure.None,
        OperationCanceledException => ClipboardDiagnosticFailure.Cancelled,
        TimeoutException => ClipboardDiagnosticFailure.Timeout,
        InvalidOperationException => ClipboardDiagnosticFailure.InvalidOperation,
        System.ComponentModel.Win32Exception => ClipboardDiagnosticFailure.Win32,
        System.Runtime.InteropServices.COMException => ClipboardDiagnosticFailure.Com,
        UnauthorizedAccessException => ClipboardDiagnosticFailure.Access,
        IOException => ClipboardDiagnosticFailure.Io,
        _ => ClipboardDiagnosticFailure.Other,
    };
}

internal static class ClipboardDiagnosticWriter
{
    internal const string FileName = "clipboard-smoke-diagnostics.json";
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static bool TryWrite(string logDirectory, ClipboardDiagnosticDocument document)
    {
        string? temporary = null;
        try
        {
            temporary = Path.Combine(logDirectory, FileName + ".tmp");
            Directory.CreateDirectory(logDirectory);
            File.WriteAllText(temporary, JsonSerializer.Serialize(document, Options));
            File.Move(temporary, Path.Combine(logDirectory, FileName), overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Diagnostics cannot replace a smoke assertion or its original failure.
            try { if (temporary is not null) File.Delete(temporary); } catch (Exception cleanup) when (cleanup is not OutOfMemoryException) { }
            return false;
        }
    }
}

/// <summary>One smoke-owned writer checkpoints independently of a blocked UI/native read.</summary>
internal sealed class ClipboardDiagnosticSession : IAsyncDisposable
{
    private readonly string _logDirectory;
    private readonly Func<ClipboardDiagnosticDocument> _snapshot;
    private readonly Action? _onDisposed;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly object _gate = new();
    private readonly Task _worker;
    private ClipboardDiagnosticDocument? _pending;
    private TaskCompletionSource? _pendingCompletion;
    private int _disposed;

    internal ClipboardDiagnosticSession(string logDirectory, Func<ClipboardDiagnosticDocument> snapshot, Action? onDisposed = null)
    {
        _logDirectory = logDirectory;
        _snapshot = snapshot;
        _onDisposed = onDisposed;
        _worker = Task.Run(WriteLoopAsync);
    }

    internal void RequestFlush()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        SignalWriter();
    }

    private void SignalWriter()
    {
        // Coalesce stage checkpoints; all filesystem writes belong to the one worker.
        try { _signal.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    internal async Task FlushAsync()
    {
        Task completion;
        lock (_gate)
        {
            _pending = _snapshot();
            _pendingCompletion ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            completion = _pendingCompletion.Task;
        }
        SignalWriter();
        try { await completion.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException) { /* The periodic snapshot remains owned by the writer. */ }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _signal.WaitAsync(TimeSpan.FromSeconds(1), _stop.Token).ConfigureAwait(false);
                ClipboardDiagnosticDocument? pending;
                TaskCompletionSource? completion;
                lock (_gate)
                {
                    pending = _pending;
                    completion = _pendingCompletion;
                    _pending = null;
                    _pendingCompletion = null;
                }
                try { ClipboardDiagnosticWriter.TryWrite(_logDirectory, pending ?? _snapshot()); }
                catch (Exception exception) when (exception is not OutOfMemoryException) { }
                finally { completion?.TrySetResult(); }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally
        {
            _stop.Dispose();
            _signal.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            await FlushAsync().ConfigureAwait(false);
            try { _stop.Cancel(); } catch (ObjectDisposedException) { }
            try { await _worker.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (TimeoutException) { /* Keep writer resources owned until its I/O finishes. */ }
            catch (Exception exception) when (exception is not OutOfMemoryException) { }
        }
        finally { _onDisposed?.Invoke(); }
    }
}
