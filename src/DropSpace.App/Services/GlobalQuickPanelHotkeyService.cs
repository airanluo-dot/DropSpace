using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services;

/// <summary>Owns a process-lifetime RegisterHotKey message thread for the Dynamic Island Quick Panel.</summary>
public sealed class GlobalQuickPanelHotkeyService : IDisposable
{
    private const int HotkeyId = 0x4453;
    private const uint WindowMessageHotkey = 0x0312;
    private const uint WindowMessageQuit = 0x0012;
    private const uint ModifierAlt = 0x0001;
    private const uint ModifierControl = 0x0002;
    private const uint ModifierShift = 0x0004;
    private const uint ModifierWindows = 0x0008;
    private const uint ModifierNoRepeat = 0x4000;
    private readonly ILogger<GlobalQuickPanelHotkeyService> _logger;
    private Thread? _thread;
    private uint _threadId;
    private HotkeyDefinition _definition;
    private string? _gesture;
    private readonly ManualResetEventSlim _ready = new(false);
    private bool _disposed;

    public GlobalQuickPanelHotkeyService(ILogger<GlobalQuickPanelHotkeyService> logger) =>
        _logger = logger;

    public event EventHandler? Invoked;

    public bool IsRegistered { get; private set; }

    public bool CanRegister(string gesture)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var definition = Parse(gesture);
        if (IsRegistered && string.Equals(_gesture, gesture, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        const int probeId = HotkeyId + 1;
        if (!RegisterHotKey(nint.Zero, probeId, definition.Modifiers | ModifierNoRepeat, definition.VirtualKey))
        {
            return false;
        }
        _ = UnregisterHotKey(nint.Zero, probeId);
        return true;
    }

    public bool TryStart(string gesture)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ = Parse(gesture);
        if (IsRegistered && string.Equals(_gesture, gesture, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (!CanRegister(gesture))
        {
            return false;
        }
        var previous = _gesture;
        Stop();
        StartCore(gesture);
        _ = _ready.Wait(TimeSpan.FromSeconds(2));
        if (IsRegistered)
        {
            _gesture = gesture;
            return true;
        }
        if (!string.IsNullOrWhiteSpace(previous))
        {
            Stop();
            StartCore(previous);
            _ = _ready.Wait(TimeSpan.FromSeconds(2));
            if (IsRegistered)
            {
                _gesture = previous;
            }
        }
        return false;
    }

    public void Start(string gesture) => _ = TryStart(gesture);

    private void StartCore(string gesture)
    {
        _ready.Reset();
        _definition = Parse(gesture);
        _thread = new Thread(MessageThreadMain)
        {
            IsBackground = true,
            Name = "DropSpace Quick Panel hotkey",
        };
        _thread.Start();
    }

    public void Stop()
    {
        if (_thread is { IsAlive: true } && _threadId != 0)
        {
            _ = PostThreadMessage(_threadId, WindowMessageQuit, nint.Zero, nint.Zero);
            _thread.Join(TimeSpan.FromSeconds(2));
        }
        else if (_thread is { IsAlive: true })
        {
            _ = _ready.Wait(TimeSpan.FromSeconds(2));
            if (_threadId != 0)
            {
                _ = PostThreadMessage(_threadId, WindowMessageQuit, nint.Zero, nint.Zero);
                _thread.Join(TimeSpan.FromSeconds(2));
            }
        }
        _thread = null;
        _threadId = 0;
        IsRegistered = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        Stop();
        _ready.Dispose();
        _disposed = true;
    }

    private void MessageThreadMain()
    {
        _threadId = GetCurrentThreadId();
        try
        {
            if (!RegisterHotKey(
                nint.Zero,
                HotkeyId,
                _definition.Modifiers | ModifierNoRepeat,
                _definition.VirtualKey))
            {
                _ready.Set();
                _logger.LogWarning(
                    "Quick Panel hotkey registration failed with Win32 error {Error}; the Dynamic Island remains available by pointer.",
                    Marshal.GetLastWin32Error());
                return;
            }
            IsRegistered = true;
            _ready.Set();
            _logger.LogInformation("Quick Panel global hotkey registered without logging the configured key sequence.");
            while (GetMessage(out var message, nint.Zero, 0, 0) > 0)
            {
                if (message.Message == WindowMessageHotkey && message.WParam.ToInt32() == HotkeyId)
                {
                    Invoked?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Quick Panel hotkey observer stopped unexpectedly.");
        }
        finally
        {
            _ready.Set();
            _ = UnregisterHotKey(nint.Zero, HotkeyId);
            IsRegistered = false;
        }
    }

    internal static HotkeyDefinition Parse(string gesture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gesture);
        var parts = gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        uint modifiers = 0;
        uint key = 0;
        foreach (var part in parts)
        {
            if (part.Equals("Win", StringComparison.OrdinalIgnoreCase)) modifiers |= ModifierWindows;
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase)) modifiers |= ModifierShift;
            else if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) modifiers |= ModifierControl;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase)) modifiers |= ModifierAlt;
            else if (part.Equals("Space", StringComparison.OrdinalIgnoreCase)) key = 0x20;
            else if (part.Length == 1 && char.IsAsciiLetterOrDigit(part[0])) key = char.ToUpperInvariant(part[0]);
            else throw new ArgumentException("The Quick Panel hotkey contains an unsupported key.", nameof(gesture));
        }
        if (modifiers == 0 || key == 0)
        {
            throw new ArgumentException("The Quick Panel hotkey requires a modifier and a key.", nameof(gesture));
        }
        return new HotkeyDefinition(modifiers, key);
    }

    internal readonly record struct HotkeyDefinition(uint Modifiers, uint VirtualKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public nint Window;
        public uint Message;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out NativeMessage message, nint window, uint minimum, uint maximum);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
