namespace DropSpace.Core.Widgets;

public sealed record WidgetSize(int Columns, int Rows);

public static class WidgetCatalog
{
    public static IReadOnlyList<WidgetSize> Sizes(NativeWidgetId id) => id switch
    {
        NativeWidgetId.Clock => [new(1, 1), new(2, 1), new(2, 2)],
        NativeWidgetId.Calendar => [new(1, 1), new(2, 2)],
        NativeWidgetId.ResourceUsage or NativeWidgetId.Stopwatch => [new(2, 1), new(2, 2)],
        NativeWidgetId.Battery or NativeWidgetId.ClipboardPause => [new(1, 1), new(2, 1)],
        NativeWidgetId.Uptime => [new(2, 1)],
        _ => [new(1, 1)],
    };
    public static WidgetSize NormalizeSize(NativeWidgetId id, int columns, int rows) => Sizes(id)
        .MinBy(size => Math.Abs((long)size.Columns - columns) + Math.Abs((long)size.Rows - rows))!;
    public static WidgetPlacement DefaultPlacement(NativeWidgetId id)
    {
        var existing = WidgetLayout.Default.Expanded.FirstOrDefault(item => item.Id == id);
        if (existing is not null) return existing;
        var size = Sizes(id)[0];
        return new(id, 0, 0, size.Columns, size.Rows);
    }
}
