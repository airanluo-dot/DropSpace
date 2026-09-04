using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services;

/// <summary>Owns a process-lifetime RegisterHotKey message thread for the Dynamic Island Quick Panel.</summary>
public sealed class GlobalQuickPanelHotkeyService : IDisposable, IAsyncDisposable
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
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private Thread? _thread;
    private uint _threadId;
    private HotkeyDefinition _definition;
    private string? _gesture;
    private TaskCompletionSource<bool>? _readySignal;
    private TaskCompletionSource<object?>? _exitSignal;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private int _isRegistered;
    private bool _disposed;

    public GlobalQuickPanelHotkeyService(ILogger<GlobalQuickPanelHotkeyService> logger) =>
        _logger = logger;

    public event EventHandler? Invoked;

    public bool IsRegistered => Volatile.Read(ref _isRegistered) != 0;

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

    public async Task<bool> TryStartAsync(
        string gesture,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ = Parse(gesture);
        if (IsRegistered && string.Equals(_gesture, gesture, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsRegistered && string.Equals(_gesture, gesture, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!CanRegister(gesture))
            {
                return false;
            }

            var previous = _gesture;
            if (!await StopCoreAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            StartCore(gesture);
            if (await WaitUntilReadyAsync(cancellationToken).ConfigureAwait(false))
            {
                _gesture = gesture;
                return true;
            }

            if (!string.IsNullOrWhiteSpace(previous) &&
                await StopCoreAsync(cancellationToken).ConfigureAwait(false))
            {
                StartCore(previous);
                if (await WaitUntilReadyAsync(cancellationToken).ConfigureAwait(false))
                {
                    _gesture = previous;
                }
            }

            return false;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StartAsync(string gesture, CancellationToken cancellationToken = default)
    {
        if (!await TryStartAsync(gesture, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning("Quick Panel hotkey could not be registered; the Dynamic Island remains available by pointer.");
        }
    }

    private void StartCore(string gesture)
    {
        _definition = Parse(gesture);
        _readySignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _exitSignal = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(MessageThreadMain)
        {
            IsBackground = true,
            Name = "DropSpace Quick Panel hotkey",
        };
        _thread.Start();
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public void Dispose()
    {
        _ = DisposeAsync().AsTask();
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            if (_disposeTask is null)
            {
                _disposed = true;
                _disposeTask = DisposeCoreAsync();
            }

            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _lifecycleGate.Dispose();
    }

    private void MessageThreadMain()
    {
        Volatile.Write(ref _threadId, GetCurrentThreadId());
        try
        {
            _ = PeekMessage(out _, nint.Zero, 0, 0, 0);
            if (!RegisterHotKey(
                nint.Zero,
                HotkeyId,
                _definition.Modifiers | ModifierNoRepeat,
                _definition.VirtualKey))
            {
                _readySignal?.TrySetResult(false);
                _logger.LogWarning(
                    "Quick Panel hotkey registration failed with Win32 error {Error}; the Dynamic Island remains available by pointer.",
                    Marshal.GetLastWin32Error());
                return;
            }
            Volatile.Write(ref _isRegistered, 1);
            _readySignal?.TrySetResult(true);
            _logger.LogInformation("Quick Panel global hotkey registered without logging the configured key sequence.");
            while (GetMessage(out var message, nint.Zero, 0, 0) > 0)
            {
                if (message.Message == WindowMessageHotkey && message.WParam.ToInt32() == HotkeyId)
                {
                    NativeSubscriberNotification.Invoke(Invoked, this, _logger);
                }
            }
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Quick Panel hotkey observer stopped unexpectedly.");
        }
        finally
        {
            _ = UnregisterHotKey(nint.Zero, HotkeyId);
            Volatile.Write(ref _isRegistered, 0);
            _readySignal?.TrySetResult(false);
            _exitSignal?.TrySetResult(null);
        }
    }

    private async Task<bool> WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        var readySignal = _readySignal;
        if (readySignal is null)
        {
            return false;
        }

        try
        {
            await readySignal.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Quick Panel hotkey thread did not report readiness within the bounded startup window.");
            return false;
        }

        return IsRegistered;
    }

    private async Task<bool> StopCoreAsync(CancellationToken cancellationToken)
    {
        var thread = _thread;
        var exitSignal = _exitSignal;
        if (thread is null)
        {
            Volatile.Write(ref _threadId, 0);
            Volatile.Write(ref _isRegistered, 0);
            return true;
        }

        if (thread.IsAlive)
        {
            var threadId = Volatile.Read(ref _threadId);
            if (threadId == 0 && _readySignal is { } readySignal)
            {
                try
                {
                    await readySignal.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    _logger.LogWarning("Quick Panel hotkey thread did not expose its message queue within the bounded stop window.");
                }

                threadId = Volatile.Read(ref _threadId);
            }

            if (threadId != 0 && !PostThreadMessage(threadId, WindowMessageQuit, nint.Zero, nint.Zero))
            {
                _logger.LogWarning(
                    "Quick Panel hotkey thread quit message could not be posted; Win32 error {Error}.",
                    Marshal.GetLastWin32Error());
            }
        }

        if (thread.IsAlive && exitSignal is not null)
        {
            try
            {
                await exitSignal.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogError("Quick Panel hotkey thread did not stop within the bounded lifecycle window; native state remains owned by that thread.");
                return false;
            }
        }

        _thread = null;
        Volatile.Write(ref _threadId, 0);
        Volatile.Write(ref _isRegistered, 0);
        return true;
    }

    internal static HotkeyDefinition Parse(string gesture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gesture);
        var parts = gesture.Split('+', StringSplitOptions.TrimEntries);
        uint modifiers = 0;
        uint key = 0;
        foreach (var part in parts)
        {
            uint modifier = part.ToUpperInvariant() switch
            {
                "WIN" => ModifierWindows,
                "SHIFT" => ModifierShift,
                "CTRL" => ModifierControl,
                "ALT" => ModifierAlt,
                _ => 0,
            };
            if (modifier != 0)
            {
                if ((modifiers & modifier) != 0) throw new ArgumentException("Duplicate hotkey modifier.", nameof(gesture));
                modifiers |= modifier;
            }
            else
            {
                if (key != 0) throw new ArgumentException("Multiple hotkey keys.", nameof(gesture));
                if (part.Equals("Space", StringComparison.OrdinalIgnoreCase)) key = 0x20;
                else if (part.Length == 1 && char.IsAsciiLetterOrDigit(part[0])) key = char.ToUpperInvariant(part[0]);
                else throw new ArgumentException("Unsupported hotkey key.", nameof(gesture));
            }
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out NativeMessage message, nint window, uint minimum, uint maximum, uint removeMessage);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
