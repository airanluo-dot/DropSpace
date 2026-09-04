using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DropSpace.Core.DragDrop;
using DropSpace.Core.Models;
using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services;

public sealed record DragSessionCandidate(
    long SessionId,
    string MonitorId,
    DragScreenPoint Point,
    DragSourceKind Source,
    DragEvidenceLevel EvidenceLevel,
    DragEvidenceFlags Evidence,
    bool RequiresOleVerification,
    DragIntentConfidence DragIntentConfidence,
    PayloadConfidence PayloadConfidence);

/// <summary>
/// Event-driven, non-injecting observer for source-agnostic candidate file drags. It never blocks
/// input and never reads dragged content. Explorer/Desktop item evidence and documented drag-start
/// events provide candidate intent; every Smart candidate requires bounded OLE verification before
/// the application reveals its visual target.
/// </summary>
public sealed class DragSessionDetector : IDisposable, IAsyncDisposable
{
    private const int HookMouseLowLevel = 14;
    private const int HookKeyboardLowLevel = 13;
    private const int WindowMessageQuit = 0x0012;
    private const int WindowMessageMouseMove = 0x0200;
    private const int WindowMessageLeftButtonDown = 0x0201;
    private const int WindowMessageLeftButtonUp = 0x0202;
    private const int WindowMessageRightButtonDown = 0x0204;
    private const int WindowMessageRightButtonUp = 0x0205;
    private const int WindowMessageKeyDown = 0x0100;
    private const int WindowMessageSystemKeyDown = 0x0104;
    private const int VirtualKeyEscape = 0x1B;
    private const uint PeekMessageNoRemove = 0;
    private const int SystemMetricHorizontalDrag = 68;
    private const int SystemMetricVerticalDrag = 69;
    private const uint CoInitializeMultithreaded = 0;
    private const int RpcEChangedMode = unchecked((int)0x80010106);
    private const uint EventSystemDragDropStart = 0x000E;
    private const uint EventSystemDragDropEnd = 0x000F;
    private const uint EventObjectDragStart = 0x8021;
    private const uint EventObjectDragCancel = 0x8022;
    private const uint EventObjectDragComplete = 0x8023;
    private const uint WinEventOutOfContext = 0;
    private const uint WinEventSkipOwnProcess = 2;
    private readonly MonitorLayoutService _monitorLayout;
    private readonly ILogger<DragSessionDetector> _logger;
    private readonly DragSignalQueue<DetectorSignal> _criticalSignals = new(reliable: true);
    private readonly DragSignalQueue<DetectorSignal> _moveSignals = new(reliable: false, lossyCapacity: 1);
    private readonly DragSessionPolicy _policy;
    private readonly SemaphoreSlim _lifecycleSemaphore = new(1, 1);
    private readonly object _disposeGate = new();
    private readonly ManualResetEventSlim _hookMessageQueueReady = new(false);
    private readonly ManualResetEventSlim _observerRegistrationReady = new(false);
    private CancellationTokenSource? _runCancellation;
    private Task? _processor;
    private Thread? _hookThread;
    private uint _hookThreadId;
    private HookProcedure? _mouseProcedure;
    private HookProcedure? _keyboardProcedure;
    private WinEventProcedure? _winEventProcedure;
    private nint _mouseHook;
    private nint _keyboardHook;
    private nint _systemDragWinEventHook;
    private nint _dragWinEventHook;
    private CancellationTokenSource? _completionGrace;
    private CancellationTokenSource? _sessionTimeout;
    private readonly ConcurrentDictionary<CancellationTokenSource, Task> _scheduledTasks = new();
    private FileDragWakeMode _mode = FileDragWakeMode.Disabled;
    private bool _disposed;
    private int _disposeRequested;
    private Task? _disposeTask;
    private long _observedSignals;
    private long _recognizedSourceCount;
    private long _rejectedSourceCount;
    private long _comInitializationFailureCount;
    private long _objectDragStartSignalCount;
    private long _systemDragStartSignalCount;
    private long _genericCandidateCount;
    private long _verifiedCandidateCount;
    private long _rejectedCandidateCount;
    private long _probeTimeoutCount;
    private int _mouseHookRegistrationError;
    private int _keyboardHookRegistrationError;
    private int _objectDragHookRegistrationError;
    private int _systemDragHookRegistrationError;
    private int _pointerObservationActive;
    private int _candidateCreationSuppressed;
    private string[] _excludedProcessNames = [];
    private readonly object _latencyGate = new();
    private readonly Queue<double> _probeLatencyMilliseconds = new();
    private long _velocitySlowCount;
    private long _velocityMediumCount;
    private long _velocityFastCount;
    private long _velocityExtremeCount;

    public DragSessionDetector(
        MonitorLayoutService monitorLayout,
        ILogger<DragSessionDetector> logger)
    {
        _monitorLayout = monitorLayout;
        _logger = logger;
        _policy = new DragSessionPolicy(
            GetSystemMetrics(SystemMetricHorizontalDrag),
            GetSystemMetrics(SystemMetricVerticalDrag));
    }

    public event EventHandler<DragSessionCandidate>? CandidateStarted;

    public event EventHandler<DragSessionCandidate>? VerifiedFileDragStarted;

    public event EventHandler<long>? CandidateEnded;

    public event EventHandler? PlacementEditEscapeRequested;

    public bool ObjectDragEventsRegistered => _dragWinEventHook != nint.Zero;

    public bool SystemDragEventsRegistered => _systemDragWinEventHook != nint.Zero;

    public bool MouseObserverRegistered => _mouseHook != nint.Zero;

    public long ObservedSignalCount => Interlocked.Read(ref _observedSignals);

    public long DroppedMoveSignals => _moveSignals.ReplacedWriteCount;

    public long CriticalSignalWriteFailures => _criticalSignals.WriteFailureCount;

    // Kept as a compatibility alias for existing diagnostics consumers. The lossy lane is the
    // only lane where replacement is expected during a healthy run.
    public long DroppedSignalCount => DroppedMoveSignals;

    public long RecognizedSourceCount => Interlocked.Read(ref _recognizedSourceCount);

    public long RejectedSourceCount => Interlocked.Read(ref _rejectedSourceCount);

    public long ComInitializationFailureCount => Interlocked.Read(ref _comInitializationFailureCount);

    public long ObjectDragStartSignalCount => Interlocked.Read(ref _objectDragStartSignalCount);

    public long SystemDragStartSignalCount => Interlocked.Read(ref _systemDragStartSignalCount);

    public long GenericCandidateCount => Interlocked.Read(ref _genericCandidateCount);

    public long VerifiedCandidateCount => Interlocked.Read(ref _verifiedCandidateCount);

    public long RejectedCandidateCount => Interlocked.Read(ref _rejectedCandidateCount);

    public long ProbeTimeoutCount => Interlocked.Read(ref _probeTimeoutCount);

