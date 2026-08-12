using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using DropSpace.Core.DragDrop;
using DropSpace.Core.Models;
using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services;

public sealed record DragSessionCandidate(
    long SessionId,
    string MonitorId,
    DragScreenPoint Point,
    DragSourceKind Source);

/// <summary>
/// Event-driven, non-injecting observer for candidate Explorer/Desktop file drags. It never blocks
/// input and never reads a dragged path. A visible, temporary OLE target performs final CF_HDROP
/// validation after a candidate has revealed the Overlay.
/// </summary>
public sealed class DragSessionDetector : IDisposable
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
    private static readonly TimeSpan PointerReleaseGrace = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(30);
    private readonly MonitorLayoutService _monitorLayout;
    private readonly ILogger<DragSessionDetector> _logger;
    private readonly Channel<DetectorSignal> _signals = Channel.CreateBounded<DetectorSignal>(
        new BoundedChannelOptions(256)
        {
            // Hooks must never wait. Wait mode makes TryWrite return false when the bounded queue
            // is full, so overload is both non-blocking and visible in diagnostics.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly DragSessionPolicy _policy;
    private readonly object _lifecycleGate = new();
    private readonly ManualResetEventSlim _hookMessageQueueReady = new(false);
    private CancellationTokenSource? _runCancellation;
    private Task? _processor;
    private Thread? _hookThread;
    private uint _hookThreadId;
    private HookProcedure? _mouseProcedure;
    private HookProcedure? _keyboardProcedure;
    private nint _mouseHook;
    private nint _keyboardHook;
    private UiAutomationDragEventSource? _uiAutomation;
    private CancellationTokenSource? _completionGrace;
    private CancellationTokenSource? _sessionTimeout;
    private FileDragWakeMode _mode = FileDragWakeMode.Disabled;
    private bool _disposed;
    private long _observedSignals;
    private long _droppedSignals;
    private int _pointerObservationActive;

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

    public event EventHandler<long>? CandidateEnded;

    public bool UiAutomationEventsRegistered => _uiAutomation?.IsRegistered == true;

    public long ObservedSignalCount => Interlocked.Read(ref _observedSignals);

    public long DroppedSignalCount => Interlocked.Read(ref _droppedSignals);

    public void SetMode(FileDragWakeMode mode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lifecycleGate)
        {
            if (_mode == mode && (mode != FileDragWakeMode.SmartExperimental || _hookThread is not null))
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
                StopCore();
            }
        }
    }

    public void NotifyOleSessionCompleted()
    {
        TryWrite(new DetectorSignal(DetectorSignalKind.OleCompleted, GetCursorPoint()));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_lifecycleGate)
        {
            StopCore();
            _disposed = true;
            _hookMessageQueueReady.Dispose();
        }
    }

    private void StartCore()
    {
        if (_hookThread is { IsAlive: true })
        {
            return;
        }

        _hookThread = null;
        _hookThreadId = 0;
        _hookMessageQueueReady.Reset();
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

    private void StopCore()
    {
        _completionGrace?.Cancel();
        _completionGrace?.Dispose();
        _completionGrace = null;
        _sessionTimeout?.Cancel();
        _sessionTimeout?.Dispose();
        _sessionTimeout = null;
        _runCancellation?.Cancel();
        if (_hookThread is { IsAlive: true })
        {
            _ = _hookMessageQueueReady.Wait(TimeSpan.FromSeconds(2));
            if (_hookThreadId != 0)
            {
                _ = PostThreadMessage(_hookThreadId, WindowMessageQuit, nint.Zero, nint.Zero);
            }
        }

        if (_hookThread is { IsAlive: true })
        {
            _hookThread.Join(TimeSpan.FromSeconds(2));
        }

        if (_processor is not null)
        {
            try
            {
                _processor.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException exception) when (exception.InnerExceptions.All(
                       static inner => inner is OperationCanceledException))
            {
            }
        }

        if (_hookThread is not { IsAlive: true })
        {
            _hookThread = null;
            _hookThreadId = 0;
        }
        else
        {
            _logger.LogError(
                "The smart drag observer did not exit within the bounded shutdown interval; its queued WM_QUIT remains pending and no replacement observer will be started concurrently.");
        }
        _runCancellation?.Dispose();
        _runCancellation = null;
        _processor = null;
        while (_signals.Reader.TryRead(out _))
        {
        }
        Interlocked.Exchange(ref _pointerObservationActive, 0);
        _policy.Reset();
        _logger.LogInformation("Smart file-drag detection disabled and all observer hooks were removed.");
    }

    private void HookThreadMain()
    {
        _hookThreadId = GetCurrentThreadId();
        // Force the native message queue into existence before SetMode can request WM_QUIT. Without
        // this handshake a very fast Smart -> Classic switch can race PostThreadMessage and strand
        // an observer in its first GetMessage call.
        _ = PeekMessage(out _, nint.Zero, 0, 0, PeekMessageNoRemove);
        _hookMessageQueueReady.Set();
        var comResult = CoInitializeEx(nint.Zero, CoInitializeMultithreaded);
        var comInitialized = comResult >= 0;
        try
        {
            _mouseProcedure = MouseHookCallback;
            _keyboardProcedure = KeyboardHookCallback;
            _mouseHook = SetWindowsHookEx(HookMouseLowLevel, _mouseProcedure, GetModuleHandle(null), 0);
            _keyboardHook = SetWindowsHookEx(HookKeyboardLowLevel, _keyboardProcedure, GetModuleHandle(null), 0);
            if (_mouseHook == nint.Zero || _keyboardHook == nint.Zero)
            {
                _logger.LogError(
                    "Smart drag observer hook registration failed with Win32 error {Error}; the classic compatibility mode remains available.",
                    Marshal.GetLastWin32Error());
            }

            _uiAutomation = new UiAutomationDragEventSource(
                (kind, point) => TryWrite(new DetectorSignal(kind, point)),
                _logger);
            _uiAutomation.Start();

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
            _uiAutomation?.Dispose();
            _uiAutomation = null;
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

            if (comInitialized)
            {
                CoUninitialize();
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

    private async Task ProcessSignalsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var signal in _signals.Reader.ReadAllAsync(cancellationToken))
            {
                Interlocked.Increment(ref _observedSignals);
                DragSessionTransition transition;
                switch (signal.Kind)
                {
                    case DetectorSignalKind.LeftPressed:
                    case DetectorSignalKind.RightPressed:
                        var source = ShellDragSourceInspector.Classify(signal.Point);
                        _policy.PointerPressed(
                            signal.Point,
                            signal.Kind == DetectorSignalKind.LeftPressed
                                ? DragPointerButton.Left
                                : DragPointerButton.Right,
                            source);
                        if (source == DragSourceKind.Unknown)
                        {
                            // Stop forwarding global mouse moves as soon as a non-file origin is
                            // known. The low-level hook remains observation-only and near-zero work
                            // during ordinary idle pointer movement.
                            Interlocked.Exchange(ref _pointerObservationActive, 0);
                        }
                        continue;
                    case DetectorSignalKind.PointerMoved:
                        transition = _policy.PointerMoved(signal.Point);
                        break;
                    case DetectorSignalKind.UiAutomationStarted:
                        transition = _policy.UiAutomationDragStarted(
                            signal.Point,
                            ShellDragSourceInspector.Classify(signal.Point));
                        break;
                    case DetectorSignalKind.LeftReleased:
                    case DetectorSignalKind.RightReleased:
                    case DetectorSignalKind.UiAutomationCompleted:
                        ScheduleCompletion(signal.Point, cancellationToken);
                        continue;
                    case DetectorSignalKind.UiAutomationCancelled:
                    case DetectorSignalKind.Cancelled:
                        transition = _policy.DragCancelled(signal.Point);
                        break;
                    case DetectorSignalKind.OleCompleted:
                        transition = _policy.DragCompleted(signal.Point);
                        break;
                    case DetectorSignalKind.Timeout:
                        transition = _policy.Timeout(signal.Point);
                        break;
                    default:
                        continue;
                }

                PublishTransition(transition);
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
            _completionGrace?.Cancel();
            _sessionTimeout?.Cancel();
            _sessionTimeout?.Dispose();
            _sessionTimeout = new CancellationTokenSource();
            ScheduleTimeout(transition.SessionId, transition.Point, _sessionTimeout.Token);
            var monitor = _monitorLayout.GetMonitorAtPoint(transition.Point.X, transition.Point.Y);
            _logger.LogInformation(
                "Smart file-drag candidate session {SessionId} started on monitor {MonitorId}: source={Source}; no user path was inspected.",
                transition.SessionId,
                monitor.Id,
                transition.Source);
            CandidateStarted?.Invoke(this, new DragSessionCandidate(
                transition.SessionId,
                monitor.Id,
                transition.Point,
                transition.Source));
            return;
        }

        if (transition.Kind is DragSessionTransitionKind.Completed or DragSessionTransitionKind.Cancelled)
        {
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
        _completionGrace?.Dispose();
        _completionGrace = CancellationTokenSource.CreateLinkedTokenSource(runCancellation);
        var sessionId = _policy.ActiveSessionId;
        _ = CompleteAfterGraceAsync(sessionId, point, _completionGrace.Token);
    }

    private async Task CompleteAfterGraceAsync(
        long sessionId,
        DragScreenPoint point,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(PointerReleaseGrace, cancellationToken);
            if (_policy.IsActive && _policy.ActiveSessionId == sessionId)
            {
                PublishTransition(_policy.PointerReleased(point));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void ScheduleTimeout(long sessionId, DragScreenPoint point, CancellationToken cancellationToken)
    {
        _ = TimeoutAsync(sessionId, point, cancellationToken);
    }

    private async Task TimeoutAsync(long sessionId, DragScreenPoint point, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(SessionTimeout, cancellationToken);
            if (_policy.IsActive && _policy.ActiveSessionId == sessionId)
            {
                TryWrite(new DetectorSignal(DetectorSignalKind.Timeout, point));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void TryWrite(DetectorSignal signal)
    {
        if (!_signals.Writer.TryWrite(signal))
        {
            Interlocked.Increment(ref _droppedSignals);
        }
    }

    private static DragScreenPoint GetCursorPoint() => GetCursorPos(out var point)
        ? new DragScreenPoint(point.X, point.Y)
        : default;

    private enum DetectorSignalKind
    {
        None,
        PointerMoved,
        LeftPressed,
        LeftReleased,
        RightPressed,
        RightReleased,
        UiAutomationStarted,
        UiAutomationCompleted,
        UiAutomationCancelled,
        Cancelled,
        OleCompleted,
        Timeout,
    }

    private readonly record struct DetectorSignal(
        DetectorSignalKind Kind,
        DragScreenPoint Point);

    private delegate nint HookProcedure(int code, nint wParam, nint lParam);

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

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(nint reserved, uint apartmentType);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    private sealed class UiAutomationDragEventSource : IDisposable
    {
        private const int DragStartEventId = 20026;
        private const int DragCancelEventId = 20027;
        private const int DragCompleteEventId = 20028;
        private const int TreeScopeDescendants = 4;
        private readonly Action<DetectorSignalKind, DragScreenPoint> _publish;
        private readonly ILogger _logger;
        private readonly UiaEventCallback _callback;
        private readonly List<nint> _events = [];
        private nint _root;

        public UiAutomationDragEventSource(
            Action<DetectorSignalKind, DragScreenPoint> publish,
            ILogger logger)
        {
            _publish = publish;
            _logger = logger;
            _callback = OnEvent;
        }

        public bool IsRegistered => _events.Count == 3;

        public void Start()
        {
            try
            {
                var rootResult = UiaGetRootNode(out _root);
                if (rootResult < 0 || _root == nint.Zero)
                {
                    Marshal.ThrowExceptionForHR(rootResult);
                }

                Register(DragStartEventId);
                Register(DragCancelEventId);
                Register(DragCompleteEventId);
                _logger.LogInformation(
                    "UI Automation drag signal source registered for DragStart, DragCancel and DragComplete events.");
            }
            catch (Exception exception) when (exception is COMException or DllNotFoundException or EntryPointNotFoundException)
            {
                _logger.LogWarning(
                    exception,
                    "UI Automation drag events are unavailable; bounded Explorer/Desktop threshold detection remains active.");
                Dispose();
            }
        }

        public void Dispose()
        {
            foreach (var handle in _events)
            {
                _ = UiaRemoveEvent(handle);
            }

            _events.Clear();
            if (_root != nint.Zero)
            {
                _ = UiaNodeRelease(_root);
                _root = nint.Zero;
            }
        }

        private void Register(int eventId)
        {
            var result = UiaAddEvent(
                _root,
                eventId,
                _callback,
                TreeScopeDescendants,
                nint.Zero,
                0,
                nint.Zero,
                out var handle);
            if (result < 0 || handle == nint.Zero)
            {
                Marshal.ThrowExceptionForHR(result);
            }

            _events.Add(handle);
        }

        private void OnEvent(nint arguments, nint requestedData, nint treeStructure)
        {
            if (arguments == nint.Zero)
            {
                return;
            }

            var eventArguments = Marshal.PtrToStructure<UiaEventArguments>(arguments);
            var kind = eventArguments.EventId switch
            {
                DragStartEventId => DetectorSignalKind.UiAutomationStarted,
                DragCancelEventId => DetectorSignalKind.UiAutomationCancelled,
                DragCompleteEventId => DetectorSignalKind.UiAutomationCompleted,
                _ => DetectorSignalKind.None,
            };
            if (kind != DetectorSignalKind.None)
            {
                _publish(kind, GetCursorPoint());
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct UiaEventArguments
        {
            public readonly int Type;
            public readonly int EventId;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void UiaEventCallback(nint arguments, nint requestedData, nint treeStructure);

        [DllImport("UIAutomationCore.dll")]
        private static extern int UiaGetRootNode(out nint node);

        [DllImport("UIAutomationCore.dll")]
        private static extern int UiaAddEvent(
            nint node,
            int eventId,
            UiaEventCallback callback,
            int scope,
            nint properties,
            int propertyCount,
            nint cacheRequest,
            out nint eventHandle);

        [DllImport("UIAutomationCore.dll")]
        private static extern int UiaRemoveEvent(nint eventHandle);

        [DllImport("UIAutomationCore.dll")]
        private static extern int UiaNodeRelease(nint node);
    }

    private static class ShellDragSourceInspector
    {
        private const uint GetAncestorParent = 1;
        private const uint GetAncestorRoot = 2;
        private const int UiAutomationControlTypePropertyId = 30003;
        private const int UiAutomationControlTypeListItem = 50007;
        private const int UiAutomationControlTypeTreeItem = 50024;
        private const int UiAutomationControlTypeDataItem = 50029;
        private static readonly Guid CUiAutomation8ClassId = new("E22AD333-B25F-460C-83D0-0581107395C9");
        private static readonly ThreadLocal<IUiAutomation?> Automation = new(CreateAutomation);
        private static readonly HashSet<string> FileViewClasses = new(StringComparer.OrdinalIgnoreCase)
        {
            "SysListView32",
            "SHELLDLL_DefView",
            "DirectUIHWND",
            "CtrlNotifySink",
            "Microsoft.UI.Content.DesktopChildSiteBridge",
        };

        public static DragSourceKind Classify(DragScreenPoint point)
        {
            var window = WindowFromPoint(new NativePoint { X = point.X, Y = point.Y });
            if (window == nint.Zero)
            {
                return DragSourceKind.Unknown;
            }

            var root = GetAncestor(window, GetAncestorRoot);
            _ = GetWindowThreadProcessId(root, out var processId);
            string processName;
            try
            {
                using var process = Process.GetProcessById(unchecked((int)processId));
                processName = process.ProcessName;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
            {
                return DragSourceKind.Unknown;
            }

            if (!string.Equals(processName, "explorer", StringComparison.OrdinalIgnoreCase))
            {
                return DragSourceKind.Unknown;
            }

            var rootClass = GetClass(root);
            var desktop = rootClass is "Progman" or "WorkerW";
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

            if (!IsFileItemAtPoint(point))
            {
                return DragSourceKind.Unknown;
            }

            return desktop ? DragSourceKind.DesktopFileView : DragSourceKind.ExplorerFileView;
        }

        private static bool IsFileItemAtPoint(DragScreenPoint point)
        {
            IUiAutomationElement? element = null;
            try
            {
                var automation = Automation.Value;
                if (automation is null || automation.ElementFromPoint(
                        new NativePoint { X = point.X, Y = point.Y },
                        out element) < 0 || element is null)
                {
                    return false;
                }

                if (element.GetCurrentPropertyValue(
                        UiAutomationControlTypePropertyId,
                        out var value) < 0 || value is not int controlType)
                {
                    return false;
                }

                return controlType is UiAutomationControlTypeListItem or
                    UiAutomationControlTypeTreeItem or
                    UiAutomationControlTypeDataItem;
            }
            catch (Exception exception) when (exception is COMException or InvalidCastException)
            {
                return false;
            }
            finally
            {
                if (element is not null && Marshal.IsComObject(element))
                {
                    _ = Marshal.ReleaseComObject(element);
                }
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
