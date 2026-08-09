using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services;

public sealed record ClipboardNotification(
    uint SequenceNumber,
    DateTimeOffset ObservedAtUtc);

public sealed record ClipboardNotificationStatus(
    bool IsRegistered,
    DateTimeOffset? LastNotificationUtc,
    long ObservedUpdateCount,
    string? Error);

/// <summary>
/// Bridges the desktop clipboard listener contract into the capture pipeline. The main WinUI HWND
/// remains alive while the app is in the tray, so it provides a stable message-pump owner without
/// requiring polling or a second hidden XAML window.
/// </summary>
public sealed class ClipboardNotificationService : IDisposable
{
    private const uint ClipboardUpdateMessage = 0x031D;
    private const uint SubclassId = 0x4453434C;
    private readonly ILogger<ClipboardNotificationService> _logger;
    private readonly SubclassProc _subclassProc;
    private nint _windowHandle;
    private DateTimeOffset? _lastNotificationUtc;
    private long _observedUpdateCount;
    private string? _error;
    private bool _registered;
    private bool _disposed;

    public ClipboardNotificationService(ILogger<ClipboardNotificationService> logger)
    {
        _logger = logger;
        _subclassProc = WindowSubclassProc;
    }

    public event EventHandler<ClipboardNotification>? ClipboardChanged;

    public event EventHandler<ClipboardNotificationStatus>? StatusChanged;

    public ClipboardNotificationStatus Status => new(
        _registered,
        _lastNotificationUtc,
        Interlocked.Read(ref _observedUpdateCount),
        _error);

    public void Initialize(nint windowHandle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_registered)
        {
            return;
        }

        if (windowHandle == nint.Zero)
        {
            throw new ArgumentException("A valid message-pump window is required.", nameof(windowHandle));
        }

        _windowHandle = windowHandle;
        if (!SetWindowSubclass(_windowHandle, _subclassProc, new UIntPtr(SubclassId), UIntPtr.Zero))
        {
            FailRegistration(new Win32Exception(Marshal.GetLastWin32Error(), "The clipboard window subclass could not be installed."));
            return;
        }

        if (!AddClipboardFormatListener(_windowHandle))
        {
            var exception = new Win32Exception(Marshal.GetLastWin32Error(), "AddClipboardFormatListener failed.");
            RemoveWindowSubclass(_windowHandle, _subclassProc, new UIntPtr(SubclassId));
            FailRegistration(exception);
            return;
        }

        _registered = true;
        _error = null;
        _logger.LogInformation(
            "Clipboard listener registered on HWND {WindowHandle}; sequence {SequenceNumber}.",
            _windowHandle,
            GetClipboardSequenceNumber());
        StatusChanged?.Invoke(this, Status);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_windowHandle != nint.Zero)
        {
            if (_registered && !RemoveClipboardFormatListener(_windowHandle))
            {
                _logger.LogWarning(
                    "RemoveClipboardFormatListener failed with Win32 error {ErrorCode}.",
                    Marshal.GetLastWin32Error());
            }

            RemoveWindowSubclass(_windowHandle, _subclassProc, new UIntPtr(SubclassId));
        }

        _registered = false;
        _windowHandle = nint.Zero;
        _disposed = true;
    }

    private nint WindowSubclassProc(
        nint window,
        uint message,
        nint wParam,
        nint lParam,
        UIntPtr subclassId,
        UIntPtr referenceData)
    {
        if (message != ClipboardUpdateMessage)
        {
            return DefSubclassProc(window, message, wParam, lParam);
        }

        var observedAt = DateTimeOffset.UtcNow;
        var sequence = GetClipboardSequenceNumber();
        _lastNotificationUtc = observedAt;
        var observed = Interlocked.Increment(ref _observedUpdateCount);
        _logger.LogInformation(
            "WM_CLIPBOARDUPDATE received; sequence {SequenceNumber}, observed count {ObservedCount}.",
            sequence,
            observed);
        ClipboardChanged?.Invoke(this, new ClipboardNotification(sequence, observedAt));
        StatusChanged?.Invoke(this, Status);
        return nint.Zero;
    }

    private void FailRegistration(Exception exception)
    {
        _registered = false;
        _error = exception.Message;
        _logger.LogError(exception, "Clipboard listener registration failed.");
        StatusChanged?.Invoke(this, Status);
    }

    private delegate nint SubclassProc(
        nint window,
        uint message,
        nint wParam,
        nint lParam,
        UIntPtr subclassId,
        UIntPtr referenceData);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(nint window);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint window, SubclassProc callback, UIntPtr id, UIntPtr referenceData);

    [DllImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint window, SubclassProc callback, UIntPtr id);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint window, uint message, nint wParam, nint lParam);
}
