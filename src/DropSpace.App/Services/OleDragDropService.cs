using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using DropSpace.Core.DragDrop;
using DropSpace.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services;

public sealed record DragActivationCallbacks(
    Action<string> DragApproaching,
    Action<string, bool> DragReadyChanged,
    Action<string> DragLeft,
    Func<string, IReadOnlyList<string>, Task> Dropped,
    Func<string, IReadOnlyList<string>, Task>? DroppedOwned = null);

/// <summary>
/// Owns OLE initialization and native drop-target registrations. Both the visually transparent reveal host
/// and the independently visible WinUI island feed the same callbacks and AddPathsAsync pipeline.
/// </summary>
public sealed class OleDragDropService : IDisposable
{
    private const int Success = 0;
    private const int SuccessAlreadyInitialized = 1;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<OleDragDropService> _logger;
    private readonly MonitorLayoutService _monitorLayout;
    private readonly OleFileDataClassifier _fileDataClassifier = new();
    private readonly VirtualFileMaterializer _virtualFileMaterializer;
    private readonly SmartDragProbeOptions _probeOptions = SmartDragProbeOptions.Default;
    private readonly List<IDisposable> _registrations = [];
    private EphemeralOleDragProbe? _activeProbe;
    private bool _oleInitialized;
    private bool _disposed;

    public OleDragDropService(
        ILoggerFactory loggerFactory,
        MonitorLayoutService monitorLayout,
        AppStoragePaths paths)
    {
        _loggerFactory = loggerFactory;
        _monitorLayout = monitorLayout;
        _logger = loggerFactory.CreateLogger<OleDragDropService>();
        _virtualFileMaterializer = new VirtualFileMaterializer(
            paths,
            _fileDataClassifier,
            loggerFactory.CreateLogger<VirtualFileMaterializer>());
    }

    public DragActivationHost CreateActivationHost(
        MonitorDescriptor monitor,
        DragActivationCallbacks callbacks)
    {
        EnsureOleInitialized();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var host = new DragActivationHost(
            monitor,
            callbacks,
            _fileDataClassifier,
            _virtualFileMaterializer,
            _loggerFactory.CreateLogger<DragActivationHost>());
        _registrations.Add(host);
        return host;
    }

    internal OleDropTargetRegistration RegisterVisualTarget(
        nint windowHandle,
        string monitorId,
        DragActivationCallbacks callbacks)
    {
        EnsureOleInitialized();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var registration = new OleDropTargetRegistration(
            windowHandle,
            monitorId,
            callbacks,
            _loggerFactory.CreateLogger<OleDropTargetRegistration>(),
            _fileDataClassifier,
            _virtualFileMaterializer,
            _ => true,
            "visual-overlay");
        return registration;
    }

    internal int ActiveVerificationProbeCount => _activeProbe is { IsDisposed: false } ? 1 : 0;

    internal EphemeralOleDragProbe StartVerificationProbe(
        long sessionId,
        DragScreenPoint candidatePoint,
        Action<OleDragProbeResult> completed,
        Func<bool>? isStillValid = null)
    {
        EnsureOleInitialized();
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(completed);

        if (isStillValid is not null && !isStillValid())
        {
            throw new OperationCanceledException("The Smart drag candidate is no longer valid.");
        }

        if (!GetCursorPos(out var nativePoint))
        {
            var error = Marshal.GetLastWin32Error();
            _logger.LogWarning(
                "Smart OLE verification probe could not read the current cursor position; candidate=({CandidateX},{CandidateY}), win32Error={Win32Error}.",
                candidatePoint.X,
                candidatePoint.Y,
                error);
            throw new Win32Exception(error, "The current cursor position could not be read.");
        }

        var currentPoint = new DragScreenPoint(nativePoint.X, nativePoint.Y);
        if (isStillValid is not null && !isStillValid())
        {
            throw new OperationCanceledException("The Smart drag candidate became stale before probe creation.");
        }

        _activeProbe?.Dispose();
        _activeProbe = null;
        EphemeralOleDragProbe? probe = null;
        var monitor = _monitorLayout.GetMonitorAtPoint(currentPoint.X, currentPoint.Y);
        if (isStillValid is not null && !isStillValid())
        {
            throw new OperationCanceledException("The Smart drag candidate became stale while resolving its monitor.");
        }

        probe = new EphemeralOleDragProbe(
            sessionId,
            currentPoint,
            monitor,
            _probeOptions,
            _fileDataClassifier,
            result =>
            {
                if (ReferenceEquals(_activeProbe, probe))
                {
                    _activeProbe = null;
                }

                completed(result);
            },
            _loggerFactory.CreateLogger<EphemeralOleDragProbe>());
        _activeProbe = probe;
        return probe;
    }

