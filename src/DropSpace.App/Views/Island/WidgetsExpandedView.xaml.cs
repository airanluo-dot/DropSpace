using System.ComponentModel;
using DropSpace.App.Services;
using DropSpace.App.ViewModels;
using DropSpace.Core.Widgets;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DropSpace.App.Views.Island;

public sealed partial class WidgetsExpandedView : UserControl
{
    private WidgetViewModel? _view;
    private readonly Dictionary<NativeWidgetId, TextBlock> _values = [];
    private readonly Dictionary<NativeWidgetId, WidgetPlacement> _placements = [];
    private Button? _stopwatchButton, _clipboardButton;
    public event EventHandler? SettingsRequested;
    public WidgetsExpandedView()
    {
        InitializeComponent();
        for (var i = 0; i < WidgetLayoutPolicy.Columns; i++) Tiles.ColumnDefinitions.Add(new());
        for (var i = 0; i < WidgetLayoutPolicy.Rows; i++) Tiles.RowDefinitions.Add(new());
        Loaded += OnLoaded; Unloaded += OnUnloaded;
    }
    public WidgetViewModel? ViewModel
    {
        get => _view;
        set { if (_view is not null) _view.PropertyChanged -= OnChanged; _view = value; if (IsLoaded && value is not null) value.PropertyChanged += OnChanged; Rebuild(); }
    }
    public void SetActive(bool active) => _view?.SetVisible(this, active);
    private void OnLoaded(object sender, RoutedEventArgs args) { if (_view is not null) _view.PropertyChanged += OnChanged; Rebuild(); }
    private void OnUnloaded(object sender, RoutedEventArgs args) { if (_view is not null) { _view.PropertyChanged -= OnChanged; _view.SetVisible(this, false); } }
    private void OnChanged(object? sender, PropertyChangedEventArgs args)
    { if (args.PropertyName is nameof(WidgetViewModel.Snapshot) or nameof(WidgetViewModel.StopwatchText) or nameof(WidgetViewModel.ClipboardPauseLabel)) RenderData(); else Rebuild(); }
    private void Rebuild()
    {
        Tiles.Children.Clear(); _values.Clear(); _placements.Clear(); _stopwatchButton = null; _clipboardButton = null;
        if (_view is null) return;
        EmptyText.Visibility = !_view.Enabled || _view.Layout.Expanded.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!_view.Enabled) return;
        foreach (var placement in _view.Layout.Expanded)
        {
            _placements[placement.Id] = placement;
            var title = new TextBlock { FontSize = 11, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis };
            XamlResourceOverride.Apply(title, "Widget" + placement.Id);
            var value = new TextBlock { FontSize = placement.ColumnSpan == 1 ? 13 : placement.RowSpan == 1 ? 14 : placement.Id == NativeWidgetId.Clock ? 26 : 16, TextWrapping = TextWrapping.Wrap };
            var content = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            content.Children.Add(title); content.Children.Add(value);
            FrameworkElement tile;
            if (placement.Id == NativeWidgetId.Settings)
            {
                var button = new Button { Content = new FontIcon { Glyph = "\uE713" }, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
                XamlResourceOverride.Apply(button, "WidgetSettingsAction");
                button.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty); tile = button;
            }
            else
            {
                _values[placement.Id] = value;
                if (placement.Id == NativeWidgetId.Stopwatch)
                {
                    if (placement.RowSpan == 1) { content.Children.Remove(title); content.Orientation = Orientation.Horizontal; value.VerticalAlignment = VerticalAlignment.Center; }
                    _stopwatchButton = new Button { Command = _view.StopwatchToggleCommand, Padding = new(4, 1, 4, 1), FontSize = 11 };
                    content.Children.Add(_stopwatchButton);
                    var menu = new MenuFlyout(); var reset = new MenuFlyoutItem { Command = _view.StopwatchResetCommand };
                    XamlResourceOverride.Apply(reset, "StopwatchReset"); menu.Items.Add(reset); content.ContextFlyout = menu;
                }
                if (placement.Id == NativeWidgetId.ClipboardPause)
                {
                    _clipboardButton = new Button { Command = _view.ClipboardPauseCommand, Content = new FontIcon { Glyph = "\uE77F", FontSize = 18 }, Padding = new(4), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
                    tile = _clipboardButton;
                }
                else tile = new Border { CornerRadius = new(14), Padding = new(8), Background = (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"], Child = content };
            }
            Grid.SetColumn(tile, placement.Column); Grid.SetRow(tile, placement.Row);
            Grid.SetColumnSpan(tile, placement.ColumnSpan); Grid.SetRowSpan(tile, placement.RowSpan);
            Tiles.Children.Add(tile);
        }
        RenderData();
    }
    private void RenderData()
    {
        var data = _view?.Snapshot;
        if (_view is null) return;
        if (_stopwatchButton is not null) { _stopwatchButton.Content = _view.StopwatchActionLabel; Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_stopwatchButton, _view.StopwatchActionLabel); }
        if (_clipboardButton is not null)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_clipboardButton, _view.ClipboardPauseLabel);
            ToolTipService.SetToolTip(_clipboardButton, _view.ClipboardPauseLabel);
            ((FontIcon)_clipboardButton.Content).Glyph = _view.ClipboardPaused ? "\uE768" : "\uE769";
        }
        foreach (var (id, text) in _values)
            text.Text = data is null ? "—" : id switch
            {
                NativeWidgetId.Clock => data.LocalTime.ToString("t"),
                NativeWidgetId.Calendar => _placements[id].ColumnSpan == 1 ? data.LocalTime.ToString("dd") : data.LocalTime.ToString("dddd\nd MMMM"),
                NativeWidgetId.ResourceUsage => $"CPU {Percent(data.CpuPercent)}\nRAM {Percent(data.MemoryPercent)}",
                NativeWidgetId.Battery => data.BatteryPercent is { } battery ? $"{battery}%" : data.OnAcPower == true ? "AC" : "—",
                NativeWidgetId.Uptime => $"{(int)data.Uptime.TotalHours}h {data.Uptime.Minutes}m",
                NativeWidgetId.Stopwatch => _view.StopwatchText,
                _ => string.Empty,
            };
    }
    private static string Percent(double? value) => value is { } number ? $"{number:0}%" : "—";
}
