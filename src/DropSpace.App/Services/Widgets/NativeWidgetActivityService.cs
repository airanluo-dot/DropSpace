using System.Runtime.InteropServices;
using DropSpace.Core.Island;
using DropSpace.Core.Models;
using DropSpace.Core.Widgets;
using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services.Widgets;

public sealed class NativeWidgetActivityService : IAsyncDisposable
{
    private const string SourceId = "widgets";
    private readonly IIslandActivityRouter _router;
    private readonly ILogger<NativeWidgetActivityService> _logger;
    private readonly object _gate = new();
    private Timer? _timer;
    private WidgetSettings _settings = new();
    private bool _enabled;
    private bool _disposed;
    private Guid _activityId;
    private ulong _lastIdle;
    private ulong _lastKernel;
    private ulong _lastUser;

    public NativeWidgetActivityService(IIslandActivityRouter router, ILogger<NativeWidgetActivityService> logger)
    {
        _router = router;
        _logger = logger;
    }

    public Task ApplySettingsAsync(WidgetSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _settings = settings;
            _enabled = settings.Enabled;
            _timer?.Dispose();
            _timer = _enabled ? new Timer(_ => Publish(), null, TimeSpan.Zero, TimeSpan.FromSeconds(1)) : null;
            if (!_enabled) RemoveActivity();
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            lock (_gate)
            {
                _timer?.Dispose();
                _timer = null;
                RemoveActivity();
            }
        }

        return ValueTask.CompletedTask;
    }

    private void Publish()
    {
        if (!_enabled) return;
        try
        {
            var now = DateTimeOffset.Now;
            var cpu = ReadCpuUsage();
            var memory = ReadMemoryUsage();
            var parts = new List<string>();
            if (_settings.CompactTimeEnabled || _settings.ClockEnabled) parts.Add(now.ToString("HH:mm"));
            if (_settings.CompactResourceUsageEnabled || _settings.ResourceUsageEnabled) parts.Add($"CPU {cpu:0}% · RAM {memory:0}%");
            if (_settings.CalendarEnabled) parts.Add(now.ToString("MMM d"));
            if (_settings.Layout.Expanded.Any(widget => widget.Id == NativeWidgetId.Settings)) parts.Add("Settings");
            if (parts.Count == 0)
            {
                RemoveActivity();
                return;
            }

            var title = _settings.CompactTimeEnabled || _settings.ClockEnabled ? now.ToString("HH:mm") : "DropSpace";
            var subtitle = string.Join("  ", parts.Skip(1));
            _activityId = _router.Publish(new IslandActivity(
                _activityId,
                IslandActivityKind.WidgetIdle,
                IslandActivityPriority.WidgetIdle,
                IslandActivityPresentation.Both,
                SourceId,
                title,
                subtitle,
                "Native Widgets",
                $"{now:dddd, MMMM d} · CPU {cpu:0}% · RAM {memory:0}%",
                new(DateTimeOffset.UtcNow),
                true));
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException)
        {
            _logger.LogDebug(exception, "Native widget sampling failed safely.");
        }
    }

    private void RemoveActivity()
    {
        if (_activityId != Guid.Empty)
        {
            _router.Remove(_activityId);
            _activityId = Guid.Empty;
        }
        _router.RemoveSource(SourceId);
    }

    private double ReadCpuUsage()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        var idleValue = ToUInt64(idle);
        var kernelValue = ToUInt64(kernel);
        var userValue = ToUInt64(user);
        if (_lastKernel == 0)
        {
            _lastIdle = idleValue;
            _lastKernel = kernelValue;
            _lastUser = userValue;
            return 0;
        }

        var idleDelta = idleValue - _lastIdle;
        var totalDelta = (kernelValue - _lastKernel) + (userValue - _lastUser);
        _lastIdle = idleValue;
        _lastKernel = kernelValue;
        _lastUser = userValue;
        return totalDelta == 0 ? 0 : Math.Clamp((1 - idleDelta / (double)totalDelta) * 100, 0, 100);
    }

    private static double ReadMemoryUsage()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref status) ? status.dwMemoryLoad : 0;
    }

    private static ulong ToUInt64(FILETIME value) => ((ulong)value.dwHighDateTime << 32) | value.dwLowDateTime;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}
