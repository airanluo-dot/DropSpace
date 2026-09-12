using System.Runtime.InteropServices;
using DropSpace.Core.Widgets;
using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services.Widgets;

/// <summary>Visible-widget data only. This service cannot request island presence.</summary>
public sealed class NativeWidgetDataService(ILogger<NativeWidgetDataService> logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private CancellationTokenSource? _stop;
    private Task _worker = Task.CompletedTask;
    private bool _disposed;
    public WidgetDataSnapshot? Current { get; private set; }
    public event EventHandler<WidgetDataSnapshot>? Changed;

    public async Task SetVisibleAsync(bool visible, CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (visible && _stop is not null && !_worker.IsCompleted) return;
            await StopCoreAsync().ConfigureAwait(false);
            if (!visible) return;
            _stop = new();
            var token = _stop.Token;
            _worker = Task.Run(() => SampleAsync(token), CancellationToken.None);
        }
        finally { _lifecycle.Release(); }
    }

    private async Task SampleAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        (ulong Idle, ulong Kernel, ulong User)? previous = null;
        try
        {
            do
            {
                token.ThrowIfCancellationRequested();
                double? cpu = null;
                if (GetSystemTimes(out var idle, out var kernel, out var user))
                {
                    var current = (Idle: idle, Kernel: kernel, User: user);
                    if (previous is { } old && kernel >= old.Kernel && user >= old.User && idle >= old.Idle)
                    {
                        var total = kernel - old.Kernel + user - old.User;
                        if (total > 0) cpu = Math.Clamp(100 * (1 - (idle - old.Idle) / (double)total), 0, 100);
                    }
                    previous = current;
                }
                else previous = null;
                var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
                double? memoryPercent = GlobalMemoryStatusEx(ref memory) ? memory.Load : null;
                var snapshot = new WidgetDataSnapshot(DateTimeOffset.Now, cpu, memoryPercent);
                Current = snapshot;
                if (Changed is { } handlers)
                    foreach (EventHandler<WidgetDataSnapshot> handler in handlers.GetInvocationList())
                        try { handler(this, snapshot); }
                        catch (Exception exception) { logger.LogDebug("Widget subscriber failed ({Category}).", exception.GetType().Name); }
            } while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task StopCoreAsync()
    {
        _stop?.Cancel(); await _worker.ConfigureAwait(false);
        _stop?.Dispose(); _stop = null; Current = null;
    }
    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { if (_disposed) return; _disposed = true; await StopCoreAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
}
