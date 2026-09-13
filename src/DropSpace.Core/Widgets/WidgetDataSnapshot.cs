namespace DropSpace.Core.Widgets;

public sealed record WidgetDataSnapshot(DateTimeOffset LocalTime, double? CpuPercent, double? MemoryPercent,
    int? BatteryPercent = null, bool? OnAcPower = null, TimeSpan Uptime = default);