    public string ObserverRegistrationDiagnostics =>
        $"ready={_observerRegistrationReady.IsSet}; " +
        $"mouse={MouseObserverRegistered}/error={Volatile.Read(ref _mouseHookRegistrationError)}; " +
        $"keyboard={_keyboardHook != nint.Zero}/error={Volatile.Read(ref _keyboardHookRegistrationError)}; " +
        $"objectDrag={ObjectDragEventsRegistered}/error={Volatile.Read(ref _objectDragHookRegistrationError)}; " +
        $"systemDrag={SystemDragEventsRegistered}/error={Volatile.Read(ref _systemDragHookRegistrationError)}";

    public bool WaitForObserverRegistration(TimeSpan timeout) =>
        _observerRegistrationReady.Wait(timeout);

    public bool CandidateCreationSuppressed => Volatile.Read(ref _candidateCreationSuppressed) != 0;

    public bool IsVerificationPending(long sessionId) =>
        !_disposed &&
        Volatile.Read(ref _disposeRequested) == 0 &&
        _mode == FileDragWakeMode.SmartExperimental &&
        _policy.IsActive &&
        _policy.ActiveSessionId == sessionId &&
        _policy.ActiveState == DragSessionState.ProbePending &&
        _policy.RequiresOleVerification;

    public void SetPlacementEditing(bool active)
    {
        if (Volatile.Read(ref _disposeRequested) != 0)
        {
            throw new ObjectDisposedException(nameof(DragSessionDetector));
        }

        Volatile.Write(ref _candidateCreationSuppressed, active ? 1 : 0);
        if (active) TryWrite(new DetectorSignal(DetectorSignalKind.AccessibleObjectCancelled, GetCursorPoint()));
    }

    public void SetMode(FileDragWakeMode mode)
    {
        TrackLifecycleTask(SetModeAsync(mode), "mode change");
    }

