using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services;

public sealed record DragActivationCallbacks(
    Action<string> DragApproaching,
    Action<string, bool> DragReadyChanged,
    Action<string> DragLeft,
    Func<string, IReadOnlyList<string>, Task> Dropped);

/// <summary>
/// Owns OLE initialization and native drop-target registrations. Both the zero-alpha reveal host
/// and the independently visible WinUI island feed the same callbacks and AddPathsAsync pipeline.
/// </summary>
public sealed class OleDragDropService : IDisposable
{
    private const int Success = 0;
    private const int SuccessAlreadyInitialized = 1;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<OleDragDropService> _logger;
    private readonly List<IDisposable> _registrations = [];
    private bool _oleInitialized;
    private bool _disposed;

    public OleDragDropService(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<OleDragDropService>();
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
            _loggerFactory.CreateLogger<DragActivationHost>());
        _registrations.Add(host);
        return host;
    }

    public IDisposable RegisterVisualTarget(
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
            _ => true,
            "visual-overlay");
        _registrations.Add(registration);
        return registration;
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
}

public sealed class DragActivationHost : IDisposable
{
    public const double WidthDips = 680;
    public const double HeightDips = 72;
    private const int HitTestTransparent = -1;
    private const int MouseActivateNoActivate = 3;
    private const uint WindowStylePopup = 0x80000000;
    private const uint ExtendedStyleTopmost = 0x00000008;
    private const uint ExtendedStyleToolWindow = 0x00000080;
    private const uint ExtendedStyleLayered = 0x00080000;
    private const uint ExtendedStyleNoActivate = 0x08000000;
    private const uint LayeredAlpha = 0x00000002;
    private const int ShowNoActivate = 4;
    private const int HideWindow = 0;
    private const uint WindowMessageNonClientHitTest = 0x0084;
    private const uint WindowMessageMouseActivate = 0x0021;
    private const uint WindowMessageEraseBackground = 0x0014;
    private const uint WindowMessageDisplayChange = 0x007E;
    private const string WindowClassName = "DropSpace.DragActivationHost.v2";
    private static readonly object ClassGate = new();
    private static readonly Dictionary<nint, DragActivationHost> Hosts = [];
    private static readonly WindowProcedureCallback SharedWindowProcedure = StaticWindowProcedure;
    private static ushort _windowClass;
    private readonly MonitorDescriptor _monitor;
    private readonly ILogger<DragActivationHost> _logger;
    private readonly OleDropTargetRegistration _dropTarget;
    private readonly NativeRectangle _bounds;
    private bool _disposed;

    public DragActivationHost(
        MonitorDescriptor monitor,
        DragActivationCallbacks callbacks,
        ILogger<DragActivationHost> logger)
    {
        _monitor = monitor;
        _logger = logger;
        EnsureWindowClass();

        var width = ToPixels(WidthDips);
        var height = ToPixels(HeightDips);
        var left = monitor.Left + (monitor.Width - width) / 2;
        _bounds = new NativeRectangle(left, monitor.Top, left + width, monitor.Top + height);
        WindowHandle = CreateWindowEx(
            ExtendedStyleTopmost | ExtendedStyleToolWindow | ExtendedStyleLayered | ExtendedStyleNoActivate,
            WindowClassName,
            string.Empty,
            WindowStylePopup,
            _bounds.Left,
            _bounds.Top,
            width,
            height,
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

        if (!SetLayeredWindowAttributes(WindowHandle, 0, 0, LayeredAlpha))
        {
            var exception = new Win32Exception(Marshal.GetLastWin32Error(), "The activation HWND could not be made zero-alpha.");
            DestroyHostWindow();
            throw exception;
        }

        ShowWindow(WindowHandle, ShowNoActivate);
        _dropTarget = new OleDropTargetRegistration(
            WindowHandle,
            monitor.Id,
            callbacks,
            logger,
            IsDropReady,
            "activation-host");
        _logger.LogInformation(
            "Drag activation host created on monitor {MonitorId}: HWND {WindowHandle}, DPI {Dpi}, bounds {Left},{Top},{Width},{Height}; zero-alpha=yes, mouse-hit-test=transparent.",
            monitor.Id,
            WindowHandle,
            monitor.Dpi,
            _bounds.Left,
            _bounds.Top,
            width,
            height);
    }

    public event EventHandler? DisplayTopologyChanged;

    public nint WindowHandle { get; }

    public string MonitorId => _monitor.Id;

    public void SetEnabled(bool enabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ShowWindow(WindowHandle, enabled ? ShowNoActivate : HideWindow);
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
        var horizontalInset = ToPixels(90);
        var topInset = ToPixels(10);
        return point.X >= _bounds.Left + horizontalInset &&
               point.X < _bounds.Right - horizontalInset &&
               point.Y >= _bounds.Top + topInset &&
               point.Y < _bounds.Bottom;
    }

    private int ToPixels(double dips) => Math.Max(1, (int)Math.Round(dips * _monitor.Scale));

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
            // Deliberately use WM_NCHITTEST rather than WS_EX_TRANSPARENT. The latter is a paint
            // ordering flag and can make OLE target discovery inconsistent; HTTRANSPARENT keeps
            // ordinary clicks flowing to the desktop/window beneath this zero-alpha host.
            return new nint(HitTestTransparent);
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

    private readonly record struct NativeRectangle(int Left, int Top, int Right, int Bottom);

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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);
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
    private const int DragDropAlreadyRegistered = unchecked((int)0x80040101);
    private const uint DropEffectNone = 0;
    private const uint DropEffectCopy = 1;
    private const short ClipboardFormatHDrop = 15;
    private const uint QueryAllFiles = 0xFFFFFFFF;
    private readonly nint _windowHandle;
    private readonly string _monitorId;
    private readonly DragActivationCallbacks _callbacks;
    private readonly ILogger _logger;
    private readonly Func<NativePoint, bool> _isReady;
    private readonly string _surfaceKind;
    private IDataObject? _currentDataObject;
    private bool _canAccept;
    private bool _lastReady;
    private long _dragOverCount;
    private bool _disposed;