    internal void CancelVerificationProbe(long sessionId)
    {
        if (_activeProbe is not { } probe || (sessionId != 0 && probe.SessionId != sessionId))
        {
            return;
        }

        _activeProbe = null;
        probe.Dispose();
    }

    internal async Task RunVerificationProbeSmokeAsync(
        DragScreenPoint point,
        CancellationToken cancellationToken = default)
    {
        EnsureOleInitialized();
        using var dataObject = new CfHDropDataObject([@"C:\DropSpace-probe-smoke.txt"]);
        var classification = _fileDataClassifier.Classify(dataObject);
        if (classification.Kind != OleFileDataKind.FileSystemPaths || !classification.CanAccept)
        {
            throw new InvalidOperationException("The shared OLE classifier rejected synthetic CF_HDROP data.");
        }

        var shellItems = _fileDataClassifier.Classify(new QueryOnlyDataObject(
            new OleFormatAdvertisement(
                _fileDataClassifier.ShellIdListClipboardFormat,
                TYMED.TYMED_HGLOBAL)));
        var virtualFiles = _fileDataClassifier.Classify(new QueryOnlyDataObject(
            new OleFormatAdvertisement(
                _fileDataClassifier.FileGroupDescriptorWClipboardFormat,
                TYMED.TYMED_HGLOBAL),
            new OleFormatAdvertisement(
                _fileDataClassifier.FileContentsClipboardFormat,
                TYMED.TYMED_ISTREAM,
                0)));
        var unsupported = _fileDataClassifier.Classify(new QueryOnlyDataObject(
            new OleFormatAdvertisement(13, TYMED.TYMED_HGLOBAL)));
        if (shellItems.Kind != OleFileDataKind.ShellItems || !shellItems.IsFileLikeEvidence || shellItems.CanAcceptNow ||
            virtualFiles.Kind != OleFileDataKind.VirtualFiles || !virtualFiles.IsFileLikeEvidence ||
            virtualFiles.CanAcceptNow || !virtualFiles.CanMaterialize ||
            unsupported.Kind != OleFileDataKind.None || unsupported.CanAccept)
        {
            throw new InvalidOperationException(
                "The shared OLE classifier did not distinguish Shell items, virtual files, and unsupported text formats.");
        }

        if (!GetCursorPos(out var originalCursor))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "The Smart OLE cursor-refresh smoke could not read the original cursor position.");
        }

        var liveCursorMonitor = _monitorLayout.GetMonitorAtPoint(point.X, point.Y);
        var liveCursorPoint = new DragScreenPoint(
            liveCursorMonitor.Left + liveCursorMonitor.Width / 2,
            liveCursorMonitor.Top + liveCursorMonitor.Height / 2);
        var staleCandidatePoint = new DragScreenPoint(
            liveCursorMonitor.Left + liveCursorMonitor.Width / 4,
            liveCursorPoint.Y);
        if (Math.Abs(staleCandidatePoint.X - liveCursorPoint.X) < _probeOptions.OuterSizePixels)
        {
            staleCandidatePoint = new DragScreenPoint(
                liveCursorMonitor.Left + Math.Min(liveCursorMonitor.Width - 1, liveCursorMonitor.Width * 3 / 4),
                liveCursorPoint.Y);
        }

        var cursorMoved = false;
        try
        {
            if (!SetCursorPos(liveCursorPoint.X, liveCursorPoint.Y))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "The Smart OLE cursor-refresh smoke could not move the test cursor.");
            }

            cursorMoved = true;
            var completion = new TaskCompletionSource<OleDragProbeResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var createdProbe = StartVerificationProbe(
                long.MaxValue,
                staleCandidatePoint,
                result => completion.TrySetResult(result));
            if (!createdProbe.VerifyNativeContract() || ActiveVerificationProbeCount != 1)
            {
                CancelVerificationProbe(createdProbe.SessionId);
                throw new InvalidOperationException(
                    "The Smart OLE probe did not satisfy its hollow Region, NOACTIVATE, TOOLWINDOW and TOPMOST contract.");
            }

            if (createdProbe.ProbeCenter.X != liveCursorPoint.X || createdProbe.ProbeCenter.Y != liveCursorPoint.Y)
            {
                CancelVerificationProbe(createdProbe.SessionId);
                throw new InvalidOperationException(
                    $"The Smart OLE probe used the stale candidate point ({staleCandidatePoint.X},{staleCandidatePoint.Y}) instead of the live cursor ({liveCursorPoint.X},{liveCursorPoint.Y}).");
            }

            SetCursorPos(originalCursor.X, originalCursor.Y);
            cursorMoved = false;

            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            await Task.Yield();
            createdProbe.Dispose();
            createdProbe.Dispose();
            if (result.Outcome != OleDragProbeOutcome.TimedOut ||
                result.SessionId != createdProbe.SessionId ||
                !createdProbe.IsDisposed ||
                ActiveVerificationProbeCount != 0)
            {
                throw new InvalidOperationException(
                    "The Smart OLE probe did not enforce timeout cleanup, single ownership and idempotent disposal.");
            }
        }
        finally
        {
            if (cursorMoved)
            {
                SetCursorPos(originalCursor.X, originalCursor.Y);
            }
        }

        var fallbackCompletion = new TaskCompletionSource<OleDragProbeResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var monitor = _monitorLayout.GetMonitorAtPoint(point.X, point.Y);
        using var fallbackProbe = new EphemeralOleDragProbe(
            long.MaxValue - 1,
            point,
            monitor,
            _probeOptions with { HardLifetimeMilliseconds = 10 },
            _fileDataClassifier,
            fallbackCompletion.SetResult,
            _loggerFactory.CreateLogger<EphemeralOleDragProbe>(),
            static (_, _, _, _) => false);
        var fallbackResult = await fallbackCompletion.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
        await Task.Delay(300, cancellationToken);
        if (fallbackResult.Outcome != OleDragProbeOutcome.TimedOut || !fallbackProbe.IsDisposed)
        {
            throw new InvalidOperationException(
                "The Smart OLE probe did not clean up after simulated PostMessage failure.");
        }

        _logger.LogInformation(
            "Smart OLE verification probe smoke passed: native contract, CF_HDROP/Shell/virtual/unsupported classification, timeout cleanup, PostMessage-failure fallback, and double-dispose were verified.");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        for (var index = _registrations.Count - 1; index >= 0; index--)
        {
            _registrations[index].Dispose();
        }

        _registrations.Clear();
        CancelVerificationProbe(0);
        if (_oleInitialized)
        {
            OleUninitialize();
            _oleInitialized = false;
        }

        _disposed = true;
    }

    private void EnsureOleInitialized()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_oleInitialized)
        {
            return;
        }

        var result = OleInitialize(nint.Zero);
        if (result is not (Success or SuccessAlreadyInitialized))
        {
            Marshal.ThrowExceptionForHR(result);
        }

        _oleInitialized = true;
        _logger.LogInformation("OLE drag/drop initialized on the WinUI message-pump thread.");
    }

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(nint reserved);

    [DllImport("ole32.dll")]
    private static extern void OleUninitialize();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);
}

