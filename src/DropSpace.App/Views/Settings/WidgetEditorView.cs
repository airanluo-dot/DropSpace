using System.ComponentModel;
using DropSpace.App.Services.Widgets;
using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using DropSpace.Core.Widgets;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;

namespace DropSpace.App.Views.Settings;

public sealed partial class WidgetEditorView : UserControl
{
    private const double DragThresholdDips = 6;
    private readonly NativeSettingsEditor _editor;
    private readonly IAppStringLocalizer _strings;
    private readonly Grid _grid = new() { Height = 270, ColumnSpacing = 6, RowSpacing = 6 };
    private readonly Grid _library = new() { ColumnSpacing = 6, RowSpacing = 6, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly StackPanel _sizes = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private long _layoutFeedbackRevision;
    private int _layoutEdits;
    private readonly HashSet<NativeWidgetId> _suppressClicks = [];
    private WidgetLayout? _undoLayout;
    private readonly Button _undo = new();
    private readonly Border _dropPreview = new() { IsHitTestVisible = false, BorderThickness = new(2), CornerRadius = new(8), Visibility = Visibility.Collapsed };
    private readonly StackPanel _expanded = new() { Spacing = 12 };
    private readonly Button _remove;
    private readonly TextBlock _selected = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private bool _errorAnnouncementPending;
    private bool _errorOwnerUnloaded;
    private NativeWidgetId? _selectedId;
    private readonly Dictionary<NativeWidgetId, Button> _placedButtons = [];
    public WidgetEditorView(NativeSettingsEditor editor, IAppStringLocalizer strings)
    {
        _editor = editor; _strings = strings;
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Polite);
        _error.Loaded += (_, _) => AnnounceErrorIfReady();
        var body = new StackPanel { Spacing = 14 }; Content = body;
        var enabled = new SettingsForm(editor, strings);
        enabled.AddToggle("WidgetsEnabled", s => s.Widgets.Enabled, (s,v) => s with { Widgets = s.Widgets with { Enabled = v } }); body.Children.Add(enabled);
        body.Children.Add(new TextBlock { Text = strings.Get("WidgetsExpandedMode"), FontSize = 18 });
        for (var i = 0; i < WidgetLayoutPolicy.Columns; i++) _library.ColumnDefinitions.Add(new());
        _grid.SizeChanged += (_, _) => UpdateGridSizes();
        body.Children.Add(_expanded);
        for (var i = 0; i < WidgetLayoutPolicy.Columns; i++) _grid.ColumnDefinitions.Add(new());
        for (var i = 0; i < WidgetLayoutPolicy.Rows; i++) _grid.RowDefinitions.Add(new());
        _expanded.Children.Add(_error);
        _expanded.Children.Add(_grid); _expanded.Children.Add(_selected);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        _remove = new Button { Content = strings.Get("WidgetRemove") };
        _remove.Click += OnRemove;
        // Drag-back is the primary removal gesture; retain an accessible alternate action.
        actions.Children.Add(_sizes); actions.Children.Add(_remove); _expanded.Children.Add(actions);
        _undo.Content = strings.Get("WidgetUndo"); _undo.IsEnabled = false;
        _undo.Click += async (_, _) =>
        {
            if (_layoutEdits > 0 || _undoLayout is not { } previous) return;
            BeginLayoutIntent();
            ++_layoutEdits; RefreshUndoEnabled();
            try
            {
                if (await editor.UpdateAsync(s => s with { Widgets = s.Widgets with { Layout = previous } }) &&
                    ReferenceEquals(_undoLayout, previous)) _undoLayout = null;
            }
            finally { --_layoutEdits; RefreshUndoEnabled(); }
        };
        actions.Children.Add(_undo);
        _expanded.Children.Add(new TextBlock { Text = strings.Get("WidgetLibrary"), FontSize = 18 }); _expanded.Children.Add(_library);
        var reset = new Button { Content = strings.Get("WidgetsReset") }; reset.Click += async (_, _) =>
        {
            BeginLayoutIntent();
            await UpdateLayoutAsync(_ => WidgetLayout.Default);
        }; body.Children.Add(reset);
        Loaded += (_, _) =>
        {
            _errorOwnerUnloaded = false;
            editor.PropertyChanged += OnChanged; Rebuild();
            AnnounceErrorIfReady();
        };
        Unloaded += (_, _) =>
        {
            _errorOwnerUnloaded = true;
            _errorAnnouncementPending = false;
            editor.PropertyChanged -= OnChanged;
        };
        Rebuild();
    }
    private void OnChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(NativeSettingsEditor.Settings)) Rebuild();
        if (args.PropertyName == nameof(NativeSettingsEditor.Error)) SetError(_editor.Error);
    }
    private void SetError(string? message)
    {
        var text = message ?? string.Empty;
        var visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        if (_error.Text == text && _error.Visibility == visibility) return;
        _error.Text = text; _error.Visibility = visibility;
        _errorAnnouncementPending = visibility == Visibility.Visible && !_errorOwnerUnloaded;
        AnnounceErrorIfReady();
    }
    private void AnnounceErrorIfReady()
    {
        try
        {
            if (!_errorAnnouncementPending || !IsLoaded || !_error.IsLoaded) return;
            // Consume this transition once. Unloading, clearing, or another error retires it;
            // returning to a hidden settings page must not replay an old announcement.
            _errorAnnouncementPending = false;
            if (string.IsNullOrEmpty(_error.Text)) return;
            for (DependencyObject? node = _error; node is not null; node = VisualTreeHelper.GetParent(node))
                if (node is UIElement element && element.Visibility != Visibility.Visible) return;
            var peer = FrameworkElementAutomationPeer.FromElement(_error)
                ?? FrameworkElementAutomationPeer.CreatePeerForElement(_error);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }
        catch (Exception)
        {
            _errorAnnouncementPending = false;
            // Accessibility feedback must not replace the original error.
        }
    }
    private void Rebuild()
    {
        _library.Children.Clear(); _library.RowDefinitions.Clear();
        if (_grid.Children.Count == 0)
        {
            for (var row = 0; row < WidgetLayoutPolicy.Rows; row++)
            for (var column = 0; column < WidgetLayoutPolicy.Columns; column++)
            {
                var cell = new Border { BorderThickness = new(1), Style = ThemeStyle("DropSpaceWidgetEditorCellStyle"), CornerRadius = new(8) };
                Grid.SetColumn(cell, column); Grid.SetRow(cell, row); _grid.Children.Add(cell);
            }
        }
        if (!_grid.Children.Contains(_dropPreview))
        {
            _dropPreview.Style = ThemeStyle("DropSpaceWidgetEditorDropPreviewStyle");
            Canvas.SetZIndex(_dropPreview, 99);
            _grid.Children.Add(_dropPreview);
        }
        var placements = _editor.Settings.Widgets.Layout.Expanded;
        foreach (var id in _placedButtons.Keys.Where(id => !placements.Any(item => item.Id == id)).ToArray())
        {
            _grid.Children.Remove(_placedButtons[id]);
            _placedButtons.Remove(id);
        }
        foreach (var placement in _editor.Settings.Widgets.Layout.Expanded)
        {
            // Retain the focused/captured control while moving it. Replacing the
            // grid can move focus and scroll the settings page unexpectedly.
            if (!_placedButtons.TryGetValue(placement.Id, out var button))
            {
                button = CreateDragButton(placement.Id, WidgetName(placement.Id));
                button.HorizontalAlignment = HorizontalAlignment.Stretch; button.VerticalAlignment = VerticalAlignment.Stretch;
                button.Click += (_, _) => { if (_suppressClicks.Remove(placement.Id)) return; _selectedId = placement.Id; RefreshSelection(); };
                _placedButtons[placement.Id] = button;
                button.Style = ThemeStyle("DropSpaceWidgetEditorButtonStyle");
                _grid.Children.Add(button);
            }
            Grid.SetColumn(button, placement.Column); Grid.SetRow(button, placement.Row); Grid.SetColumnSpan(button, placement.ColumnSpan); Grid.SetRowSpan(button, placement.RowSpan);
        }
        var available = Enum.GetValues<NativeWidgetId>().Where(id => !placements.Any(item => item.Id == id)).ToArray();
        var cells = new HashSet<(int Column, int Row)>();
        var previews = new List<(NativeWidgetId Id, WidgetPlacement Placement)>();
        var rows = WidgetLayoutPolicy.Rows;
        foreach (var id in available)
        {
            var size = WidgetCatalog.DefaultPlacement(id);
            var row = 0; var column = 0;
            while (true)
            {
                var fits = column + size.ColumnSpan <= WidgetLayoutPolicy.Columns;
                for (var y = row; fits && y < row + size.RowSpan; y++)
                for (var x = column; fits && x < column + size.ColumnSpan; x++) fits &= !cells.Contains((x,y));
                if (fits) break;
                column++; if (column >= WidgetLayoutPolicy.Columns) { column = 0; row++; }
            }
            for (var y = row; y < row + size.RowSpan; y++)
            for (var x = column; x < column + size.ColumnSpan; x++) cells.Add((x,y));
            rows = Math.Max(rows, row + size.RowSpan);
            previews.Add((id, size with { Column = column, Row = row }));
        }
        for (var row = 0; row < rows; row++)
        {
            _library.RowDefinitions.Add(new());
            for (var column = 0; column < WidgetLayoutPolicy.Columns; column++)
            {
                var cell = new Border { BorderThickness = new(1), Style = ThemeStyle("DropSpaceWidgetEditorCellStyle"), CornerRadius = new(8), IsHitTestVisible = false };
                Grid.SetColumn(cell,column); Grid.SetRow(cell,row); _library.Children.Add(cell);
            }
        }
        foreach (var (id, placement) in previews)
        {
            var button = CreateDragButton(id, WidgetName(id));
            button.Click += async (_, _) => { if (_suppressClicks.Remove(id)) return; await PlaceAsync(id,0,0); };
            button.HorizontalAlignment = HorizontalAlignment.Stretch; button.VerticalAlignment = VerticalAlignment.Stretch;
            Grid.SetColumn(button,placement.Column); Grid.SetRow(button,placement.Row);
            Grid.SetColumnSpan(button,placement.ColumnSpan); Grid.SetRowSpan(button,placement.RowSpan);
            _library.Children.Add(button);
        }
        UpdateGridSizes();
        RefreshSelection();
    }
    private Button CreateDragButton(NativeWidgetId id, string label)
    {
        var content = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var icon = new FontIcon { Glyph = NativeWidgetIcons.Glyph(id), FontSize = 18 };
        AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
        content.Children.Add(icon);
        content.Children.Add(new TextBlock { Text = label, FontSize = 12, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center });
        var dragTransform = new TranslateTransform();
        var button = new Button { Content = content, MinWidth = 0, MinHeight = 0, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(4), ManipulationMode = ManipulationModes.None, RenderTransform = dragTransform };
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
            var origin = pressedAt;
            pressedAt = null; dragging = false; _dropPreview.Visibility = Visibility.Collapsed; button.Opacity = 1; dragTransform.X = dragTransform.Y = 0;
            button.ReleasePointerCapture(args.Pointer);
            if (!shouldPlace) return;
            args.Handled = true;
            DispatcherQueue.TryEnqueue(() => _suppressClicks.Remove(id));
            var libraryPoint = args.GetCurrentPoint(_library).Position;
            if (libraryPoint.X >= 0 && libraryPoint.Y >= 0 && libraryPoint.X < _library.ActualWidth && libraryPoint.Y < _library.ActualHeight)
            {
                await RemoveAsync(id);
                return;
            }
            var target = args.GetCurrentPoint(_grid).Position;
            if (target.X < 0 || target.Y < 0 || target.X >= _grid.ActualWidth || target.Y >= _grid.ActualHeight) return;
            var column = (int)(target.X / ((_grid.ActualWidth + _grid.ColumnSpacing) / WidgetLayoutPolicy.Columns));
            var row = (int)(target.Y / ((_grid.ActualHeight + _grid.RowSpacing) / WidgetLayoutPolicy.Rows));
            var existing = _editor.Settings.Widgets.Layout.Expanded.FirstOrDefault(item => item.Id == id);
            if (existing is not null && origin is { } start)
            {
                column = existing.Column + (int)Math.Round((target.X - start.X) / ((_grid.ActualWidth + _grid.ColumnSpacing) / WidgetLayoutPolicy.Columns));
                row = existing.Row + (int)Math.Round((target.Y - start.Y) / ((_grid.ActualHeight + _grid.RowSpacing) / WidgetLayoutPolicy.Rows));
            }
            try { await PlaceAsync(id, column, row); }
            catch (Exception) { SetError(_strings.Get("WidgetDropFailed")); }
        }), true);
        button.AddHandler(PointerCanceledEvent, new PointerEventHandler((_, _) => { _suppressClicks.Remove(id); pressedAt = null; dragging = false; _dropPreview.Visibility = Visibility.Collapsed; button.Opacity = 1; dragTransform.X = dragTransform.Y = 0; }), true);
        button.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, args) =>
        {
            // Button releases capture before the routed PointerReleased handler runs.
            // Preserve the pending drop for that normal release; only cancel an interrupted drag.
            if (!args.GetCurrentPoint(_grid).Properties.IsLeftButtonPressed) return;
            _suppressClicks.Remove(id);
            pressedAt = null; dragging = false; _dropPreview.Visibility = Visibility.Collapsed; button.Opacity = 1; dragTransform.X = dragTransform.Y = 0;
        }), true);
        button.AddHandler(PointerMovedEvent, new PointerEventHandler((_, args) =>
        {
            var point = args.GetCurrentPoint(_grid);
            if (pressedAt is not { } origin || !point.Properties.IsLeftButtonPressed) return;
            if (Math.Abs(point.Position.X - origin.X) < DragThresholdDips && Math.Abs(point.Position.Y - origin.Y) < DragThresholdDips) return;
            if (!dragging) { dragging = true; _suppressClicks.Add(id); _selectedId = id; RefreshSelection(); button.Opacity = 0.65; }
            dragTransform.X = point.Position.X - origin.X;
            dragTransform.Y = point.Position.Y - origin.Y;
            var old = _editor.Settings.Widgets.Layout.Expanded.FirstOrDefault(p => p.Id == id);
            var size = old ?? WidgetCatalog.DefaultPlacement(id);
            var cellWidth = (_grid.ActualWidth + _grid.ColumnSpacing) / WidgetLayoutPolicy.Columns;
            var cellHeight = (_grid.ActualHeight + _grid.RowSpacing) / WidgetLayoutPolicy.Rows;
            if (cellWidth <= 0 || cellHeight <= 0) return;
            var column = old is null ? (int)Math.Floor(point.Position.X / cellWidth) : old.Column + (int)Math.Round(dragTransform.X / cellWidth);
            var row = old is null ? (int)Math.Floor(point.Position.Y / cellHeight) : old.Row + (int)Math.Round(dragTransform.Y / cellHeight);
            var inside = column >= 0 && row >= 0 && column + size.ColumnSpan <= WidgetLayoutPolicy.Columns && row + size.RowSpan <= WidgetLayoutPolicy.Rows;
            var desired = size with { Column = column, Row = row };
            var valid = inside && (old is null
                ? WidgetLayoutPolicy.TryAdd(_editor.Settings.Widgets.Layout, id, column, row, out var added) && added.Expanded.Any(p => p.Id == id && p.Column == column && p.Row == row)
                : WidgetLayoutPolicy.TryMoveOrResize(_editor.Settings.Widgets.Layout, desired, out var previewLayout));
            _dropPreview.Visibility = valid ? Visibility.Visible : Visibility.Collapsed;
            if (valid) { Grid.SetColumn(_dropPreview, column); Grid.SetRow(_dropPreview, row); Grid.SetColumnSpan(_dropPreview, size.ColumnSpan); Grid.SetRowSpan(_dropPreview, size.RowSpan); }
        }), true);
        return button;
    }
    private void RefreshSelection()
    {
        foreach (var (id, button) in _placedButtons)
        {
            var selected = id == _selectedId;
            button.BorderThickness = new Thickness(selected ? 3 : 1);
            button.Style = ThemeStyle(selected ? "DropSpaceWidgetEditorSelectedButtonStyle" : "DropSpaceWidgetEditorButtonStyle");
            AutomationProperties.SetItemStatus(button, selected ? _strings.Get("WidgetSelected") : string.Empty);
            if (button.Content is StackPanel content && content.Children.LastOrDefault() is TextBlock label)
                label.FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
        }
        var placement = _editor.Settings.Widgets.Layout.Expanded.FirstOrDefault(item => item.Id == _selectedId);
        _remove.IsEnabled = placement is not null;
        _selected.Text = placement is null ? string.Empty : WidgetName(placement.Id);
        _selected.Visibility = placement is null ? Visibility.Collapsed : Visibility.Visible;
        _sizes.Children.Clear();
        if (placement is null) return;
        foreach (var size in WidgetCatalog.Sizes(placement.Id))
        {
            var selected = size.Columns == placement.ColumnSpan && size.Rows == placement.RowSpan;
            var shape = new Border { Width = size.Columns * 16, Height = size.Rows * 16, CornerRadius = new(4), Style = ThemeStyle(selected ? "DropSpaceWidgetEditorSelectedSizeStyle" : "DropSpaceWidgetEditorSizeStyle"), BorderThickness = new(1) };
            var choose = new Button { Content = shape, Padding = new(6), MinWidth = 0, BorderThickness = new(selected ? 2 : 1) };
            var label = _strings.Get("WidgetSize") + $" {size.Columns} × {size.Rows}";
            AutomationProperties.SetName(choose,label); ToolTipService.SetToolTip(choose,label);
            choose.Click += async (_, _) =>
            {
                var current = _editor.Settings.Widgets.Layout.Expanded.FirstOrDefault(p => p.Id == placement.Id);
                if (current is not null) await ResizeAsync(current, size);
            };
            _sizes.Children.Add(choose);
        }
        _sizes.Visibility = _sizes.Children.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void UpdateGridSizes()
    {
        var width = _grid.ActualWidth;
        if (width <= 0) return;
        var cell = Math.Max(24,(width - (WidgetLayoutPolicy.Columns - 1) * _grid.ColumnSpacing) / WidgetLayoutPolicy.Columns);
        var height = cell * WidgetLayoutPolicy.Rows + (WidgetLayoutPolicy.Rows - 1) * _grid.RowSpacing;
        if (Math.Abs(_grid.Height-height)>0.5) _grid.Height=height;
        foreach (var row in _library.RowDefinitions) row.Height = new GridLength(cell);
    }
    private async void OnRemove(object sender, RoutedEventArgs args)
    {
        if (_selectedId is { } id) await RemoveAsync(id);
    }
    private string WidgetName(NativeWidgetId id) => _strings.Get(id == NativeWidgetId.Settings ? "WidgetSettingsName" : "Widget" + id + ".Text");
    private static Style ThemeStyle(string key) => (Style)Application.Current.Resources[key];
}