    public async Task SetModeAsync(
        FileDragWakeMode mode,
        CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposeRequested) != 0)
        {
            throw new ObjectDisposedException(nameof(DragSessionDetector));
        }

        await _lifecycleSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposeRequested) != 0)
            {
                throw new ObjectDisposedException(nameof(DragSessionDetector));
            }

            if (_mode == mode &&
                (mode != FileDragWakeMode.SmartExperimental || _hookThread is { IsAlive: true }))
            {
                return;
            }

            _mode = mode;
            if (mode == FileDragWakeMode.SmartExperimental)
            {
                StartCore();
            }
            else
            {
                // Shutdown waits are deliberately asynchronous. The UI thread can request a mode
                // transition without synchronously waiting for a native hook thread to exit.
                await StopCoreAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _lifecycleSemaphore.Release();
        }
    }

    public void SetExcludedProcesses(IEnumerable<string> processNames)
    {
        ArgumentNullException.ThrowIfNull(processNames);
        Volatile.Write(ref _excludedProcessNames, processNames
            .Select(static name => Path.GetFileNameWithoutExtension(name.Trim()))
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray());
    }

    public void RecordProbeLatency(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero || !double.IsFinite(elapsed.TotalMilliseconds))
        {
            return;
        }
        lock (_latencyGate)
        {
            while (_probeLatencyMilliseconds.Count >= 4_096)
            {
                _probeLatencyMilliseconds.Dequeue();
            }
            _probeLatencyMilliseconds.Enqueue(elapsed.TotalMilliseconds);
        }
    }

    public string CreateCompatibilityReport()
    {
        double[] samples;
        lock (_latencyGate)
        {
            samples = _probeLatencyMilliseconds.Order().ToArray();
        }
        static double Percentile(double[] values, double percentile) => values.Length == 0
            ? 0
            : values[(int)Math.Clamp(Math.Ceiling(percentile * values.Length) - 1, 0, values.Length - 1)];
        return string.Join(Environment.NewLine,
            "DropSpace Smart Drag compatibility report (path-free)",
            $"observer: {ObserverRegistrationDiagnostics}",
            $"signals: observed={ObservedSignalCount}; droppedMoves={DroppedMoveSignals}; criticalWriteFailures={CriticalSignalWriteFailures}",
            $"candidates: generic={GenericCandidateCount}; verified={VerifiedCandidateCount}; rejected={RejectedCandidateCount}; timeout={ProbeTimeoutCount}",
            $"probe-ms: p50={Percentile(samples, .50):F1}; p90={Percentile(samples, .90):F1}; p95={Percentile(samples, .95):F1}; p99={Percentile(samples, .99):F1}",
            $"velocity-buckets: slow={Interlocked.Read(ref _velocitySlowCount)}; medium={Interlocked.Read(ref _velocityMediumCount)}; fast={Interlocked.Read(ref _velocityFastCount)}; extreme={Interlocked.Read(ref _velocityExtremeCount)}",
            $"false-reveal-proxy: {RejectedCandidateCount + ProbeTimeoutCount}");
    }

    public void NotifyOleSessionCompleted(long sessionId)
    {
        TryWrite(new DetectorSignal(
            DetectorSignalKind.OleCompleted,
            GetCursorPoint(),
            SessionId: sessionId));
    }

    public void NotifyProbeVerified(long sessionId, DragScreenPoint point)
    {
        TryWrite(new DetectorSignal(
            DetectorSignalKind.ProbeVerified,
            point,
            SessionId: sessionId));
    }

    public void NotifyProbeRejected(long sessionId, DragScreenPoint point)
    {
        TryWrite(new DetectorSignal(
            DetectorSignalKind.ProbeRejected,
            point,
            SessionId: sessionId));
    }

    public void NotifyProbeTimedOut(long sessionId, DragScreenPoint point)
    {
        TryWrite(new DetectorSignal(
            DetectorSignalKind.ProbeTimedOut,
            point,
            SessionId: sessionId));
    }

    public void Dispose()
    {
        // Keep the synchronous compatibility surface non-blocking. The async-disposable path is
        // awaited by the host service provider; this wrapper records and observes its task.
        TrackLifecycleTask(DisposeAsync().AsTask(), "dispose");
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            if (_disposeTask is null)
            {
                Interlocked.Exchange(ref _disposeRequested, 1);
                _disposeTask = DisposeCoreAsync();
            }

            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _lifecycleSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            _mode = FileDragWakeMode.Disabled;
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _disposed, true);
            _criticalSignals.Complete();
            _moveSignals.Complete();
            if (_hookThread is not { IsAlive: true })
            {
                _hookMessageQueueReady.Dispose();
                _observerRegistrationReady.Dispose();
            }
            else
            {
                _logger.LogError(
                    "The smart drag observer remained alive during asynchronous disposal; native resources were left reachable for its eventual thread exit.");
            }

            _lifecycleSemaphore.Release();
        }
    }

    private void TrackLifecycleTask(Task task, string operation)
    {
        // The detector owns the returned task through the lifecycle semaphore. This continuation
        // observes failures for legacy void callers without blocking their UI thread.
        task.ContinueWith(
            completed =>
            {
                if (completed.IsFaulted)
                {
                    _logger.LogError(
                        completed.Exception?.GetBaseException(),
                        "Asynchronous smart-drag {Operation} failed.",
                        operation);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }

    private void StartCore()
    {
        if (_hookThread is { IsAlive: true } ||
            _processor is { IsCompleted: false })
        {
            return;
        }

        _hookThread = null;
        _hookThreadId = 0;
        _hookMessageQueueReady.Reset();
        _observerRegistrationReady.Reset();
        Volatile.Write(ref _mouseHookRegistrationError, 0);
        Volatile.Write(ref _keyboardHookRegistrationError, 0);
        Volatile.Write(ref _objectDragHookRegistrationError, 0);
        Volatile.Write(ref _systemDragHookRegistrationError, 0);
        _runCancellation = new CancellationTokenSource();
        _processor = Task.Run(() => ProcessSignalsAsync(_runCancellation.Token));
        _hookThread = new Thread(HookThreadMain)
        {
            IsBackground = true,
            Name = "DropSpace file-drag observer",
        };
        _hookThread.Start();
        _logger.LogInformation(
            "Smart file-drag detection enabled: idle top-edge activation HWNDs are not required; mouse hooks observe only and never suppress input.");
    }

    private async Task StopCoreAsync()
    {
        foreach (var cancellation in _scheduledTasks.Keys)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                _scheduledTasks.TryRemove(cancellation, out _);
            }
        }

        var scheduledTasks = _scheduledTasks.Values.ToArray();
        _completionGrace?.Cancel();
        _sessionTimeout?.Cancel();
        _runCancellation?.Cancel();

        if (scheduledTasks.Length > 0)
        {
            try
            {
                await Task.WhenAll(scheduledTasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is the expected shutdown path for owned grace and timeout tasks.
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "A smart-drag lifecycle task failed during shutdown.");
            }
        }

        _completionGrace = null;
        _sessionTimeout = null;

        var hookThread = _hookThread;
        var processor = _processor;
        var hookExited = hookThread is not { IsAlive: true };
        if (!hookExited)
        {
            if (!await WaitForEventAsync(_hookMessageQueueReady, SmartDragRuntimePolicy.ObserverShutdownTimeout).ConfigureAwait(false))
            {
                _logger.LogError("The smart drag observer message queue did not become ready during asynchronous shutdown.");
            }

            if (_hookThreadId != 0 && !PostThreadMessage(_hookThreadId, WindowMessageQuit, nint.Zero, nint.Zero))
            {
                _logger.LogError(
                    "Posting WM_QUIT to the smart drag observer failed with Win32 error {Error}.",
                    Marshal.GetLastWin32Error());
            }

            hookExited = await WaitForThreadExitAsync(hookThread!, SmartDragRuntimePolicy.ObserverShutdownTimeout).ConfigureAwait(false);
        }

        var processorExited = processor is null;
        if (processor is not null)
        {
            try
            {
                await processor.WaitAsync(SmartDragRuntimePolicy.ObserverShutdownTimeout).ConfigureAwait(false);
                processorExited = true;
            }
            catch (OperationCanceledException)
            {
                processorExited = true;
            }
            catch (TimeoutException)
            {
                processorExited = processor.IsCompleted;
            }
            catch (Exception exception)
            {
                processorExited = true;
                _logger.LogWarning(exception, "Smart drag signal processing ended with an asynchronous shutdown exception.");
            }
        }

        if (hookExited)
        {
            _hookThread = null;
            _hookThreadId = 0;
        }
        else
        {
            _logger.LogError(
                "The smart drag observer did not exit within the bounded asynchronous shutdown interval; its queued WM_QUIT remains pending and no replacement observer will be started concurrently.");
        }

        if (processorExited)
        {
            _processor = null;
        }
        else
        {
            _logger.LogError(
                "The smart drag signal processor did not exit within the bounded asynchronous shutdown interval; no replacement processor will be started concurrently.");
        }

        if (hookExited && processorExited)
        {
            _runCancellation?.Dispose();
            _runCancellation = null;
        }

        while (_criticalSignals.TryRead(out _))
        {
        }

        while (_moveSignals.TryRead(out _))
        {
        }

        Interlocked.Exchange(ref _pointerObservationActive, 0);
        Volatile.Write(ref _candidateCreationSuppressed, 0);
        if (!_disposed)
        {
            _observerRegistrationReady.Reset();
        }

        _policy.Reset();
        _logger.LogInformation("Smart file-drag detection disabled and all observer hooks were removed.");
    }

    private static async Task<bool> WaitForEventAsync(
        ManualResetEventSlim signal,
        TimeSpan timeout)
    {
        if (signal.IsSet)
        {
            return true;
        }

        var deadline = Stopwatch.GetTimestamp() +
            checked((long)(timeout.TotalSeconds * Stopwatch.Frequency));
        while (!signal.IsSet)
        {
            var remainingTicks = deadline - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0)
            {
                return signal.IsSet;
            }

            var remaining = TimeSpan.FromSeconds(remainingTicks / (double)Stopwatch.Frequency);
            await Task.Delay(
                remaining < SmartDragRuntimePolicy.WaitPollSlice
                    ? remaining
                    : SmartDragRuntimePolicy.WaitPollSlice).ConfigureAwait(false);
        }

        return true;
    }

    private static async Task<bool> WaitForThreadExitAsync(Thread thread, TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() +
            checked((long)(timeout.TotalSeconds * Stopwatch.Frequency));
        while (thread.IsAlive)
        {
            var remainingTicks = deadline - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0)
            {
                return !thread.IsAlive;
            }

            var remaining = TimeSpan.FromSeconds(remainingTicks / (double)Stopwatch.Frequency);
            await Task.Delay(
                remaining < SmartDragRuntimePolicy.WaitPollSlice
                    ? remaining
                    : SmartDragRuntimePolicy.WaitPollSlice).ConfigureAwait(false);
        }

        return true;
    }

    private void HookThreadMain()
    {
        _hookThreadId = GetCurrentThreadId();
        // Force the native message queue into existence before SetMode can request WM_QUIT. Without
        // this handshake a very fast Smart -> Classic switch can race PostThreadMessage and strand
        // an observer in its first GetMessage call.
        _ = PeekMessage(out _, nint.Zero, 0, 0, PeekMessageNoRemove);
        _hookMessageQueueReady.Set();
        try
        {
            _mouseProcedure = MouseHookCallback;
            _keyboardProcedure = KeyboardHookCallback;
            _winEventProcedure = WinEventCallback;
            _mouseHook = SetWindowsHookEx(HookMouseLowLevel, _mouseProcedure, GetModuleHandle(null), 0);
            if (_mouseHook == nint.Zero)
            {
                Volatile.Write(ref _mouseHookRegistrationError, Marshal.GetLastWin32Error());
            }

            _keyboardHook = SetWindowsHookEx(HookKeyboardLowLevel, _keyboardProcedure, GetModuleHandle(null), 0);
            if (_keyboardHook == nint.Zero)
            {
                Volatile.Write(ref _keyboardHookRegistrationError, Marshal.GetLastWin32Error());
            }

            if (_mouseHook == nint.Zero || _keyboardHook == nint.Zero)
            {
                _logger.LogError(
                    "Smart drag input observer registration was incomplete: mouse={MouseRegistered} error={MouseError}, keyboard={KeyboardRegistered} error={KeyboardError}; the classic compatibility mode remains available.",
                    _mouseHook != nint.Zero,
                    _mouseHookRegistrationError,
                    _keyboardHook != nint.Zero,
                    _keyboardHookRegistrationError);
            }

            _systemDragWinEventHook = SetWinEventHook(
                EventSystemDragDropStart,
                EventSystemDragDropEnd,
                nint.Zero,
                _winEventProcedure,
                0,
                0,
                WinEventOutOfContext | WinEventSkipOwnProcess);
            if (_systemDragWinEventHook == nint.Zero)
            {
                Volatile.Write(ref _systemDragHookRegistrationError, Marshal.GetLastWin32Error());
                _logger.LogWarning(
                    "EVENT_SYSTEM_DRAGDROPSTART/END registration failed with Win32 error {Error}; other smart-drag signals remain active.",
                    _systemDragHookRegistrationError);
            }

            _dragWinEventHook = SetWinEventHook(
                EventObjectDragStart,
                EventObjectDragComplete,
                nint.Zero,
                _winEventProcedure,
                0,
                0,
                WinEventOutOfContext | WinEventSkipOwnProcess);
            if (_dragWinEventHook == nint.Zero)
            {
                Volatile.Write(ref _objectDragHookRegistrationError, Marshal.GetLastWin32Error());
                _logger.LogWarning(
                    "EVENT_OBJECT_DRAGSTART/CANCEL/COMPLETE registration failed with Win32 error {Error}; system drag events and the Explorer threshold fallback remain active.",
                    _objectDragHookRegistrationError);
            }
            else
            {
                _logger.LogInformation(
                    "Documented EVENT_OBJECT_DRAGSTART/CANCEL/COMPLETE signals registered on the observer message thread.");
            }

            // Registration must complete before the message pump starts and must never be gated on
            // a root-wide UI Automation subscription. The former UiaAddEvent call was deprecated
            // and could block this thread before GetMessage, leaving Smart mode inert. UIA/MSAA
            // are now used only for bounded source hit-testing on the serialized worker.
            _observerRegistrationReady.Set();

            _logger.LogInformation(
                "Smart drag native observers ready: {Diagnostics}.",
                ObserverRegistrationDiagnostics);

            while (GetMessage(out var message, nint.Zero, 0, 0) > 0)
            {
                _ = TranslateMessage(ref message);
                _ = DispatchMessage(ref message);
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "The smart drag observer thread stopped unexpectedly; the classic compatibility mode remains available.");
        }
        finally
        {
            _observerRegistrationReady.Reset();
            if (_dragWinEventHook != nint.Zero)
            {
                _ = UnhookWinEvent(_dragWinEventHook);
                _dragWinEventHook = nint.Zero;
            }

            if (_systemDragWinEventHook != nint.Zero)
            {
                _ = UnhookWinEvent(_systemDragWinEventHook);
                _systemDragWinEventHook = nint.Zero;
            }

            _winEventProcedure = null;
            if (_mouseHook != nint.Zero)
            {
                _ = UnhookWindowsHookEx(_mouseHook);
                _mouseHook = nint.Zero;
            }

            if (_keyboardHook != nint.Zero)
            {
                _ = UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = nint.Zero;
            }
        }
    }

    private nint MouseHookCallback(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var data = Marshal.PtrToStructure<LowLevelMouseHookData>(lParam);
            var kind = wParam.ToInt32() switch
            {
                WindowMessageMouseMove => DetectorSignalKind.PointerMoved,
                WindowMessageLeftButtonDown => DetectorSignalKind.LeftPressed,
                WindowMessageLeftButtonUp => DetectorSignalKind.LeftReleased,
                WindowMessageRightButtonDown => DetectorSignalKind.RightPressed,
                WindowMessageRightButtonUp => DetectorSignalKind.RightReleased,
                _ => DetectorSignalKind.None,
            };
            if (kind is DetectorSignalKind.LeftPressed or DetectorSignalKind.RightPressed)
            {
                Interlocked.Exchange(ref _pointerObservationActive, 1);
                TryWrite(new DetectorSignal(kind, new DragScreenPoint(data.Point.X, data.Point.Y)));
            }
            else if (kind == DetectorSignalKind.PointerMoved &&
                     Volatile.Read(ref _pointerObservationActive) != 0)
            {
                TryWrite(new DetectorSignal(kind, new DragScreenPoint(data.Point.X, data.Point.Y)));
            }
            else if ((kind is DetectorSignalKind.LeftReleased or DetectorSignalKind.RightReleased) &&
                     Interlocked.Exchange(ref _pointerObservationActive, 0) != 0)
            {
                TryWrite(new DetectorSignal(kind, new DragScreenPoint(data.Point.X, data.Point.Y)));
            }
        }

        return CallNextHookEx(nint.Zero, code, wParam, lParam);
    }

    private nint KeyboardHookCallback(int code, nint wParam, nint lParam)
    {
        if (code >= 0 &&
            wParam.ToInt32() is WindowMessageKeyDown or WindowMessageSystemKeyDown &&
            Marshal.ReadInt32(lParam) == VirtualKeyEscape)
        {
            TryWrite(new DetectorSignal(DetectorSignalKind.Cancelled, GetCursorPoint()));
        }

        return CallNextHookEx(nint.Zero, code, wParam, lParam);
    }

    private void WinEventCallback(
        nint hook,
        uint eventType,
        nint window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        var kind = eventType switch
        {
            EventSystemDragDropStart => DetectorSignalKind.AccessibleObjectStarted,
            EventSystemDragDropEnd => DetectorSignalKind.AccessibleObjectCompleted,
            EventObjectDragStart => DetectorSignalKind.AccessibleObjectStarted,
            EventObjectDragCancel => DetectorSignalKind.AccessibleObjectCancelled,
            EventObjectDragComplete => DetectorSignalKind.AccessibleObjectCompleted,
            _ => DetectorSignalKind.None,
        };
        if (kind == DetectorSignalKind.None)
        {
            return;
        }

        if (kind == DetectorSignalKind.AccessibleObjectStarted)
        {
            if (eventType == EventSystemDragDropStart)
            {
                Interlocked.Increment(ref _systemDragStartSignalCount);
            }
            else
            {
                Interlocked.Increment(ref _objectDragStartSignalCount);
            }
        }

        // The out-of-context callback only queues metadata. Process and UI Automation inspection
        // run on the serialized worker, so this callback never delays or alters source input.
        TryWrite(new DetectorSignal(kind, GetCursorPoint(), window));
    }

    private async Task ProcessSignalsAsync(CancellationToken cancellationToken)
    {
        var pressedShellSurface = DragSourceKind.Unknown;
        var pressedExcluded = false;
        DragScreenPoint? previousMove = null;
        long previousMoveTimestamp = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var signal = await ReadNextSignalAsync(cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _observedSignals);
                if (signal.CandidateCreationSuppressed || CandidateCreationSuppressed)
                {
                    if (signal.Kind == DetectorSignalKind.Cancelled && CandidateCreationSuppressed)
                    {
                        PlacementEditEscapeRequested?.Invoke(this, EventArgs.Empty);
                    }

                    // Suppression forbids creation/promotion only. Terminal events still
                    // converge the active session, including signals queued before editing.
                    if (signal.Kind is DetectorSignalKind.LeftPressed or DetectorSignalKind.RightPressed or
                        DetectorSignalKind.PointerMoved or DetectorSignalKind.AccessibleObjectStarted or
                        DetectorSignalKind.ProbeVerified)
                    {
                        PublishTransition(_policy.DragCancelled(signal.Point));
                        pressedShellSurface = DragSourceKind.Unknown;
                        pressedExcluded = false;
                        continue;
                    }
                }

                DragSessionTransition transition;
                switch (signal.Kind)
                {
                    case DetectorSignalKind.LeftPressed:
                    case DetectorSignalKind.RightPressed:
                        pressedExcluded = IsForegroundProcessExcluded();
                        if (pressedExcluded)
                        {
                            pressedShellSurface = DragSourceKind.Unknown;
                            continue;
                        }
                        var inspection = InspectSource(signal.Point);
                        pressedShellSurface = inspection.SurfaceSource;
                        transition = _policy.PointerPressed(
                            signal.Point,
                            signal.Kind == DetectorSignalKind.LeftPressed
                                ? DragPointerButton.Left
                                : DragPointerButton.Right,
                            inspection.SurfaceSource,
                            exactFileItem: inspection.ItemSource != DragSourceKind.Unknown);
                        PublishTransition(transition);
                        continue;
                    case DetectorSignalKind.PointerMoved:
                        RecordVelocity(signal, ref previousMove, ref previousMoveTimestamp);
                        transition = _policy.PointerMoved(signal.Point);
                        break;
                    case DetectorSignalKind.AccessibleObjectStarted:
                        if (pressedExcluded)
                        {
                            continue;
                        }
                        var eventSource = ShellDragSourceInspector.ClassifyDragEvent(
                            signal.SourceWindow,
                            signal.Point);
                        transition = _policy.AccessibilityDragStarted(
                            signal.Point,
                            eventSource != DragSourceKind.Unknown ? eventSource : pressedShellSurface);
                        break;
                    case DetectorSignalKind.LeftReleased:
                    case DetectorSignalKind.RightReleased:
                    case DetectorSignalKind.AccessibleObjectCompleted:
                        transition = _policy.PointerReleased(signal.Point);
                        PublishTransition(transition);
                        ScheduleCompletion(signal.Point, cancellationToken);
                        if (!_policy.IsActive)
                        {
                            pressedShellSurface = DragSourceKind.Unknown;
                            pressedExcluded = false;
                        }
                        continue;
                    case DetectorSignalKind.AccessibleObjectCancelled:
                    case DetectorSignalKind.Cancelled:
                        transition = _policy.DragCancelled(signal.Point);
                        pressedShellSurface = DragSourceKind.Unknown;
                        pressedExcluded = false;
                        break;
                    case DetectorSignalKind.CompletionGraceElapsed:
                        transition = _policy.CompletionGraceExpired(signal.SessionId, signal.Point);
                        break;
                    case DetectorSignalKind.OleCompleted:
                        transition = _policy.IsActive && _policy.ActiveSessionId == signal.SessionId
                            ? _policy.DragCompleted(signal.Point)
                            : DragSessionTransition.None;
                        pressedShellSurface = DragSourceKind.Unknown;
                        break;
                    case DetectorSignalKind.ProbeVerified:
                        transition = _policy.ProbeVerified(signal.SessionId, signal.Point);
                        break;
                    case DetectorSignalKind.ProbeRejected:
                        transition = _policy.ProbeRejected(signal.SessionId, signal.Point);
                        break;
                    case DetectorSignalKind.ProbeTimedOut:
                        transition = _policy.ProbeTimedOut(signal.SessionId, signal.Point);
                        break;
                    case DetectorSignalKind.Timeout:
                        transition = _policy.Timeout(signal.SessionId, signal.Point);
                        break;
                    default:
                        continue;
                }

                PublishTransition(transition);
                if (transition.Kind is DragSessionTransitionKind.Completed or
                    DragSessionTransitionKind.Superseded or
                    DragSessionTransitionKind.Cancelled or
                    DragSessionTransitionKind.Rejected or
                    DragSessionTransitionKind.TimedOut)
                {
                    pressedShellSurface = DragSourceKind.Unknown;
                    pressedExcluded = false;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Smart drag signal processing stopped unexpectedly.");
        }
    }

    private void PublishTransition(DragSessionTransition transition)
    {
        if (transition.Kind == DragSessionTransitionKind.Started)
        {
            if (transition.RequiresOleVerification)
            {
                Interlocked.Increment(ref _genericCandidateCount);
            }

            _completionGrace?.Cancel();
            _sessionTimeout?.Cancel();
            var timeoutCancellation = new CancellationTokenSource();
            _sessionTimeout = timeoutCancellation;
            ScheduleTimeout(transition.SessionId, transition.Point, timeoutCancellation);
            var monitor = _monitorLayout.GetMonitorAtPoint(transition.Point.X, transition.Point.Y);
            _logger.LogInformation(
                "Smart file-drag candidate session {SessionId} started on monitor {MonitorId}: source={Source}, evidenceLevel={EvidenceLevel}, evidence={Evidence}, requiresOleVerification={RequiresOleVerification}; no user path was inspected.",
                transition.SessionId,
                monitor.Id,
                transition.Source,
                transition.EvidenceLevel,
                transition.Evidence,
                transition.RequiresOleVerification);
            CandidateStarted?.Invoke(this, new DragSessionCandidate(
                transition.SessionId,
                monitor.Id,
                transition.Point,
                transition.Source,
                transition.EvidenceLevel,
                transition.Evidence,
                transition.RequiresOleVerification,
                transition.DragIntentConfidence,
                transition.PayloadConfidence));
            return;
        }

        if (transition.Kind == DragSessionTransitionKind.Verified)
        {
            Interlocked.Increment(ref _verifiedCandidateCount);
            var monitor = _monitorLayout.GetMonitorAtPoint(transition.Point.X, transition.Point.Y);
            _logger.LogInformation(
                "Smart drag candidate session {SessionId} was verified for visual ownership on monitor {MonitorId}: evidenceLevel={EvidenceLevel}, evidence={Evidence}.",
                transition.SessionId,
                monitor.Id,
                transition.EvidenceLevel,
                transition.Evidence);
            VerifiedFileDragStarted?.Invoke(this, new DragSessionCandidate(
                transition.SessionId,
                monitor.Id,
                transition.Point,
                transition.Source,
                transition.EvidenceLevel,
                transition.Evidence,
                false,
                transition.DragIntentConfidence,
                transition.PayloadConfidence));
            return;
        }

        if (transition.Kind is DragSessionTransitionKind.Completed or
            DragSessionTransitionKind.Superseded or
            DragSessionTransitionKind.Cancelled or
            DragSessionTransitionKind.Rejected or
            DragSessionTransitionKind.TimedOut)
        {
            if (transition.Kind == DragSessionTransitionKind.Rejected)
            {
                Interlocked.Increment(ref _rejectedCandidateCount);
            }
            else if (transition.Kind == DragSessionTransitionKind.TimedOut)
            {
                Interlocked.Increment(ref _probeTimeoutCount);
            }

            _completionGrace?.Cancel();
            _sessionTimeout?.Cancel();
            _logger.LogInformation(
                "Smart file-drag candidate session {SessionId} ended with {Result}.",
                transition.SessionId,
                transition.Kind);
            CandidateEnded?.Invoke(this, transition.SessionId);
        }
    }

    private void ScheduleCompletion(DragScreenPoint point, CancellationToken runCancellation)
    {
        if (!_policy.IsActive)
        {
            return;
        }

        _completionGrace?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(runCancellation);
        _completionGrace = cancellation;
        var sessionId = _policy.ActiveSessionId;
        var task = CompleteAfterGraceAsync(sessionId, point, cancellation);
        _scheduledTasks[cancellation] = task;
        if (task.IsCompleted)
        {
            _scheduledTasks.TryRemove(cancellation, out _);
        }

        TrackLifecycleTask(task, "completion grace");
    }

    private async Task CompleteAfterGraceAsync(
        long sessionId,
        DragScreenPoint point,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(SmartDragRuntimePolicy.PointerReleaseGrace, cancellation.Token);
            TryWrite(new DetectorSignal(
                DetectorSignalKind.CompletionGraceElapsed,
                point,
                SessionId: sessionId));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            _scheduledTasks.TryRemove(cancellation, out _);
            if (ReferenceEquals(_completionGrace, cancellation))
            {
                _completionGrace = null;
            }

            cancellation.Dispose();
        }
    }

    private void ScheduleTimeout(
        long sessionId,
        DragScreenPoint point,
        CancellationTokenSource cancellation)
    {
        var task = TimeoutAsync(sessionId, point, cancellation);
        _scheduledTasks[cancellation] = task;
        if (task.IsCompleted)
        {
            _scheduledTasks.TryRemove(cancellation, out _);
        }

        TrackLifecycleTask(task, "session timeout");
    }

    private async Task TimeoutAsync(
        long sessionId,
        DragScreenPoint point,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(SmartDragRuntimePolicy.SessionTimeout, cancellation.Token);
            TryWrite(new DetectorSignal(DetectorSignalKind.Timeout, point, SessionId: sessionId));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            _scheduledTasks.TryRemove(cancellation, out _);
            if (ReferenceEquals(_sessionTimeout, cancellation))
            {
                _sessionTimeout = null;
            }

            cancellation.Dispose();
        }
    }

    private void TryWrite(DetectorSignal signal)
    {
        if (signal.Timestamp == 0)
        {
            signal = signal with { Timestamp = Stopwatch.GetTimestamp() };
        }
        if (signal.Kind != DetectorSignalKind.None && CandidateCreationSuppressed)
        {
            signal = signal with { CandidateCreationSuppressed = true };
        }

        if (signal.Kind == DetectorSignalKind.PointerMoved)
        {
            _ = _moveSignals.TryWrite(signal);
        }
        else if (!_criticalSignals.TryWrite(signal))
        {
            _logger.LogDebug(
                "Smart drag critical signal could not be queued because the detector is shutting down: {SignalKind}.",
                signal.Kind);
        }
    }

    private void RecordVelocity(
        DetectorSignal signal,
        ref DragScreenPoint? previousPoint,
        ref long previousTimestamp)
    {
        if (previousPoint is { } previous && previousTimestamp > 0 && signal.Timestamp > previousTimestamp)
        {
            var seconds = (signal.Timestamp - previousTimestamp) / (double)Stopwatch.Frequency;
            var distance = Math.Sqrt(
                Math.Pow(signal.Point.X - previous.X, 2) +
                Math.Pow(signal.Point.Y - previous.Y, 2));
            var velocity = distance / seconds;
            if (velocity < SmartDragRuntimePolicy.SlowVelocityPixelsPerSecond) Interlocked.Increment(ref _velocitySlowCount);
            else if (velocity < SmartDragRuntimePolicy.MediumVelocityPixelsPerSecond) Interlocked.Increment(ref _velocityMediumCount);
            else if (velocity < SmartDragRuntimePolicy.FastVelocityPixelsPerSecond) Interlocked.Increment(ref _velocityFastCount);
            else Interlocked.Increment(ref _velocityExtremeCount);
        }
        previousPoint = signal.Point;
        previousTimestamp = signal.Timestamp;
    }

    private async ValueTask<DetectorSignal> ReadNextSignalAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var hasCritical = _criticalSignals.TryPeek(out var critical);
            var hasMove = _moveSignals.TryPeek(out var move);
            if (hasCritical && hasMove)
            {
                // The lanes have different pressure policies, not different chronology. An older
                // threshold-crossing move must be applied before a later release/cancel signal.
                if (move.Timestamp < critical.Timestamp && _moveSignals.TryRead(out move))
                {
                    return move;
                }
                if (_criticalSignals.TryRead(out critical))
                {
                    return critical;
                }
            }
            else if (hasCritical && _criticalSignals.TryRead(out critical))
            {
                return critical;
            }
            else if (hasMove && _moveSignals.TryRead(out move))
            {
                return move;
            }

            // Cancel the losing wait after either channel becomes readable. Leaving one pending
            // WaitToReadAsync per pointer move would retain registrations until the next critical
            // signal and turn a long drag into avoidable pressure of its own.
            using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var criticalReady = _criticalSignals.WaitToReadAsync(waitCancellation.Token).AsTask();
            var moveReady = _moveSignals.WaitToReadAsync(waitCancellation.Token).AsTask();
            _ = await Task.WhenAny(criticalReady, moveReady).ConfigureAwait(false);
            await waitCancellation.CancelAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private SourceInspection InspectSource(DragScreenPoint point)
    {
        // ProcessSignalsAsync resumes on arbitrary thread-pool threads. UI Automation is COM and
        // therefore must be initialized on the exact thread that performs ElementFromPoint. The
        // Preview.1 implementation initialized only the hook/message thread, so the asynchronous
        // classifier commonly returned Unknown before the drag threshold could reveal the Island.
        var result = CoInitializeEx(nint.Zero, CoInitializeMultithreaded);
        var mustUninitialize = result >= 0;
        if (result < 0 && result != RpcEChangedMode)
        {
            Interlocked.Increment(ref _comInitializationFailureCount);
            return SourceInspection.Unknown;
        }

        try
        {
            var inspection = ShellDragSourceInspector.Inspect(point);
            if (inspection.SurfaceSource == DragSourceKind.Unknown)
            {
                Interlocked.Increment(ref _rejectedSourceCount);
            }
            else
            {
                Interlocked.Increment(ref _recognizedSourceCount);
            }
            return inspection;
        }
        finally
        {
            if (mustUninitialize)
            {
                CoUninitialize();
            }
        }
    }

    private bool IsForegroundProcessExcluded()
    {
        var exclusions = Volatile.Read(ref _excludedProcessNames);
        if (exclusions.Length == 0)
        {
            return false;
        }
        try
        {
            var window = GetForegroundWindow();
            _ = GetWindowThreadProcessId(window, out var processId);
            if (processId == 0)
            {
                return false;
            }
            using var process = Process.GetProcessById(checked((int)processId));
            return exclusions.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private static DragScreenPoint GetCursorPoint() => GetCursorPos(out var point)
        ? new DragScreenPoint(point.X, point.Y)
        : default;

    private readonly record struct SourceInspection(
        DragSourceKind SurfaceSource,
        DragSourceKind ItemSource)
    {
        public static SourceInspection Unknown { get; } = new(
            DragSourceKind.Unknown,
            DragSourceKind.Unknown);
    }

    private enum DetectorSignalKind
    {
        None,
        PointerMoved,
        LeftPressed,
        LeftReleased,
        RightPressed,
        RightReleased,
        AccessibleObjectStarted,
        AccessibleObjectCompleted,
        AccessibleObjectCancelled,
        CompletionGraceElapsed,
        Cancelled,
        OleCompleted,
        ProbeVerified,
        ProbeRejected,
        ProbeTimedOut,
        Timeout,
    }

    private readonly record struct DetectorSignal(
        DetectorSignalKind Kind,
        DragScreenPoint Point,
        nint SourceWindow = default,
        long SessionId = 0,
        long Timestamp = 0,
        bool CandidateCreationSuppressed = false);

    private delegate nint HookProcedure(int code, nint wParam, nint lParam);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void WinEventProcedure(
        nint hook,
        uint eventType,
        nint window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelMouseHookData
    {
        public NativePoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public nint Window;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public NativePoint Point;
        public uint Private;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(
        int hookId,
        HookProcedure procedure,
        nint module,
        uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWinEventHook(
        uint eventMinimum,
        uint eventMaximum,
        nint module,
        WinEventProcedure callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out NativeMessage message, nint window, uint minimum, uint maximum);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(
        out NativeMessage message,
        nint window,
        uint minimum,
        uint maximum,
        uint removeMessage);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, int message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint apartmentType);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    private static class ShellDragSourceInspector
    {
        private const uint GetAncestorParent = 1;
        private const uint GetAncestorRoot = 2;
        private const int UiAutomationControlTypePropertyId = 30003;
        private const int UiAutomationControlTypeListItem = 50007;
        private const int UiAutomationControlTypeTreeItem = 50024;
        private const int UiAutomationControlTypeDataItem = 50029;
        private static readonly Guid CUiAutomation8ClassId = new("E22AD333-B25F-460C-83D0-0581107395C9");
        private static readonly HashSet<string> FileViewClasses = new(StringComparer.OrdinalIgnoreCase)
        {
            "SysListView32",
            "SHELLDLL_DefView",
            "DirectUIHWND",
            "DUIViewWndClassName",
            "CtrlNotifySink",
            "Microsoft.UI.Content.DesktopChildSiteBridge",
            "Windows.UI.Composition.DesktopWindowContentBridge",
        };

        public static SourceInspection Inspect(DragScreenPoint point)
        {
            var window = WindowFromPoint(new NativePoint { X = point.X, Y = point.Y });
            var rootSource = ClassifyExplorerRoot(window);
            if (rootSource == DragSourceKind.Unknown)
            {
                return SourceInspection.Unknown;
            }

            var surfaceSource = ClassifyShellSurface(window);
            var itemAtPoint = IsFileItemAtPoint(point) || IsAccessibleFileItemAtPoint(point);
            if (surfaceSource == DragSourceKind.Unknown && itemAtPoint)
            {
                // Modern Explorer hosts parts of its file view in XAML bridge windows whose class
                // names change between Windows builds. A confirmed UIA/MSAA file item plus an
                // explorer.exe root is stronger evidence than a mutable implementation class.
                surfaceSource = rootSource;
            }

            return new SourceInspection(
                surfaceSource,
                itemAtPoint ? surfaceSource : DragSourceKind.Unknown);
        }

        public static DragSourceKind ClassifyDragEvent(nint sourceWindow, DragScreenPoint point)
        {
            var source = ClassifyShellSurface(sourceWindow);
            if (source != DragSourceKind.Unknown)
            {
                return source;
            }

            // An accessibility drag-start event is itself strong drag evidence. Providers often
            // report their top-level Explorer HWND rather than the implementation-detail file-view
            // child, so accept a verified explorer.exe root without requiring a class allow-list.
            source = ClassifyExplorerRoot(sourceWindow);
            if (source != DragSourceKind.Unknown)
            {
                return source;
            }

            // Some providers report their top-level accessibility window rather than the file-view
            // child. A point fallback is accepted only when both windows belong to the same Shell
            // root. The EVENT_OBJECT_DRAGSTART signal still proves a drag; OLE CF_HDROP remains the
            // final file-data authority.
            var pointWindow = WindowFromPoint(new NativePoint { X = point.X, Y = point.Y });
            if (sourceWindow == nint.Zero)
            {
                var pointSource = ClassifyShellSurface(pointWindow);
                return pointSource != DragSourceKind.Unknown
                    ? pointSource
                    : ClassifyExplorerRoot(pointWindow);
            }

            var sourceRoot = GetAncestor(sourceWindow, GetAncestorRoot);
            var pointRoot = GetAncestor(pointWindow, GetAncestorRoot);
            return sourceRoot != nint.Zero && sourceRoot == pointRoot
                ? ClassifyExplorerRoot(pointWindow)
                : DragSourceKind.Unknown;
        }

        private static DragSourceKind ClassifyShellSurface(nint window)
        {
            if (window == nint.Zero)
            {
                return DragSourceKind.Unknown;
            }

            var rootSource = ClassifyExplorerRoot(window);
            if (rootSource == DragSourceKind.Unknown)
            {
                return DragSourceKind.Unknown;
            }

            var root = GetAncestor(window, GetAncestorRoot);
            var rootClass = GetClass(root);
            var hasFileViewSurface = false;
            var current = window;
            for (var depth = 0; depth < 16 && current != nint.Zero; depth++)
            {
                var className = GetClass(current);
                if (FileViewClasses.Contains(className))
                {
                    hasFileViewSurface = true;
                }

                if (current == root)
                {
                    break;
                }

                current = GetAncestor(current, GetAncestorParent);
            }

            if (!hasFileViewSurface)
            {
                return DragSourceKind.Unknown;
            }

            return rootClass is "Progman" or "WorkerW"
                ? DragSourceKind.DesktopFileView
                : DragSourceKind.ExplorerFileView;
        }

        private static DragSourceKind ClassifyExplorerRoot(nint window)
        {
            if (window == nint.Zero)
            {
                return DragSourceKind.Unknown;
            }

            var root = GetAncestor(window, GetAncestorRoot);
            _ = GetWindowThreadProcessId(root, out var processId);
            try
            {
                using var process = Process.GetProcessById(unchecked((int)processId));
                if (!string.Equals(process.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase))
                {
                    return DragSourceKind.Unknown;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
            {
                return DragSourceKind.Unknown;
            }

            var rootClass = GetClass(root);
            if (rootClass is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or
                "NotifyIconOverflowWindow")
            {
                return DragSourceKind.Unknown;
            }

            return rootClass is "Progman" or "WorkerW"
                ? DragSourceKind.DesktopFileView
                : DragSourceKind.ExplorerFileView;
        }

        private static bool IsFileItemAtPoint(DragScreenPoint point)
        {
            IUiAutomation? automation = null;
            IUiAutomationTreeWalker? walker = null;
            IUiAutomationElement? element = null;
            try
            {
                automation = CreateAutomation();
                if (automation is null || automation.ElementFromPoint(
                        new NativePoint { X = point.X, Y = point.Y },
                        out element) < 0 || element is null)
                {
                    return false;
                }

                if (automation.GetRawViewWalker(out walker) < 0 || walker is null)
                {
                    return false;
                }

                // ElementFromPoint may return the image or text child inside an Explorer item.
                // Walk a bounded raw-view ancestor chain instead of requiring the deepest element
                // itself to be ListItem/DataItem. This still rejects blank file-view space, text
                // selection, window moves and arbitrary controls; OLE CF_HDROP remains authoritative.
                for (var depth = 0; depth < 16 && element is not null; depth++)
                {
                    if (element.GetCurrentPropertyValue(
                            UiAutomationControlTypePropertyId,
                            out var value) >= 0 &&
                        value is int controlType &&
                        controlType is UiAutomationControlTypeListItem or
                            UiAutomationControlTypeTreeItem or
                            UiAutomationControlTypeDataItem)
                    {
                        return true;
                    }

                    if (walker.GetParentElement(element, out var parent) < 0 || parent is null)
                    {
                        break;
                    }

                    ReleaseComObject(element);
                    element = parent;
                }

                return false;
            }
            catch (Exception exception) when (exception is COMException or InvalidCastException)
            {
                return false;
            }
            finally
            {
                ReleaseComObject(element);
                ReleaseComObject(walker);
                ReleaseComObject(automation);
            }
        }

        private static bool IsAccessibleFileItemAtPoint(DragScreenPoint point)
        {
            IAccessible? accessible = null;
            try
            {
                var result = AccessibleObjectFromPoint(
                    new NativePoint { X = point.X, Y = point.Y },
                    out accessible,
                    out var childId);
                if (result < 0 || accessible is null)
                {
                    return false;
                }

                var roleValue = accessible.GetRole(childId);
                if (roleValue is null)
                {
                    return false;
                }

                var role = Convert.ToInt32(roleValue, System.Globalization.CultureInfo.InvariantCulture);
                // Explorer/Desktop file items are exposed as list items; navigation-pane folders
                // can be outline items. Rows/data cells cover Details layouts on older providers.
                return role is 0x22 or 0x24 or 0x1C or 0x1D;
            }
            catch (Exception exception) when (exception is COMException or InvalidCastException or FormatException or OverflowException)
            {
                return false;
            }
            finally
            {
                ReleaseComObject(accessible);
            }
        }

        private static void ReleaseComObject(object? value)
        {
            if (value is not null && Marshal.IsComObject(value))
            {
                _ = Marshal.ReleaseComObject(value);
            }
        }

        private static IUiAutomation? CreateAutomation()
        {
            try
            {
                var type = Type.GetTypeFromCLSID(CUiAutomation8ClassId, throwOnError: true);
                return (IUiAutomation?)Activator.CreateInstance(type!);
            }
            catch (Exception exception) when (exception is COMException or InvalidCastException or TypeLoadException)
            {
                return null;
            }
        }

        private static string GetClass(nint window)
        {
            var buffer = new StringBuilder(256);
            return GetClassName(window, buffer, buffer.Capacity) > 0
                ? buffer.ToString()
                : string.Empty;
        }

        [DllImport("user32.dll")]
        private static extern nint WindowFromPoint(NativePoint point);

        [DllImport("user32.dll")]
        private static extern nint GetAncestor(nint window, uint flags);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(nint window, StringBuilder className, int maximumCount);

        [DllImport("oleacc.dll")]
        private static extern int AccessibleObjectFromPoint(
            NativePoint point,
            [MarshalAs(UnmanagedType.Interface)] out IAccessible accessible,
            [MarshalAs(UnmanagedType.Struct)] out object childId);

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
        [Guid("618736E0-3C3D-11CF-810C-00AA00389B71")]
        private interface IAccessible
        {
            [DispId(-5006)]
            [return: MarshalAs(UnmanagedType.Struct)]
            object GetRole([In, Optional, MarshalAs(UnmanagedType.Struct)] object childId);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE")]
        private interface IUiAutomation
        {
            [PreserveSig]
            int CompareElements(IUiAutomationElement first, IUiAutomationElement second, out int areSame);

            [PreserveSig]
            int CompareRuntimeIds(nint first, nint second, out int areSame);

            [PreserveSig]
            int GetRootElement(out IUiAutomationElement element);

            [PreserveSig]
            int ElementFromHandle(nint window, out IUiAutomationElement element);

            [PreserveSig]
            int ElementFromPoint(NativePoint point, out IUiAutomationElement element);

            [PreserveSig]
            int GetFocusedElement(out IUiAutomationElement element);

            [PreserveSig]
            int GetRootElementBuildCache(nint cacheRequest, out IUiAutomationElement element);

            [PreserveSig]
            int ElementFromHandleBuildCache(nint window, nint cacheRequest, out IUiAutomationElement element);

            [PreserveSig]
            int ElementFromPointBuildCache(NativePoint point, nint cacheRequest, out IUiAutomationElement element);

            [PreserveSig]
            int GetFocusedElementBuildCache(nint cacheRequest, out IUiAutomationElement element);

            [PreserveSig]
            int CreateTreeWalker(nint condition, out IUiAutomationTreeWalker walker);

            [PreserveSig]
            int GetControlViewWalker(out IUiAutomationTreeWalker walker);

            [PreserveSig]
            int GetContentViewWalker(out IUiAutomationTreeWalker walker);

            [PreserveSig]
            int GetRawViewWalker(out IUiAutomationTreeWalker walker);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("4042C624-389C-4AFC-A630-9DF854A541FC")]
        private interface IUiAutomationTreeWalker
        {
            [PreserveSig]
            int GetParentElement(IUiAutomationElement element, out IUiAutomationElement parent);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("D22108AA-8AC5-49A5-837B-37BBB3D7591E")]
        private interface IUiAutomationElement
        {
            [PreserveSig]
            int SetFocus();

            [PreserveSig]
            int GetRuntimeId(out nint runtimeId);

            [PreserveSig]
            int FindFirst(int scope, nint condition, out IUiAutomationElement element);

            [PreserveSig]
            int FindAll(int scope, nint condition, out nint elements);

            [PreserveSig]
            int FindFirstBuildCache(int scope, nint condition, nint cacheRequest, out IUiAutomationElement element);

            [PreserveSig]
            int FindAllBuildCache(int scope, nint condition, nint cacheRequest, out nint elements);

            [PreserveSig]
            int BuildUpdatedCache(nint cacheRequest, out IUiAutomationElement element);

            [PreserveSig]
            int GetCurrentPropertyValue(
                int propertyId,
                [MarshalAs(UnmanagedType.Struct)] out object value);
        }
    }
}
