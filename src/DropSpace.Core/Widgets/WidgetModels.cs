namespace DropSpace.Core.Widgets;

public enum NativeWidgetId
{
    Clock,
    Calendar,
    ResourceUsage,
    Settings,
}

public sealed record WidgetPlacement(NativeWidgetId Id, int Column, int Row, int ColumnSpan, int RowSpan);

public sealed record CompactWidgetLayout(
    NativeWidgetId? Left,
    NativeWidgetId? Center,
    NativeWidgetId? Right);

public sealed record WidgetLayout(
    IReadOnlyList<WidgetPlacement> Expanded,
    CompactWidgetLayout Compact)
{
    public static WidgetLayout Default { get; } = new(
        [
            new(NativeWidgetId.Clock, 0, 0, 2, 1),
            new(NativeWidgetId.Calendar, 2, 0, 2, 2),
            new(NativeWidgetId.ResourceUsage, 4, 0, 2, 1),
            new(NativeWidgetId.Settings, 0, 1, 1, 1),
        ],
        new(NativeWidgetId.Clock, null, NativeWidgetId.ResourceUsage));
}

public static class WidgetLayoutPolicy
{
    public const int Columns = 6;
    public const int Rows = 3;

    public static WidgetLayout Normalize(WidgetLayout? layout)
    {
        layout ??= WidgetLayout.Default;
        var occupied = new bool[Columns, Rows];
        var placements = new List<WidgetPlacement>();
        foreach (var placement in layout.Expanded)
        {
            if (placements.Any(item => item.Id == placement.Id)) continue;
            var width = Math.Clamp(placement.ColumnSpan, 1, Columns);
            var height = Math.Clamp(placement.RowSpan, 1, Rows);
            if (!TryFindSlot(occupied, placement.Column, placement.Row, width, height, out var column, out var row)) continue;
            Mark(occupied, column, row, width, height, true);
            placements.Add(placement with { Column = column, Row = row, ColumnSpan = width, RowSpan = height });
        }

        return new WidgetLayout(
            placements,
            NormalizeCompact(layout.Compact));
    }

    private static CompactWidgetLayout NormalizeCompact(CompactWidgetLayout layout)
    {
        var values = new NativeWidgetId?[] { layout.Left, layout.Center, layout.Right };
        for (var index = 0; index < values.Length; index++)
        {
            if (values[index] is null) continue;
            for (var previous = 0; previous < index; previous++)
            {
                if (values[previous] == values[index]) values[index] = null;
            }
        }

        return new CompactWidgetLayout(values[0], values[1], values[2]);
    }

    private static bool TryFindSlot(bool[,] occupied, int column, int row, int width, int height, out int foundColumn, out int foundRow)
    {
        for (var candidateRow = Math.Clamp(row, 0, Rows - 1); candidateRow < Rows; candidateRow++)
        for (var candidateColumn = Math.Clamp(column, 0, Columns - 1); candidateColumn < Columns; candidateColumn++)
        {
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