public sealed class DragActivationHost : IDisposable
{
    public const double IdleWidthDips = 960;
    public const int IdleHeightPixels = 12;
    public const double ActiveWidthDips = 840;
    public const double ActiveHeightDips = 144;
    private const int HitTestClient = 1;
    private const int MouseActivateNoActivate = 3;
    private const uint WindowStylePopup = 0x80000000;
    private const uint ExtendedStyleTopmost = 0x00000008;
    private const uint ExtendedStyleToolWindow = 0x00000080;
    private const uint ExtendedStyleLayered = 0x00080000;
    private const uint ExtendedStyleNoActivate = 0x08000000;
    private const uint LayeredAlpha = 0x00000002;
    private const int ShowNoActivate = 4;
    private const int HideWindow = 0;
    private const uint SetWindowPositionNoActivate = 0x0010;
    private const uint SetWindowPositionShowWindow = 0x0040;
    private const uint WindowMessageNonClientHitTest = 0x0084;
    private const uint WindowMessageMouseActivate = 0x0021;
    private const uint WindowMessageEraseBackground = 0x0014;
    private const uint WindowMessageDisplayChange = 0x007E;
    private const string WindowClassName = "DropSpace.DragActivationHost.v4";
    private static readonly object ClassGate = new();
    private static readonly Dictionary<nint, DragActivationHost> Hosts = [];
    private static readonly WindowProcedureCallback SharedWindowProcedure = StaticWindowProcedure;
    private static ushort _windowClass;
    private readonly MonitorDescriptor _monitor;
    private readonly ILogger<DragActivationHost> _logger;
    private readonly OleDropTargetRegistration _dropTarget;
    private readonly NativeRectangle _idleBounds;
    private readonly NativeRectangle _activeBounds;
    private NativeRectangle _bounds;
    private bool _enabled = true;
    private bool _dragActive;
    private bool _disposed;

