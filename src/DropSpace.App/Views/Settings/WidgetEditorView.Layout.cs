using DropSpace.Core.Widgets;

namespace DropSpace.App.Views.Settings;

public sealed partial class WidgetEditorView
{
    private long BeginLayoutIntent()
    {
        // Retire only earlier local feedback. Every requested settings transaction
        // still runs, including edits queued before another layout action.
        var revision = ++_layoutFeedbackRevision;
        SetError(null);
        return revision;
    }
    private bool PublishLayoutFeedback(long revision, NativeWidgetId id, bool rejected)
    {
        if (revision != _layoutFeedbackRevision || _selectedId != id) return false;
        SetError(rejected ? _strings.Get("WidgetNoRoom") : null);
        return true;
    }
    private async Task MoveCoordinateAsync(bool column, double value)
    {
        if (_syncingPosition || _selectedId is not { } id) return;
        if (!double.IsFinite(value) || value != Math.Truncate(value) || value < 1 ||
            value > (column ? WidgetLayoutPolicy.Columns : WidgetLayoutPolicy.Rows))
        {
            RefreshPositionInputs(_editor.Settings.Widgets.Layout.Expanded.FirstOrDefault(item => item.Id == _selectedId));
            return;
        }
        var revision = BeginLayoutIntent();
        var coordinate = (int)value - 1;
        var rejected = false;
        ++_positionEdits;
        try
        {
            var saved = await UpdateLayoutAsync(layout =>
            {
                // Resolve the untouched coordinate inside the serialized transaction,
                // so entering Column then Row cannot undo the preceding column edit.
                var current = layout.Expanded.FirstOrDefault(item => item.Id == id);
                if (current is null) return layout;
                var desired = column ? current with { Column = coordinate } : current with { Row = coordinate };
                if (desired == current) return layout;
                if (!WidgetLayoutPolicy.TryMoveOrResize(layout, desired, out var updated))
                { rejected = true; return layout; }
                return updated;
            });
            if (saved) PublishLayoutFeedback(revision, id, rejected);
        }
        finally
        {
            --_positionEdits;
            RefreshPositionInputs(_editor.Settings.Widgets.Layout.Expanded.FirstOrDefault(item => item.Id == _selectedId));
        }
    }
    private async Task ResizeAsync(WidgetPlacement current, WidgetSize size)
    {
        var id = current.Id;
        var revision = BeginLayoutIntent();
        var rejected = false;
        _selectedId = id;
        var saved = await UpdateLayoutAsync(layout =>
        {
            // Size input can follow a still-saving coordinate edit. Choose a slot
            // from the actual transaction layout, preserving that committed move.
            var latest = layout.Expanded.FirstOrDefault(item => item.Id == id);
            if (latest is null) return layout;
            var positions = Enumerable.Range(0, WidgetLayoutPolicy.Rows).SelectMany(row =>
                Enumerable.Range(0, WidgetLayoutPolicy.Columns).Select(column => (Column: column, Row: row)))
                .OrderBy(p => Math.Abs(p.Column-latest.Column)+Math.Abs(p.Row-latest.Row));
            foreach (var position in positions)
            {
                var desired = latest with { Column = position.Column, Row = position.Row, ColumnSpan = size.Columns, RowSpan = size.Rows };
                if (WidgetLayoutPolicy.TryMoveOrResize(layout, desired, out var updated)) return updated;
            }
            rejected = true;
            return layout;
        });
        if (saved) PublishLayoutFeedback(revision, id, rejected);
    }
    private async Task<bool> PlaceAsync(NativeWidgetId id, int column, int row, int? width = null, int? height = null)
    {
        var revision = BeginLayoutIntent();
        _selectedId = id;
        var current = _editor.Settings.Widgets.Layout;
        if (!current.Expanded.Any(item => item.Id == id) && !WidgetLayoutPolicy.TryAdd(current, id, column, row, out _))
        { SetError(_strings.Get("WidgetNoRoom")); return false; }
        var existing = current.Expanded.FirstOrDefault(item => item.Id == id);
        if (existing is not null)
        {
            var desired = existing with { Column = column, Row = row, ColumnSpan = width ?? existing.ColumnSpan, RowSpan = height ?? existing.RowSpan };
            if (desired == existing) return true;
            if (!WidgetLayoutPolicy.TryMoveOrResize(current, desired, out _))
            { SetError(_strings.Get("WidgetNoRoom")); return false; }
        }
        var rejected = false;
        var saved = await UpdateLayoutAsync(layout =>
        {
            var old = layout.Expanded.FirstOrDefault(item => item.Id == id);
            if (old is null)
            {
                if (!WidgetLayoutPolicy.TryAdd(layout, id, column, row, out var added)) throw new InvalidOperationException("No vacant widget slot remains.");
                return added;
            }
            var moved = old with { Column = column, Row = row, ColumnSpan = width ?? old.ColumnSpan, RowSpan = height ?? old.RowSpan };
            if (!WidgetLayoutPolicy.TryMoveOrResize(layout, moved, out var updated))
            {
                rejected = true;
                return layout;
            }
            return updated;
        });
        if (saved && PublishLayoutFeedback(revision, id, rejected) && rejected) RefreshSelection();
        return saved;
    }
    private async Task<bool> UpdateLayoutAsync(Func<WidgetLayout, WidgetLayout> change)
    {
        WidgetLayout? previous = null;
        ++_layoutEdits; RefreshUndoEnabled();
        try
        {
            var saved = await _editor.UpdateAsync(settings =>
            {
                var layout = settings.Widgets.Layout;
                var updated = change(layout);
                if (layout.Equals(updated)) return settings;
                previous = layout;
                return settings with { Widgets = settings.Widgets with { Layout = updated } };
            });
            if (saved && previous is not null) _undoLayout = previous;
            return saved;
        }
        finally { --_layoutEdits; RefreshUndoEnabled(); }
    }
    private void RefreshUndoEnabled() => _undo.IsEnabled = _undoLayout is not null && _layoutEdits == 0;
    private async Task RemoveAsync(NativeWidgetId id)
    {
        if (!_editor.Settings.Widgets.Layout.Expanded.Any(item => item.Id == id)) return;
        BeginLayoutIntent();
        var saved = await UpdateLayoutAsync(layout => new(layout.Expanded.Where(item => item.Id != id).ToArray(), layout.Compact));
        if (saved && _selectedId == id) { _selectedId = null; RefreshSelection(); }
    }
}
