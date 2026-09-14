using System.ComponentModel;
using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using DropSpace.Core.Widgets;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;

namespace DropSpace.App.Views.Settings;

public sealed class WidgetEditorView : UserControl
{
    private const double DragThresholdDips = 6;
    private readonly NativeSettingsEditor _editor;
    private readonly IAppStringLocalizer _strings;
    private readonly Grid _grid = new() { Height = 270, ColumnSpacing = 6, RowSpacing = 6 };
    private readonly StackPanel _library = new() { Spacing = 8 };
    private readonly StackPanel _expanded = new() { Spacing = 12 };
    private readonly NumberBox _column, _row;
    private readonly ComboBox _size;
    private readonly Button _remove;
    private bool _syncingSelection;
    private readonly TextBlock _selected = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap };
    private NativeWidgetId? _selectedId;
    private readonly Dictionary<NativeWidgetId, Button> _placedButtons = [];
    public WidgetEditorView(NativeSettingsEditor editor, IAppStringLocalizer strings)
    {
        _editor = editor; _strings = strings;
        var body = new StackPanel { Spacing = 14 }; Content = body;
        var enabled = new SettingsForm(editor, strings);
        enabled.AddToggle("WidgetsEnabled", s => s.Widgets.Enabled, (s,v) => s with { Widgets = s.Widgets with { Enabled = v } }); body.Children.Add(enabled);
        body.Children.Add(new TextBlock { Text = strings.Get("WidgetsExpandedMode"), FontSize = 18 });
        body.Children.Add(_expanded); _expanded.Children.Add(new TextBlock { Text = strings.Get("WidgetsEditorHelp"), TextWrapping = TextWrapping.Wrap });
        for (var i = 0; i < WidgetLayoutPolicy.Columns; i++) _grid.ColumnDefinitions.Add(new());
        for (var i = 0; i < WidgetLayoutPolicy.Rows; i++) _grid.RowDefinitions.Add(new());
        _expanded.Children.Add(_grid); _expanded.Children.Add(_selected);
        var numbers = new Grid { ColumnSpacing = 8 };
        for (var i = 0; i < 3; i++) numbers.ColumnDefinitions.Add(new());
        _column = Number("WidgetColumn", 1, WidgetLayoutPolicy.Columns); _row = Number("WidgetRow", 1, WidgetLayoutPolicy.Rows);
        _size = new ComboBox { Header = strings.Get("WidgetSize"), HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(_size, strings.Get("WidgetSize"));
        var controls = new FrameworkElement[] { _column, _row, _size };
        for (var i = 0; i < controls.Length; i++) { Grid.SetColumn(controls[i], i); numbers.Children.Add(controls[i]); }
        _expanded.Children.Add(numbers);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _remove = new Button { Content = strings.Get("WidgetRemove") }; _remove.Click += OnRemove;
        actions.Children.Add(_remove); _expanded.Children.Add(actions);
        _column.ValueChanged += async (_, _) => await ApplyPositionAsync();
        _row.ValueChanged += async (_, _) => await ApplyPositionAsync();
        _size.SelectionChanged += async (_, _) => await ApplyPositionAsync();
        _expanded.Children.Add(new TextBlock { Text = strings.Get("WidgetLibrary"), FontSize = 18 }); _expanded.Children.Add(_library);
        var reset = new Button { Content = strings.Get("WidgetsReset") }; reset.Click += async (_, _) => await editor.UpdateAsync(s => s with { Widgets = s.Widgets with { Layout = WidgetLayout.Default } }); body.Children.Add(reset); body.Children.Add(_error);
        Loaded += (_, _) => { editor.PropertyChanged += OnChanged; Rebuild(); }; Unloaded += (_, _) => editor.PropertyChanged -= OnChanged;
        Rebuild();
    }
    private NumberBox Number(string key, double min, double max) => new() { Header = _strings.Get(key), Minimum = min, Maximum = max, Value = min, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private void OnChanged(object? sender, PropertyChangedEventArgs args) { if (args.PropertyName == nameof(NativeSettingsEditor.Settings)) Rebuild(); }
    private void Rebuild()
    {
        _grid.Children.Clear(); _library.Children.Clear(); _placedButtons.Clear();
        for (var row = 0; row < WidgetLayoutPolicy.Rows; row++)
        for (var column = 0; column < WidgetLayoutPolicy.Columns; column++)
        {
            var cell = new Border { BorderThickness = new(1), BorderBrush = Brush("CardStrokeColorDefaultBrush"), Background = Brush("ControlFillColorSecondaryBrush"), CornerRadius = new(8) };
            Grid.SetColumn(cell, column); Grid.SetRow(cell, row); _grid.Children.Add(cell);
        }
        foreach (var placement in _editor.Settings.Widgets.Layout.Expanded)
        {
            var button = CreateDragButton(placement.Id, WidgetName(placement.Id));
            button.HorizontalAlignment = HorizontalAlignment.Stretch; button.VerticalAlignment = VerticalAlignment.Stretch;
            button.Click += (_, _) => { _selectedId = placement.Id; RefreshSelection(); };
            _placedButtons[placement.Id] = button;
            button.Background = Brush("ControlFillColorDefaultBrush");
            Grid.SetColumn(button, placement.Column); Grid.SetRow(button, placement.Row); Grid.SetColumnSpan(button, placement.ColumnSpan); Grid.SetRowSpan(button, placement.RowSpan); _grid.Children.Add(button);
        }
        foreach (var id in Enum.GetValues<NativeWidgetId>().Where(id => !_editor.Settings.Widgets.Layout.Expanded.Any(item => item.Id == id)))
        {
            var size = WidgetCatalog.DefaultPlacement(id);
            var button = CreateDragButton(id, $"{WidgetName(id)}  ·  {size.ColumnSpan} × {size.RowSpan}  ·  {_strings.Get("WidgetAdd")}");
            button.Click += async (_, _) => await PlaceAsync(id, 0, 0); _library.Children.Add(button);
        }
        RefreshSelection();
    }
    private Button CreateDragButton(NativeWidgetId id, string label)
    {
        var content = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(new FontIcon { Glyph = id switch { NativeWidgetId.Clock => "\uE823", NativeWidgetId.Calendar => "\uE787", NativeWidgetId.ResourceUsage => "\uE9D9", NativeWidgetId.Battery => "\uE850", NativeWidgetId.Uptime => "\uE7E8", NativeWidgetId.Stopwatch => "\uE916", NativeWidgetId.ClipboardPause => "\uE77F", _ => "\uE713" }, FontSize = 18 });
        content.Children.Add(new TextBlock { Text = label, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
        var dragTransform = new TranslateTransform();
        var button = new Button { Content = content, Padding = new Thickness(4), ManipulationMode = ManipulationModes.None, RenderTransform = dragTransform };
        ToolTipService.SetToolTip(button, label);
        AutomationProperties.SetName(button, label);
        Windows.Foundation.Point? pressedAt = null;
        var dragging = false;
        button.AddHandler(PointerPressedEvent, new PointerEventHandler((_, args) =>
        {
            var point = args.GetCurrentPoint(_grid);
            if (point.Properties.IsLeftButtonPressed)
            {
                pressedAt = point.Position;
                button.CapturePointer(args.Pointer);
            }
        }), true);
        button.AddHandler(PointerReleasedEvent, new PointerEventHandler(async (_, args) =>
        {
            var shouldPlace = dragging;
            pressedAt = null; dragging = false; button.Opacity = 1; dragTransform.X = dragTransform.Y = 0;
            button.ReleasePointerCapture(args.Pointer);
            if (!shouldPlace) return;
            args.Handled = true;
            var target = args.GetCurrentPoint(_grid).Position;
            if (target.X < 0 || target.Y < 0 || target.X >= _grid.ActualWidth || target.Y >= _grid.ActualHeight) return;
            var column = (int)(target.X / ((_grid.ActualWidth + _grid.ColumnSpacing) / WidgetLayoutPolicy.Columns));
            var row = (int)(target.Y / ((_grid.ActualHeight + _grid.RowSpacing) / WidgetLayoutPolicy.Rows));
            try { await PlaceAsync(id, column, row); }
            catch (Exception) { _error.Text = _strings.Get("WidgetDropFailed"); }
        }), true);
        button.AddHandler(PointerCanceledEvent, new PointerEventHandler((_, _) => { pressedAt = null; dragging = false; button.Opacity = 1; dragTransform.X = dragTransform.Y = 0; }), true);
        button.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, args) =>
        {
            // Button releases capture before the routed PointerReleased handler runs.
            // Preserve the pending drop for that normal release; only cancel an interrupted drag.
            if (!args.GetCurrentPoint(_grid).Properties.IsLeftButtonPressed) return;
            pressedAt = null; dragging = false; button.Opacity = 1; dragTransform.X = dragTransform.Y = 0;
        }), true);
        button.AddHandler(PointerMovedEvent, new PointerEventHandler((_, args) =>
        {
            var point = args.GetCurrentPoint(_grid);
            if (pressedAt is not { } origin || !point.Properties.IsLeftButtonPressed) return;
            if (Math.Abs(point.Position.X - origin.X) < DragThresholdDips && Math.Abs(point.Position.Y - origin.Y) < DragThresholdDips) return;
            if (!dragging) { dragging = true; _selectedId = id; RefreshSelection(); button.Opacity = 0.65; }
            dragTransform.X = point.Position.X - origin.X;
            dragTransform.Y = point.Position.Y - origin.Y;
        }), true);
        return button;
    }
    private void RefreshSelection()
    {
        _syncingSelection = true;
        try
        {
        foreach (var (id, button) in _placedButtons)
        {
            var selected = id == _selectedId;
            button.BorderThickness = new Thickness(selected ? 3 : 1);
            button.BorderBrush = Brush(selected ? "AccentFillColorDefaultBrush" : "ControlStrokeColorDefaultBrush");
            AutomationProperties.SetItemStatus(button, selected ? _strings.Get("WidgetSelected") : string.Empty);
            if (button.Content is StackPanel content && content.Children.LastOrDefault() is TextBlock label)
                label.FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }
        var placement = _editor.Settings.Widgets.Layout.Expanded.FirstOrDefault(item => item.Id == _selectedId);
        _column.IsEnabled = _row.IsEnabled = _size.IsEnabled = _remove.IsEnabled = placement is not null;
        _selected.Text = placement is null ? _strings.Get("WidgetSelect") : WidgetName(placement.Id);
        if (placement is null) return;
        _column.Value = placement.Column + 1; _row.Value = placement.Row + 1;
        _size.Items.Clear();
        foreach (var size in WidgetCatalog.Sizes(placement.Id)) _size.Items.Add(new ComboBoxItem { Content = $"{size.Columns} × {size.Rows}", Tag = size });
        _size.SelectedItem = _size.Items.OfType<ComboBoxItem>().First(item => item.Tag is WidgetSize size && size.Columns == placement.ColumnSpan && size.Rows == placement.RowSpan);
        _size.IsEnabled = _size.Items.Count > 1;
        }
        finally { _syncingSelection = false; }
    }
    private Task<bool> PlaceAsync(NativeWidgetId id, int column, int row, int? width = null, int? height = null)
    {
        _error.Text = string.Empty; _selectedId = id;
        var current = _editor.Settings.Widgets.Layout;
        if (!current.Expanded.Any(item => item.Id == id) && !WidgetLayoutPolicy.TryAdd(current, id, column, row, out _))
        { _error.Text = _strings.Get("WidgetNoRoom"); return Task.FromResult(false); }
        return _editor.UpdateAsync(settings =>
        {
            var layout = settings.Widgets.Layout;
            var old = layout.Expanded.FirstOrDefault(item => item.Id == id);
            if (old is null)
            {
                if (!WidgetLayoutPolicy.TryAdd(layout, id, column, row, out var added)) throw new InvalidOperationException("No vacant widget slot remains.");
                return settings with { Widgets = settings.Widgets with { Layout = added } };
            }
            var moved = old with { Column = column, Row = row, ColumnSpan = width ?? old.ColumnSpan, RowSpan = height ?? old.RowSpan };
            var normalized = WidgetLayoutPolicy.Normalize(new(new[] { moved }.Concat(layout.Expanded.Where(item => item.Id != id)).ToArray(), layout.Compact));
            if (normalized.Expanded.Count != layout.Expanded.Count + (layout.Expanded.Any(item => item.Id == id) ? 0 : 1)) throw new InvalidOperationException("The requested widget layout has no room for every existing widget.");
            return settings with { Widgets = settings.Widgets with { Layout = normalized } };
        });
    }
    private async Task ApplyPositionAsync()
    {
        if (!_syncingSelection && _selectedId is { } id && double.IsFinite(_column.Value) && double.IsFinite(_row.Value) && _size.SelectedItem is ComboBoxItem { Tag: WidgetSize size })
            await PlaceAsync(id, (int)_column.Value - 1, (int)_row.Value - 1, size.Columns, size.Rows);
    }
    private async void OnRemove(object sender, RoutedEventArgs args)
    {
        if (_selectedId is { } id) await _editor.UpdateAsync(s => s with { Widgets = s.Widgets with { Layout = new(s.Widgets.Layout.Expanded.Where(item => item.Id != id).ToArray(), s.Widgets.Layout.Compact) } });
    }
    private string WidgetName(NativeWidgetId id) => _strings.Get(id == NativeWidgetId.Settings ? "WidgetSettingsName" : "Widget" + id + ".Text");
    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];
}