    public OleDropTargetRegistration(
        nint windowHandle,
        string monitorId,
        DragActivationCallbacks callbacks,
        ILogger logger,
        Func<NativePoint, bool> isReady,
        string surfaceKind)
    {
        _windowHandle = windowHandle;
        _monitorId = monitorId;
        _callbacks = callbacks;
        _logger = logger;
        _isReady = isReady;
        _surfaceKind = surfaceKind;
        var result = RegisterDragDrop(windowHandle, this);
        if (result == DragDropAlreadyRegistered)
        {
            throw new InvalidOperationException($"HWND {windowHandle} already has an OLE drop target.");
        }

        if (result < 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }

        _logger.LogInformation(
            "RegisterDragDrop succeeded for {SurfaceKind} on monitor {MonitorId}, HWND {WindowHandle}.",
            _surfaceKind,
            _monitorId,
            _windowHandle);
    }

    public int DragEnter(IDataObject dataObject, uint keyState, NativePoint point, ref uint effect)
    {
        _currentDataObject = dataObject;
        var cfHDrop = HasFormat(dataObject, ClipboardFormatHDrop);
        var shellIdListFormat = unchecked((short)RegisterClipboardFormat("Shell IDList Array"));
        var storageItems = shellIdListFormat != 0 && HasFormat(dataObject, shellIdListFormat);
        _canAccept = cfHDrop;
        effect = _canAccept ? DropEffectCopy : DropEffectNone;
        _logger.LogInformation(
            "OLE DragEnter received by {SurfaceKind} on monitor {MonitorId}: CF_HDROP={CfHDrop}, StorageItems={StorageItems}, accepted={Accepted}.",
            _surfaceKind,
            _monitorId,
            cfHDrop,
            storageItems,
            _canAccept);
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
        _callbacks.DragLeft(_monitorId);
        return Success;
    }

    public int Drop(IDataObject dataObject, uint keyState, NativePoint point, ref uint effect)
    {
        try
        {
            var paths = ReadDropPaths(dataObject);
            var ready = _canAccept && _isReady(point);
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
                _ = CompleteDropAsync(paths);
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
        }

        return Success;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        var result = RevokeDragDrop(_windowHandle);
        if (result < 0)
        {
            _logger.LogWarning(
                "RevokeDragDrop failed for {SurfaceKind} HWND {WindowHandle} with HRESULT 0x{HResult:X8}.",
                _surfaceKind,
                _windowHandle,
                result);
        }

        _disposed = true;
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

    private static bool HasFormat(IDataObject dataObject, short format)
    {
        var formatEtc = CreateFormat(format);
        return dataObject.QueryGetData(ref formatEtc) == Success;
    }

    private static IReadOnlyList<string> ReadDropPaths(IDataObject dataObject)
    {
        var format = CreateFormat(ClipboardFormatHDrop);
        if (dataObject.QueryGetData(ref format) != Success)
        {
            return [];
        }

        dataObject.GetData(ref format, out var medium);
        try
        {
            if (medium.tymed != TYMED.TYMED_HGLOBAL || medium.unionmember == nint.Zero)
            {
                return [];
            }

            var count = DragQueryFile(medium.unionmember, QueryAllFiles, null, 0);
            var paths = new List<string>(checked((int)count));
            for (uint index = 0; index < count; index++)
            {
                var length = DragQueryFile(medium.unionmember, index, null, 0);
                if (length == 0)
                {
                    continue;
                }

                var buffer = new char[length + 1];
                if (DragQueryFile(medium.unionmember, index, buffer, (uint)buffer.Length) > 0)
                {
                    paths.Add(new string(buffer, 0, checked((int)length)));
                }
            }

            return paths;
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }

    private static FORMATETC CreateFormat(short format) => new()
    {
        cfFormat = format,
        dwAspect = DVASPECT.DVASPECT_CONTENT,
        lindex = -1,
        ptd = nint.Zero,
        tymed = TYMED.TYMED_HGLOBAL,
    };

    [DllImport("ole32.dll")]
    private static extern int RegisterDragDrop(
        nint window,
        [MarshalAs(UnmanagedType.Interface)] IOleDropTarget dropTarget);

    [DllImport("ole32.dll")]
    private static extern int RevokeDragDrop(nint window);

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(nint drop, uint file, [Out] char[]? fileName, uint characterCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string format);
}
