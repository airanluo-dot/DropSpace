using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace DropSpace.App.Services;

public sealed class ForegroundWindowMonitor : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0x0000;
    private const uint WineventSkipOwnProcess = 0x0002;
    private readonly DispatcherQueue _dispatcher;
    private readonly ILogger<ForegroundWindowMonitor> _logger;
    private readonly WinEventCallback _callback;
    private nint _hook;

    public ForegroundWindowMonitor(
        DispatcherQueue dispatcher,
        ILogger<ForegroundWindowMonitor> logger)
    {
        _dispatcher = dispatcher;
        _logger = logger;
        _callback = OnWinEvent;
    }

    public event EventHandler? ForegroundChanged;

    public void Start()
    {
        if (_hook != nint.Zero)
        {
            return;
        }

        _hook = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            nint.Zero,
            _callback,
            0,
            0,
            WineventOutOfContext | WineventSkipOwnProcess);
        if (_hook == nint.Zero)
        {
            _logger.LogWarning("Foreground-window notifications are unavailable; fullscreen recovery will wait for the next Overlay event.");
        }
    }

    public void Dispose()
    {
        if (_hook == nint.Zero)
        {
            return;
        }

        UnhookWinEvent(_hook);
        _hook = nint.Zero;
    }

    private void OnWinEvent(
        nint hook,
        uint eventType,
        nint window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        _dispatcher.TryEnqueue(() => ForegroundChanged?.Invoke(this, EventArgs.Empty));
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void WinEventCallback(
        nint hook,
        uint eventType,
        nint window,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint eventHookModule,
        WinEventCallback callback,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);
}
