using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using DropSpace.Core.DragDrop;
using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services;

internal enum OleDragProbeOutcome
{
    VerifiedFile,
    Rejected,
    TimedOut,
}

internal sealed record OleDragProbeResult(
    long SessionId,
    OleDragProbeOutcome Outcome,
    OleFileDataClassification Classification,
    DragScreenPoint Point,
    TimeSpan Elapsed);

/// <summary>
/// A short-lived, hollow, non-activating OLE target centered on a generic drag candidate. The
/// cursor starts inside its real Region hole and can naturally cross the surrounding ring. The
/// target only queries IDataObject formats, always returns DROPEFFECT_NONE, and queues cleanup
/// outside the COM callback.
/// </summary>
internal sealed class EphemeralOleDragProbe : IDisposable
{
    private const int Success = 0;
    private const int HitTestClient = 1;
    private const int MouseActivateNoActivate = 3;
    private const uint WindowStylePopup = 0x80000000;
    private const long ExtendedStyleTopmost = 0x00000008;
    private const long ExtendedStyleToolWindow = 0x00000080;
    private const long ExtendedStyleLayered = 0x00080000;
    private const long ExtendedStyleNoActivate = 0x08000000;
    private const uint LayeredAlpha = 0x00000002;
    private const int ShowNoActivate = 4;
    private const int RegionDifference = 4;
    private const int WindowLongExtendedStyle = -20;
    private const uint WindowMessageNonClientHitTest = 0x0084;
    private const uint WindowMessageMouseActivate = 0x0021;
    private const uint WindowMessageEraseBackground = 0x0014;
    private const uint WindowMessageProbeComplete = 0x8000 + 0x2D1;
    private const uint GetWindowOwner = 4;
    private const string WindowClassName = "DropSpace.EphemeralOleDragProbe.v1";
    private static readonly object ClassGate = new();
    private static readonly Dictionary<nint, EphemeralOleDragProbe> Probes = [];
    private static readonly WindowProcedureCallback SharedWindowProcedure = StaticWindowProcedure;
    private static ushort _windowClass;
    private readonly object _completionGate = new();
    private readonly long _sessionId;
    private readonly DragScreenPoint _origin;
    private readonly DragScreenPoint _probeCenter;
    private readonly SmartDragProbeOptions _options;
    private readonly Action<OleDragProbeResult> _completed;
    private readonly ILogger _logger;
    private readonly ProbeDropTarget _dropTarget;
    private readonly SynchronizationContext? _ownerContext;
    private readonly Func<nint, uint, nint, nint, bool> _postCompletion;
    private readonly long _createdTimestamp = Stopwatch.GetTimestamp();
    private Timer? _lifetimeTimer;
    private Timer? _cleanupWatchdog;
    private OleDragProbeResult? _pendingResult;
    private bool _registered;
    private int _disposeState;

    public EphemeralOleDragProbe(
        long sessionId,
        DragScreenPoint origin,
        MonitorDescriptor monitor,
        SmartDragProbeOptions options,
        OleFileDataClassifier classifier,
        Action<OleDragProbeResult> completed,
        ILogger logger,
        Func<nint, uint, nint, nint, bool>? postCompletion = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sessionId);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(completed);
        ArgumentNullException.ThrowIfNull(logger);
        options.Validate();

        _sessionId = sessionId;
        _origin = origin;
        _probeCenter = CalculateMonitorAwareCenter(origin, monitor, options);
        _options = options;
        _completed = completed;
        _logger = logger;
        _ownerContext = SynchronizationContext.Current;
        _postCompletion = postCompletion ?? PostMessage;
        _dropTarget = new ProbeDropTarget(this, classifier, logger);

        EnsureWindowClass();
        var left = _probeCenter.X - options.OuterSizePixels / 2;
        var top = _probeCenter.Y - options.OuterSizePixels / 2;
        WindowHandle = CreateWindowEx(
            unchecked((uint)(
                ExtendedStyleTopmost |
                ExtendedStyleToolWindow |
                ExtendedStyleLayered |
                ExtendedStyleNoActivate)),
            WindowClassName,
            string.Empty,
            WindowStylePopup,
            left,
            top,
            options.OuterSizePixels,
            options.OuterSizePixels,
            nint.Zero,
            nint.Zero,
            GetModuleHandle(null),
            nint.Zero);
        if (WindowHandle == nint.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The Smart drag verification probe HWND could not be created.");
        }

        lock (ClassGate)
        {
            Probes[WindowHandle] = this;
        }