    internal DragActivationHost(
        MonitorDescriptor monitor,
        DragActivationCallbacks callbacks,
        OleFileDataClassifier fileDataClassifier,
        VirtualFileMaterializer virtualFileMaterializer,
        ILogger<DragActivationHost> logger)
    {
        _monitor = monitor;
        _logger = logger;
        EnsureWindowClass();

        _idleBounds = CreateCenteredBounds(ToPixels(IdleWidthDips), IdleHeightPixels);
        _activeBounds = CreateCenteredBounds(ToPixels(ActiveWidthDips), ToPixels(ActiveHeightDips));
        _bounds = _idleBounds;
        WindowHandle = CreateWindowEx(
            ExtendedStyleTopmost | ExtendedStyleToolWindow | ExtendedStyleLayered | ExtendedStyleNoActivate,
            WindowClassName,
            string.Empty,
            WindowStylePopup,
            _bounds.Left,
            _bounds.Top,
            _bounds.Width,
            _bounds.Height,
            nint.Zero,
            nint.Zero,
            GetModuleHandle(null),
            nint.Zero);
        if (WindowHandle == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The drag activation HWND could not be created.");
        }

        lock (ClassGate)
        {
            Hosts[WindowHandle] = this;
        }

        // A uniform alpha of zero is omitted by WindowFromPoint/OLE target discovery on supported
        // Windows builds even when WM_NCHITTEST returns HTCLIENT. One out of 255 keeps the unpainted
        // host visually imperceptible while leaving it discoverable. Preview.3 used one physical
        // pixel here, which required the OLE cursor hotspot to land on the exact topmost scan line.
        // A bounded 12-pixel screen-edge safety band remains within the non-client resize edge on
        // common maximized windows while being large enough for real Explorer/Desktop drags.
        if (!SetLayeredWindowAttributes(WindowHandle, 0, 1, LayeredAlpha))
        {
            var exception = new Win32Exception(Marshal.GetLastWin32Error(), "The activation HWND could not be made visually transparent.");
            DestroyHostWindow();
            throw exception;
        }

        ShowWindow(WindowHandle, ShowNoActivate);
        var ownedCallbacks = new DragActivationCallbacks(
            monitorId =>
            {
                ExpandForDrag();
                callbacks.DragApproaching(monitorId);
                BringToTop();
            },
            (monitorId, ready) =>
            {
                callbacks.DragReadyChanged(monitorId, ready);
                BringToTop();
            },
            monitorId =>
            {
                try
                {
                    callbacks.DragLeft(monitorId);
                }
                finally
                {
                    CollapseAfterDrag();
                }
            },
            async (monitorId, paths) =>
            {
                try
                {
                    await callbacks.Dropped(monitorId, paths);
                }
                finally
                {
                    CollapseAfterDrag();
                }
            },
            callbacks.DroppedOwned is null
                ? null
                : async (monitorId, paths) =>
                {
                    try
                    {
                        await callbacks.DroppedOwned(monitorId, paths);
                    }
                    finally
                    {
                        CollapseAfterDrag();
                    }
                });
        _dropTarget = new OleDropTargetRegistration(
            WindowHandle,
            monitor.Id,
            ownedCallbacks,
            logger,
            fileDataClassifier,
            virtualFileMaterializer,
            IsDropReady,
            "activation-host");
        _logger.LogInformation(
            "Drag activation host created on monitor {MonitorId}: HWND {WindowHandle}, DPI {Dpi}, idle bounds {Left},{Top},{Width},{Height}, active bounds {ActiveLeft},{ActiveTop},{ActiveWidth},{ActiveHeight}; uniform-alpha=1/255, mouse-hit-test=client, ownership=activation-through-drop, activation-band=12-physical-pixels.",
            monitor.Id,
            WindowHandle,
            monitor.Dpi,
            _bounds.Left,
            _bounds.Top,
            _idleBounds.Width,
            _idleBounds.Height,
            _activeBounds.Left,
            _activeBounds.Top,
            _activeBounds.Width,
            _activeBounds.Height);
    }

    public event EventHandler? DisplayTopologyChanged;

