using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services;

public sealed class NativeTrayService : IDisposable
{
    private const uint CallbackMessage = 0x8001;
    private const uint IconId = 1;
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmContextMenu = 0x007B;
    private const uint NinSelect = 0x0400;
    private const uint NinKeySelect = 0x0401;
    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;
    private const uint MfChecked = 0x00000008;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmNonotify = 0x0080;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x0010;
    private const uint LrDefaultSize = 0x0040;
    private const uint MenuOpen = 1001;
    private const uint MenuPause = 1002;
    private const uint MenuClear = 1003;
    private const uint MenuExit = 1004;
    private const uint SubclassId = 0x4453;

    private readonly IntPtr _windowHandle;
    private readonly ILogger<NativeTrayService> _logger;
    private readonly SubclassProc _subclassProc;
    private readonly uint _taskbarCreatedMessage;
    private IntPtr _iconHandle;
    private bool _added;
    private bool _paused;
    private bool _disposed;

    public NativeTrayService(IntPtr windowHandle, string iconPath, ILogger<NativeTrayService> logger)
    {
        if (windowHandle == IntPtr.Zero)
        {
            throw new ArgumentException("A valid window handle is required.", nameof(windowHandle));
        }

        _windowHandle = windowHandle;
        _logger = logger;
        _subclassProc = WindowSubclassProc;
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        _iconHandle = LoadImage(IntPtr.Zero, iconPath, ImageIcon, 0, 0, LrLoadFromFile | LrDefaultSize);
        if (_iconHandle == IntPtr.Zero)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "The tray icon could not be loaded.");
        }

        if (!SetWindowSubclass(_windowHandle, _subclassProc, new UIntPtr(SubclassId), UIntPtr.Zero))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "The tray window hook could not be installed.");
        }
    }

    public event EventHandler? OpenRequested;

    public event EventHandler? TogglePauseRequested;

    public event EventHandler? ClearRequested;

    public event EventHandler? ExitRequested;

    public bool IsAvailable => _added;

    public bool Add()
    {
        ThrowIfDisposed();
        if (_added)
        {
            return true;
        }

        var data = CreateData();
        _added = ShellNotifyIcon(NimAdd, ref data);
        if (!_added)
        {
            _logger.LogError("Shell_NotifyIcon failed to add the notification-area icon.");
        }

        return _added;
    }

    public void SetPaused(bool paused)
    {
        ThrowIfDisposed();
        _paused = paused;
        if (!_added)
        {
            return;
        }

        var data = CreateData();
        ShellNotifyIcon(NimModify, ref data);
    }

    public void Remove()
    {
        if (!_added)
        {
            return;
        }

        var data = CreateData();
        ShellNotifyIcon(NimDelete, ref data);
        _added = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Remove();
        RemoveWindowSubclass(_windowHandle, _subclassProc, new UIntPtr(SubclassId));
        if (_iconHandle != IntPtr.Zero)
        {
            DestroyIcon(_iconHandle);
            _iconHandle = IntPtr.Zero;
        }

        _disposed = true;
    }

    private IntPtr WindowSubclassProc(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr referenceData)
    {
        if (message == _taskbarCreatedMessage)
        {
            _added = false;
            Add();
            return IntPtr.Zero;
        }

        if (message == CallbackMessage)
        {
            var eventCode = unchecked((uint)lParam.ToInt64());
            if (eventCode is WmLButtonUp or NinSelect or NinKeySelect)
            {
                OpenRequested?.Invoke(this, EventArgs.Empty);
                return IntPtr.Zero;
            }

            if (eventCode is WmRButtonUp or WmContextMenu)
            {
                ShowContextMenu();
                return IntPtr.Zero;
            }
        }

        return DefSubclassProc(window, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            AppendMenu(menu, MfString, MenuOpen, "Open DropSpace");
            AppendMenu(menu, MfString | (_paused ? MfChecked : 0), MenuPause, _paused ? "Resume Clipboard" : "Pause Clipboard");
            AppendMenu(menu, MfString, MenuClear, "Clear Clipboard History…");
            AppendMenu(menu, MfSeparator, 0, null);
            AppendMenu(menu, MfString, MenuExit, "Exit");
            GetCursorPos(out var point);
            SetForegroundWindow(_windowHandle);
            var selected = TrackPopupMenuEx(menu, TpmRightButton | TpmReturnCmd | TpmNonotify, point.X, point.Y, _windowHandle, IntPtr.Zero);
            switch (selected)
            {
                case MenuOpen:
                    OpenRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case MenuPause:
                    TogglePauseRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case MenuClear:
                    ClearRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case MenuExit:
                    ExitRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private NotifyIconData CreateData() => new()
    {
        Size = (uint)Marshal.SizeOf<NotifyIconData>(),
        WindowHandle = _windowHandle,
        Id = IconId,
        Flags = NifMessage | NifIcon | NifTip,
        CallbackMessage = CallbackMessage,
        IconHandle = _iconHandle,
        Tip = _paused ? "DropSpace — Clipboard paused" : "DropSpace — Clipboard recording",
        Info = string.Empty,
        InfoTitle = string.Empty,
    };

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public IntPtr WindowHandle;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public IntPtr IconHandle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;

        public uint State;
        public uint StateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;

        public uint TimeoutOrVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;

        public uint InfoFlags;
        public Guid ItemGuid;
        public IntPtr BalloonIconHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    private delegate IntPtr SubclassProc(
        IntPtr window,
        uint message,
        IntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr referenceData);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    private static bool ShellNotifyIcon(uint message, ref NotifyIconData data) => Shell_NotifyIcon(message, ref data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr window, SubclassProc callback, UIntPtr id, UIntPtr referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr window, SubclassProc callback, UIntPtr id);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint load);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, uint id, string? text);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr window, IntPtr parameters);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
}
