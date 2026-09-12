namespace DropSpace.Core.Widgets;

public sealed record WidgetDataSnapshot(DateTimeOffset LocalTime, double? CpuPercent, double? MemoryPercent);
