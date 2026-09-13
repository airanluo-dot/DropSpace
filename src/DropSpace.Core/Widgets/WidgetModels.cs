namespace DropSpace.Core.Widgets;

public enum NativeWidgetId
{
    Clock,
    Calendar,
    ResourceUsage,
    Settings,
    Battery,
    Uptime,
    Stopwatch,
    ClipboardPause,
}

public sealed record WidgetPlacement(NativeWidgetId Id, int Column, int Row, int ColumnSpan, int RowSpan);

public sealed record CompactWidgetLayout(
    NativeWidgetId? Left,
    NativeWidgetId? Center,
    NativeWidgetId? Right);

public sealed class WidgetLayout : IEquatable<WidgetLayout>
{
    public WidgetLayout(IReadOnlyList<WidgetPlacement> expanded, CompactWidgetLayout compact)
    {
        Expanded = expanded?.ToArray() ?? [];
        Compact = compact;
    }

    public IReadOnlyList<WidgetPlacement> Expanded { get; }

    public CompactWidgetLayout Compact { get; }

    public static WidgetLayout Default { get; } = new(
        [
            new(NativeWidgetId.Clock, 0, 0, 2, 1),
            new(NativeWidgetId.Calendar, 2, 0, 2, 2),
            new(NativeWidgetId.ResourceUsage, 4, 0, 2, 1),
            new(NativeWidgetId.Settings, 0, 1, 1, 1),
            new(NativeWidgetId.Battery, 6, 0, 2, 1),
            new(NativeWidgetId.Uptime, 4, 1, 2, 1),
            new(NativeWidgetId.Stopwatch, 0, 2, 2, 1),
            new(NativeWidgetId.ClipboardPause, 7, 1, 1, 1),
        ],
        new(NativeWidgetId.Clock, null, NativeWidgetId.ResourceUsage));

    public bool Equals(WidgetLayout? other)
    {
        return other is not null
            && Expanded.SequenceEqual(other.Expanded)
            && EqualityComparer<CompactWidgetLayout>.Default.Equals(Compact, other.Compact);
    }

    public override bool Equals(object? obj) => Equals(obj as WidgetLayout);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var placement in Expanded) hash.Add(placement);
        hash.Add(Compact);
        return hash.ToHashCode();
    }
}

public static class WidgetLayoutPolicy
{
    public const int Columns = 8;
    public const int Rows = 4;

    public static bool TryAdd(WidgetLayout layout, NativeWidgetId id, int preferredColumn, int preferredRow, out WidgetLayout updated)
    {
        updated = layout;
        if (!Enum.IsDefined(id) || layout.Expanded.Any(item => item.Id == id)) return false;
        var occupied = new bool[Columns, Rows];
        foreach (var item in layout.Expanded)
        {
            if (item.Column < 0 || item.Row < 0 || item.ColumnSpan <= 0 || item.RowSpan <= 0 ||
                (long)item.Column + item.ColumnSpan > Columns || (long)item.Row + item.RowSpan > Rows) return false;
            Mark(occupied, item.Column, item.Row, item.ColumnSpan, item.RowSpan, true);
        }
        var added = WidgetCatalog.DefaultPlacement(id);
        if (!TryFindSlot(occupied, preferredColumn, preferredRow, added.ColumnSpan, added.RowSpan, out var column, out var row)) return false;
        updated = new(layout.Expanded.Append(added with { Column = column, Row = row }).ToArray(), layout.Compact);
        return true;
    }

    public static WidgetLayout Normalize(WidgetLayout? layout)
    {
        layout ??= WidgetLayout.Default;
        var occupied = new bool[Columns, Rows];
        var placements = new List<WidgetPlacement>();
        foreach (var placement in layout.Expanded)
        {
            if (placement is null || !Enum.IsDefined(placement.Id)) continue;
            if (placements.Any(item => item.Id == placement.Id)) continue;
            var size = WidgetCatalog.NormalizeSize(placement.Id, placement.ColumnSpan, placement.RowSpan);
            var width = size.Columns;
            var height = size.Rows;
            if (!TryFindSlot(occupied, placement.Column, placement.Row, width, height, out var column, out var row)) continue;
            Mark(occupied, column, row, width, height, true);
            placements.Add(placement with { Column = column, Row = row, ColumnSpan = width, RowSpan = height });
        }

        return new WidgetLayout(
            placements,
            NormalizeCompact(layout.Compact));
    }

    private static CompactWidgetLayout NormalizeCompact(CompactWidgetLayout? layout)
    {
        layout ??= new(null, null, null);
        var values = new NativeWidgetId?[] { layout.Left, layout.Center, layout.Right };
        for (var index = 0; index < values.Length; index++)
        {
            if (values[index] is null) continue;
            if (values[index] is not (NativeWidgetId.Clock or NativeWidgetId.ResourceUsage))
            {
                values[index] = null;
                continue;
            }
            for (var previous = 0; previous < index; previous++)
            {
                if (values[previous] == values[index]) values[index] = null;
            }
        }

        return new CompactWidgetLayout(values[0], values[1], values[2]);
    }

    private static bool TryFindSlot(bool[,] occupied, int column, int row, int width, int height, out int foundColumn, out int foundRow)
    {
        var preferred = Math.Clamp(row, 0, Rows - 1) * Columns + Math.Clamp(column, 0, Columns - 1);
        for (var offset = 0; offset < Columns * Rows; offset++)
        {
            var slot = (preferred + offset) % (Columns * Rows);
            var candidateRow = slot / Columns;
            var candidateColumn = slot % Columns;
            if (candidateColumn + width > Columns || candidateRow + height > Rows) continue;
            var free = true;
            for (var y = candidateRow; y < candidateRow + height; y++)
            for (var x = candidateColumn; x < candidateColumn + width; x++)
                free &= !occupied[x, y];
            if (free)
            {
                foundColumn = candidateColumn;
                foundRow = candidateRow;
                return true;
            }
        }

        foundColumn = 0;
        foundRow = 0;
        return false;
    }

    private static void Mark(bool[,] occupied, int column, int row, int width, int height, bool value)
    {
        for (var y = row; y < row + height; y++)
        for (var x = column; x < column + width; x++) occupied[x, y] = value;
    }
}