        try
        {
            if (!SetLayeredWindowAttributes(WindowHandle, 0, 1, LayeredAlpha))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "The Smart drag verification probe could not be made visually transparent.");
            }

            ApplyHollowRegion();
            OleDropTargetNative.Register(WindowHandle, _dropTarget);
            _registered = true;
            _ = ShowWindow(WindowHandle, ShowNoActivate);
            _lifetimeTimer = new Timer(
                static state =>
                {
                    var probe = (EphemeralOleDragProbe)state!;
                    probe.QueueCompletion(
                        OleDragProbeOutcome.TimedOut,
                        OleFileDataClassification.None,
                        probe._origin);
                },
                this,
                options.HardLifetime,
                Timeout.InfiniteTimeSpan);
            _logger.LogInformation(
                "Smart OLE verification probe created for session {SessionId}: outer={OuterPixels}px, hole={HolePixels}px, hardLifetime={LifetimeMilliseconds}ms.",
                _sessionId,
                options.OuterSizePixels,
                options.CenterHolePixels,
                options.HardLifetimeMilliseconds);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public nint WindowHandle { get; }

    public long SessionId => _sessionId;

    public bool IsDisposed => Volatile.Read(ref _disposeState) != 0;

    internal bool VerifyNativeContract()
    {
        if (IsDisposed || WindowHandle == nint.Zero || !IsWindowVisible(WindowHandle))
        {
            return false;
        }

        var extendedStyle = GetWindowLongPtr(WindowHandle, WindowLongExtendedStyle).ToInt64();
        var requiredStyle = ExtendedStyleTopmost |
                            ExtendedStyleToolWindow |
                            ExtendedStyleLayered |
                            ExtendedStyleNoActivate;
        if ((extendedStyle & requiredStyle) != requiredStyle ||
            GetWindow(WindowHandle, GetWindowOwner) != nint.Zero ||
            !GetWindowRect(WindowHandle, out var bounds))
        {
            return false;
        }

        var region = CreateRectRgn(0, 0, 1, 1);
        if (region == nint.Zero)
        {
            return false;
        }

        try
        {
            if (GetWindowRgn(WindowHandle, region) == 0)
            {
                return false;
            }

            var center = _options.OuterSizePixels / 2;
            var ringInset = Math.Max(1, (_options.OuterSizePixels - _options.CenterHolePixels) / 4);
            return bounds.Left + center == _probeCenter.X &&
                   bounds.Top + center == _probeCenter.Y &&
                   !PtInRegion(region, center, center) &&
                   PtInRegion(region, ringInset, center);
        }
        finally
        {
            _ = DeleteObject(region);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }
        _lifetimeTimer?.Dispose();
        _lifetimeTimer = null;
        _cleanupWatchdog?.Dispose();
        _cleanupWatchdog = null;
        if (_registered)
        {
            var result = OleDropTargetNative.Revoke(WindowHandle);
            if (result < 0)
            {
                _logger.LogWarning(
                    "RevokeDragDrop failed for Smart OLE probe session {SessionId} with HRESULT 0x{HResult:X8}.",
                    _sessionId,
                    result);
            }

            _registered = false;
        }

        lock (ClassGate)
        {
            Probes.Remove(WindowHandle);
        }

        if (WindowHandle != nint.Zero)
        {
            _ = DestroyWindow(WindowHandle);
        }

        _logger.LogInformation(
            "Smart OLE verification probe disposed for session {SessionId} after {ElapsedMilliseconds:F1}ms.",
            _sessionId,
            Stopwatch.GetElapsedTime(_createdTimestamp).TotalMilliseconds);
    }

    private void ApplyHollowRegion()
    {
        var outer = CreateRectRgn(0, 0, _options.OuterSizePixels, _options.OuterSizePixels);
        var holeLeft = (_options.OuterSizePixels - _options.CenterHolePixels) / 2;
        var inner = CreateRectRgn(
            holeLeft,
            holeLeft,
            holeLeft + _options.CenterHolePixels,
            holeLeft + _options.CenterHolePixels);
        if (outer == nint.Zero || inner == nint.Zero)
        {
            if (outer != nint.Zero)
            {
                _ = DeleteObject(outer);
            }

            if (inner != nint.Zero)
            {
                _ = DeleteObject(inner);
            }

            throw new Win32Exception(Marshal.GetLastWin32Error(), "The Smart drag verification probe Region could not be allocated.");
        }

        try
        {
            if (CombineRgn(outer, outer, inner, RegionDifference) == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The Smart drag verification probe Region could not be combined.");
            }

            if (SetWindowRgn(WindowHandle, outer, false) == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The Smart drag verification probe Region could not be applied.");
            }

            // SetWindowRgn transfers ownership to the system after success.
            outer = nint.Zero;
        }
        finally
        {
            if (outer != nint.Zero)
            {
                _ = DeleteObject(outer);
            }

            _ = DeleteObject(inner);
        }
    }

    internal static DragScreenPoint CalculateMonitorAwareCenter(
        DragScreenPoint pointer,
        MonitorDescriptor monitor,
        SmartDragProbeOptions options)
    {
        var half = options.OuterSizePixels / 2;
        var minimumX = monitor.Left + half;
        var maximumX = monitor.Left + monitor.Width - half;
        var minimumY = monitor.Top + half;
        var maximumY = monitor.Top + monitor.Height - half;
        if (maximumX < minimumX || maximumY < minimumY)
        {
            return pointer;
        }

        // Clamp the physical-pixel probe into the selected monitor. At an edge this creates an
        // intentional inward/asymmetric ring instead of spilling onto a differently scaled display.
        return new DragScreenPoint(
            Math.Clamp(pointer.X, minimumX, maximumX),
            Math.Clamp(pointer.Y, minimumY, maximumY));
    }

    private void QueueCompletion(
        OleDragProbeOutcome outcome,
        OleFileDataClassification classification,
        DragScreenPoint point)
    {
        lock (_completionGate)
        {
            if (IsDisposed || _pendingResult is not null)
            {
                return;
            }

            _pendingResult = new OleDragProbeResult(
                _sessionId,
                outcome,
                classification,
                point,
                Stopwatch.GetElapsedTime(_createdTimestamp));
        }

        _cleanupWatchdog ??= new Timer(
            static state => ((EphemeralOleDragProbe)state!).ForceCleanupAfterQueueFailure(),
            this,
            TimeSpan.FromMilliseconds(250),
            Timeout.InfiniteTimeSpan);

        if (!_postCompletion(WindowHandle, WindowMessageProbeComplete, nint.Zero, nint.Zero))
        {
            _logger.LogWarning(
                "Smart OLE probe PostMessage completion failed for session {SessionId}; Win32 error {Error}. Falling back to the owner work queue and forced cleanup watchdog.",
                _sessionId,
                Marshal.GetLastWin32Error());
            if (_ownerContext is { } ownerContext)
            {
                ownerContext.Post(static state => ((EphemeralOleDragProbe)state!).CompleteOnOwnerThread(), this);
            }
        }
    }

    private void ForceCleanupAfterQueueFailure()
    {
        if (IsDisposed)
        {
            return;
        }

        _logger.LogWarning(
            "Smart OLE probe forced cleanup watchdog fired for session {SessionId}; registry, timer, HWND and OLE registration will be released.",
            _sessionId);
        CompleteOnOwnerThread();
        Dispose();
    }

    private void CompleteOnOwnerThread()
    {
        OleDragProbeResult? result;
        lock (_completionGate)
        {
            result = _pendingResult;
            _pendingResult = null;
        }

        if (result is null || IsDisposed)
        {
            return;
        }

        try
        {
            _completed(result);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Smart OLE probe result handling failed for session {SessionId}.", _sessionId);
        }
        finally
        {
            Dispose();
        }
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
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The Smart drag verification probe window class could not be registered.");
            }
        }
    }

    private static nint StaticWindowProcedure(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == WindowMessageNonClientHitTest)
        {
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

        if (message == WindowMessageProbeComplete)
        {
            EphemeralOleDragProbe? probe;
            lock (ClassGate)
            {
                Probes.TryGetValue(window, out probe);
            }

            probe?.CompleteOnOwnerThread();
            return nint.Zero;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private delegate nint WindowProcedureCallback(nint window, uint message, nint wParam, nint lParam);

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class ProbeDropTarget : IOleDropTarget
    {
        private const uint DropEffectNone = 0;
        private readonly EphemeralOleDragProbe _owner;
        private readonly OleFileDataClassifier _classifier;
        private readonly ILogger _logger;

        public ProbeDropTarget(
            EphemeralOleDragProbe owner,
            OleFileDataClassifier classifier,
            ILogger logger)
        {
            _owner = owner;
            _classifier = classifier;
            _logger = logger;
        }

        public int DragEnter(IDataObject dataObject, uint keyState, NativePoint point, ref uint effect)
        {
            effect = DropEffectNone;
            try
            {
                var classification = _classifier.Classify(dataObject);
                var outcome = classification.IsFileLike
                    ? OleDragProbeOutcome.VerifiedFile
                    : OleDragProbeOutcome.Rejected;
                _logger.LogInformation(
                    "Smart OLE probe DragEnter received for session {SessionId}: classification={Classification}, fileLike={FileLike}; effect=None.",
                    _owner.SessionId,
                    classification.Kind,
                    classification.IsFileLike);
                _owner.QueueCompletion(
                    outcome,
                    classification,
                    new DragScreenPoint(point.X, point.Y));
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Smart OLE probe classification failed for session {SessionId}.", _owner.SessionId);
                _owner.QueueCompletion(
                    OleDragProbeOutcome.Rejected,
                    OleFileDataClassification.None,
                    new DragScreenPoint(point.X, point.Y));
            }

            return Success;
        }

        public int DragOver(uint keyState, NativePoint point, ref uint effect)
        {
            effect = DropEffectNone;
            return Success;
        }

        public int DragLeave() => Success;

        public int Drop(IDataObject dataObject, uint keyState, NativePoint point, ref uint effect)
        {
            effect = DropEffectNone;
            return Success;
        }
    }

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

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern nint CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int CombineRgn(nint destination, nint source1, nint source2, int mode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(nint window, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out WindowRectangle rectangle);

    [DllImport("user32.dll")]
    private static extern int GetWindowRgn(nint window, nint region);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PtInRegion(nint region, int x, int y);
}