    public nint WindowHandle { get; }

    public string MonitorId => _monitor.Id;

    internal bool IsIdleTargetDiscoverable()
    {
        var point = new NativePoint(
            _idleBounds.Left + _idleBounds.Width / 2,
            _idleBounds.Top + _idleBounds.Height / 2);
        return WindowFromPoint(point) == WindowHandle;
    }

    public void SetEnabled(bool enabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_enabled == enabled)
        {
            return;
        }

        _enabled = enabled;
        if (_dragActive)
        {
            // OLE selected this HWND before the visual state changed. Keep the owner alive through
            // Drop/Leave; the requested passive state is applied when that operation finishes.
            return;
        }

        if (!enabled)
        {
            ShowWindow(WindowHandle, HideWindow);
            return;
        }

        PositionWindow(_dragActive ? _activeBounds : _idleBounds);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _dropTarget.Dispose();
        DestroyHostWindow();
        _disposed = true;
    }

    private bool IsDropReady(NativePoint point)
    {
        var horizontalInset = ToPixels(72);
        var topInset = IdleHeightPixels;
        return point.X >= _bounds.Left + horizontalInset &&
               point.X < _bounds.Right - horizontalInset &&
               point.Y >= _bounds.Top + topInset &&
               point.Y < _bounds.Bottom;
    }

    private int ToPixels(double dips) => Math.Max(1, (int)Math.Round(dips * _monitor.Scale));

    private NativeRectangle CreateCenteredBounds(int width, int height)
    {
        width = Math.Min(width, _monitor.Width);
        var left = _monitor.Left + (_monitor.Width - width) / 2;
        return new NativeRectangle(left, _monitor.Top, left + width, _monitor.Top + height);
    }

    private void ExpandForDrag()
    {
        _dragActive = true;
        PositionWindow(_activeBounds);
        _logger.LogInformation(
            "Drag activation HWND {WindowHandle} expanded for OLE ownership on monitor {MonitorId}: bounds {Left},{Top},{Width},{Height}.",
            WindowHandle,
            _monitor.Id,
            _activeBounds.Left,
            _activeBounds.Top,
            _activeBounds.Width,
            _activeBounds.Height);
    }

    private void CollapseAfterDrag()
    {
        _dragActive = false;
        if (_enabled)
        {
            PositionWindow(_idleBounds);
        }

        _logger.LogInformation(
            "Drag activation HWND {WindowHandle} returned to its bounded top-edge activation band on monitor {MonitorId}.",
            WindowHandle,
            _monitor.Id);
    }

    private void BringToTop()
    {
        if (_dragActive)
        {
            PositionWindow(_activeBounds);
        }
    }

    private void PositionWindow(NativeRectangle bounds)
    {
        _bounds = bounds;
        if (!SetWindowPos(
                WindowHandle,
                new nint(-1),
                bounds.Left,
                bounds.Top,
                bounds.Width,
                bounds.Height,
                SetWindowPositionNoActivate | SetWindowPositionShowWindow))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The drag activation HWND could not be positioned.");
        }
    }

    private void DestroyHostWindow()
    {
        if (WindowHandle == nint.Zero)
        {
            return;
        }

        lock (ClassGate)
        {
            Hosts.Remove(WindowHandle);
        }

        DestroyWindow(WindowHandle);
    }

    private static void EnsureWindowClass()
    {
        lock (ClassGate)
        {
            if (_windowClass != 0)
            {
                return;
            }

            var windowClass = new WindowClassExtended
            {
                Size = (uint)Marshal.SizeOf<WindowClassExtended>(),
                WindowProcedure = Marshal.GetFunctionPointerForDelegate(SharedWindowProcedure),
                Instance = GetModuleHandle(null),
                ClassName = WindowClassName,
            };
            _windowClass = RegisterClassEx(ref windowClass);
            if (_windowClass == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The drag activation window class could not be registered.");
            }
        }
    }

    private static nint StaticWindowProcedure(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == WindowMessageNonClientHitTest)
        {
            // OLE target discovery uses the window under the pointer. HTTRANSPARENT forwards only
            // within the creating thread and made Explorer skip this registered target entirely.
            // The idle HWND is therefore an intentional bounded HTCLIENT screen-edge band.
            return new nint(HitTestClient);
        }

        if (message == WindowMessageMouseActivate)
        {
            return new nint(MouseActivateNoActivate);
        }

        if (message == WindowMessageEraseBackground)
        {
            return new nint(1);
        }

        if (message == WindowMessageDisplayChange)
        {
            DragActivationHost? host;
            lock (ClassGate)
            {
                Hosts.TryGetValue(window, out host);
            }

            host?.DisplayTopologyChanged?.Invoke(host, EventArgs.Empty);
        }

        return DefWindowProc(window, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClassExtended
    {
        public uint Size;
        public uint Style;
        public nint WindowProcedure;
        public int ClassExtraBytes;
        public int WindowExtraBytes;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint BackgroundBrush;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? MenuName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string ClassName;
        public nint SmallIcon;
    }

    private readonly record struct NativeRectangle(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;

        public int Height => Bottom - Top;
    }

    private delegate nint WindowProcedureCallback(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WindowClassExtended windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(nint window, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(NativePoint point);
}

[ComVisible(true)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("00000122-0000-0000-C000-000000000046")]
internal interface IOleDropTarget
{
    [PreserveSig]
    int DragEnter(
        [MarshalAs(UnmanagedType.Interface)] IDataObject dataObject,
        uint keyState,
        NativePoint point,
        ref uint effect);

    [PreserveSig]
    int DragOver(uint keyState, NativePoint point, ref uint effect);

    [PreserveSig]
    int DragLeave();

    [PreserveSig]
    int Drop(
        [MarshalAs(UnmanagedType.Interface)] IDataObject dataObject,
        uint keyState,
        NativePoint point,
        ref uint effect);
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct NativePoint
{
    public NativePoint(int x, int y)
    {
        X = x;
        Y = y;
    }

    public readonly int X;
    public readonly int Y;
}

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class OleDropTargetRegistration : IOleDropTarget, IDisposable
{
    private const int Success = 0;
    private const uint DropEffectNone = 0;
    private const uint DropEffectCopy = 1;
    private readonly nint _windowHandle;
    private readonly string _monitorId;
    private readonly DragActivationCallbacks _callbacks;
    private readonly ILogger _logger;
    private readonly OleFileDataClassifier _fileDataClassifier;
    private readonly VirtualFileMaterializer _virtualFileMaterializer;
    private readonly Func<NativePoint, bool> _isReady;
    private readonly string _surfaceKind;
    private IDataObject? _currentDataObject;
    private bool _canAccept;
    private OleFileDataClassification _classification;
    private bool _lastReady;
    private long _dragOverCount;
    private Task? _lastDropCompletion;
    private CancellationTokenSource? _dropCancellation;
    private bool _disposed;

    public OleDropTargetRegistration(
        nint windowHandle,
        string monitorId,
        DragActivationCallbacks callbacks,
        ILogger logger,
        OleFileDataClassifier fileDataClassifier,
        VirtualFileMaterializer virtualFileMaterializer,
        Func<NativePoint, bool> isReady,
        string surfaceKind)
    {
        _windowHandle = windowHandle;
        _monitorId = monitorId;
        _callbacks = callbacks;
        _logger = logger;
        _fileDataClassifier = fileDataClassifier;
        _virtualFileMaterializer = virtualFileMaterializer;
        _isReady = isReady;
        _surfaceKind = surfaceKind;
        OleDropTargetNative.Register(windowHandle, this);

        _logger.LogInformation(
            "RegisterDragDrop succeeded for {SurfaceKind} on monitor {MonitorId}, HWND {WindowHandle}.",
            _surfaceKind,
            _monitorId,
            _windowHandle);
    }

    public int DragEnter(IDataObject dataObject, uint keyState, NativePoint point, ref uint effect)
    {
        _currentDataObject = dataObject;
        var discoveredWindow = WindowFromPoint(point);
        try
        {
            _classification = _fileDataClassifier.Classify(dataObject);
            _classification = _fileDataClassifier.ResolveAcceptance(dataObject, _classification);
        }
        catch (Exception exception)
        {
            _classification = OleFileDataClassification.None;
            _logger.LogWarning(
                exception,
                "OLE DragEnter classification failed closed for {SurfaceKind} on monitor {MonitorId}.",
                _surfaceKind,
                _monitorId);
        }

        _canAccept = _classification.CanAcceptNow || _classification.CanMaterialize;
        effect = _canAccept ? DropEffectCopy : DropEffectNone;
        _logger.LogInformation(
            "OLE DragEnter received by {SurfaceKind} on monitor {MonitorId}: classification={Classification}, fileLike={FileLike}, accepted={Accepted}, WindowFromPoint={DiscoveredWindow}, targetMatches={TargetMatches}.",
            _surfaceKind,
            _monitorId,
            _classification.Kind,
            _classification.IsFileLike,
            _canAccept,
            discoveredWindow,
            discoveredWindow == _windowHandle);
        if (_canAccept)
        {
            _callbacks.DragApproaching(_monitorId);
            _lastReady = _isReady(point);
            _dragOverCount = 0;
            _callbacks.DragReadyChanged(_monitorId, _lastReady);
        }

        return Success;
    }

    public int DragOver(uint keyState, NativePoint point, ref uint effect)
    {
        effect = _canAccept ? DropEffectCopy : DropEffectNone;
        if (_canAccept)
        {
            var ready = _isReady(point);
            var count = Interlocked.Increment(ref _dragOverCount);
            _callbacks.DragReadyChanged(_monitorId, ready);
            if (count == 1 || ready != _lastReady)
            {
                _logger.LogInformation(
                    "OLE DragOver received by {SurfaceKind} on monitor {MonitorId}: ready={Ready}, event count {DragOverCount}.",
                    _surfaceKind,
                    _monitorId,
                    ready,
                    count);
                _lastReady = ready;
            }
        }

        return Success;
    }

    public int DragLeave()
    {
        _logger.LogInformation(
            "OLE DragLeave received by {SurfaceKind} on monitor {MonitorId} after {DragOverCount} DragOver events.",
            _surfaceKind,
            _monitorId,
            Interlocked.Read(ref _dragOverCount));
        _currentDataObject = null;
        _canAccept = false;
        _classification = OleFileDataClassification.None;
        _callbacks.DragLeft(_monitorId);
        return Success;
    }

    public int Drop(IDataObject dataObject, uint keyState, NativePoint point, ref uint effect)
    {
        try
        {
            if (_classification.Kind == OleFileDataKind.VirtualFiles)
            {
                _dropCancellation?.Cancel();
                _dropCancellation?.Dispose();
                _dropCancellation = new CancellationTokenSource();
                effect = _canAccept ? DropEffectCopy : DropEffectNone;
                _lastDropCompletion = effect == DropEffectCopy
                    ? CompleteVirtualDropAsync(dataObject, _dropCancellation.Token)
                    : null;
                if (effect != DropEffectCopy)
                {
                    _callbacks.DragLeft(_monitorId);
                }
                return Success;
            }

            var paths = _fileDataClassifier.ReadFileSystemPaths(dataObject, _classification);
            // Once OLE selected this HWND and CF_HDROP was accepted, keep target ownership through
            // Drop. Re-evaluating a smaller visual-ready rectangle here made a valid Explorer drop
            // fail with DROPEFFECT_NONE when the final cursor sample landed on an animated edge.
            var ready = _canAccept;
            effect = ready && paths.Count > 0 ? DropEffectCopy : DropEffectNone;
            _logger.LogInformation(
                "OLE Drop received by {SurfaceKind} on monitor {MonitorId}: item count {ItemCount}, accepted={Accepted}, DragOver count {DragOverCount}.",
                _surfaceKind,
                _monitorId,
                paths.Count,
                effect == DropEffectCopy,
                Interlocked.Read(ref _dragOverCount));
            if (effect == DropEffectCopy)
            {
                _lastDropCompletion = CompleteDropAsync(paths);
                _ = _lastDropCompletion;
            }
            else
            {
                _callbacks.DragLeft(_monitorId);
            }
        }
        catch (Exception exception)
        {
            effect = DropEffectNone;
            _logger.LogWarning(exception, "OLE drop data could not be accepted.");
            _callbacks.DragLeft(_monitorId);
        }
        finally
        {
            _currentDataObject = null;
            _canAccept = false;
            _classification = OleFileDataClassification.None;
        }

        return Success;
    }

    internal async Task RunSyntheticCfHDropAsync(
        IReadOnlyList<string> paths,
        NativePoint point,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        using var dataObject = new CfHDropDataObject(paths);
        uint effect = DropEffectCopy;
        DragEnter(dataObject, 0, point, ref effect);
        if (effect != DropEffectCopy)
        {
            throw new InvalidOperationException("Synthetic CF_HDROP DragEnter was rejected by the visible target.");
        }

        Drop(dataObject, 0, point, ref effect);
        if (effect != DropEffectCopy || _lastDropCompletion is null)
        {
            throw new InvalidOperationException("Synthetic CF_HDROP Drop was rejected by the visible target.");
        }

        await _lastDropCompletion.WaitAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        var result = OleDropTargetNative.Revoke(_windowHandle);
        if (result < 0)
        {
            _logger.LogWarning(
                "RevokeDragDrop failed for {SurfaceKind} HWND {WindowHandle} with HRESULT 0x{HResult:X8}.",
                _surfaceKind,
                _windowHandle,
                result);
        }

        _dropCancellation?.Cancel();
        _dropCancellation?.Dispose();
        _dropCancellation = null;
        _disposed = true;
    }

    private async Task CompleteVirtualDropAsync(IDataObject dataObject, CancellationToken cancellationToken)
    {
        try
        {
            var paths = await _virtualFileMaterializer.MaterializeAsync(dataObject, cancellationToken);
            if (_callbacks.DroppedOwned is { } droppedOwned)
            {
                await droppedOwned(_monitorId, paths);
            }
            else
            {
                await _callbacks.Dropped(_monitorId, paths);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _callbacks.DragLeft(_monitorId);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Virtual-file materialization failed after the OLE callback returned.");
            _callbacks.DragLeft(_monitorId);
        }
    }

    private async Task CompleteDropAsync(IReadOnlyList<string> paths)
    {
        try
        {
            await _callbacks.Dropped(_monitorId, paths);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The shared Temporary Space drop pipeline failed.");
            _callbacks.DragLeft(_monitorId);
        }
    }

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(NativePoint point);
}

internal sealed class CfHDropDataObject : IDataObject, IDisposable
{
    private const short ClipboardFormatHDrop = 15;
    private const int FormatNotSupported = unchecked((int)0x80040064);
    private const uint GlobalMemoryMoveable = 0x0002;
    private const uint GlobalMemoryZeroInitialize = 0x0040;
    private readonly string[] _paths;

    public CfHDropDataObject(IEnumerable<string> paths)
    {
        _paths = paths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (_paths.Length == 0)
        {
            throw new ArgumentException("At least one path is required.", nameof(paths));
        }
    }

    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        if (QueryGetData(ref format) != 0)
        {
            Marshal.ThrowExceptionForHR(FormatNotSupported);
        }

        var payload = string.Join('\0', _paths) + "\0\0";
        var payloadBytes = System.Text.Encoding.Unicode.GetBytes(payload);
        const int dropFilesHeaderBytes = 20;
        var memory = GlobalAlloc(
            GlobalMemoryMoveable | GlobalMemoryZeroInitialize,
            (nuint)(dropFilesHeaderBytes + payloadBytes.Length));
        if (memory == nint.Zero)
        {
            throw new OutOfMemoryException("CF_HDROP HGLOBAL allocation failed.");
        }

        var pointer = GlobalLock(memory);
        if (pointer == nint.Zero)
        {
            GlobalFree(memory);
            throw new OutOfMemoryException("CF_HDROP HGLOBAL lock failed.");
        }

        try
        {
            Marshal.WriteInt32(pointer, 0, dropFilesHeaderBytes);
            Marshal.WriteInt32(pointer, 16, 1); // DROPFILES.fWide
            Marshal.Copy(payloadBytes, 0, pointer + dropFilesHeaderBytes, payloadBytes.Length);
        }
        finally
        {
            GlobalUnlock(memory);
        }

        medium = new STGMEDIUM
        {
            tymed = TYMED.TYMED_HGLOBAL,
            unionmember = memory,
            pUnkForRelease = null!,
        };
    }

    public int QueryGetData(ref FORMATETC format) =>
        format.cfFormat == ClipboardFormatHDrop &&
        format.dwAspect == DVASPECT.DVASPECT_CONTENT &&
        (format.tymed & TYMED.TYMED_HGLOBAL) != 0
            ? 0
            : FormatNotSupported;

    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) =>
        throw new NotSupportedException();

    public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
    {
        formatOut = formatIn;
        formatOut.ptd = nint.Zero;
        return unchecked((int)0x80004001);
    }

    public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release) =>
        throw new NotSupportedException();

    public IEnumFORMATETC EnumFormatEtc(DATADIR direction) =>
        throw new NotSupportedException();

    public int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink adviseSink, out int connection)
    {
        connection = 0;
        return unchecked((int)0x80004001);
    }

    public void DUnadvise(int connection) => throw new NotSupportedException();

    public int EnumDAdvise(out IEnumSTATDATA enumAdvise)
    {
        enumAdvise = null!;
        return unchecked((int)0x80004001);
    }

    public void Dispose()
    {
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(nint memory);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalFree(nint memory);
}
