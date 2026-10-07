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
    private readonly Dictionary<NativeWidgetId, Button> _timerButtons = [];
    private Button? _stopwatchButton, _clipboardButton;
    public event EventHandler? SettingsRequested;
    public event EventHandler? PinnedRequested;
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
        Tiles.Children.Clear(); _timerButtons.Clear(); _values.Clear(); _placements.Clear(); _stopwatchButton = null; _clipboardButton = null;
        if (_view is null) return;
        EmptyText.Visibility = !_view.Enabled || _view.Layout.Expanded.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!_view.Enabled) return;
        foreach (var placement in _view.Layout.Expanded)
        {
            _placements[placement.Id] = placement;
            var title = new TextBlock { FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None };
            XamlResourceOverride.Apply(title, "Widget" + placement.Id);
            var value = new TextBlock { FontSize = placement.ColumnSpan == 1 ? 13 : placement.RowSpan == 1 ? 14 : placement.Id == NativeWidgetId.Clock ? 26 : 16, TextWrapping = TextWrapping.Wrap };
            var content = new StackPanel { Spacing = placement.ColumnSpan == 1 ? 2 : 4, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch };
            content.Children.Add(title); content.Children.Add(value);
            FrameworkElement tile;
            if (placement.Id is NativeWidgetId.Calculator or NativeWidgetId.PinnedItems)
            {
                var button = new Button { Content = content, MinWidth = 0, MinHeight = 0, Padding = new(4), HorizontalContentAlignment = HorizontalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
                value.Text = placement.Id == NativeWidgetId.Calculator ? "± × ÷" : "★";
                value.TextWrapping = TextWrapping.NoWrap;
                value.TextAlignment = TextAlignment.Center;
                title.TextAlignment = TextAlignment.Center;
                content.Children.Remove(value);
                content.Children.Add(new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Child = value, HorizontalAlignment = HorizontalAlignment.Stretch });
                button.Click += async (_, _) =>
                {
                    try
                    {
                        if (placement.Id == NativeWidgetId.Calculator)
                        {
                            if (!await Windows.System.Launcher.LaunchUriAsync(new Uri("calculator:")))
                                value.Text = _view.Text("WidgetLaunchFailed");
                        }
                        else { await _view.OpenPinnedAsync(); PinnedRequested?.Invoke(this, EventArgs.Empty); }
                    }
                    catch (Exception) { value.Text = _view.Text("WidgetLaunchFailed"); }
                };
                tile = button;
            }
            else if (placement.Id == NativeWidgetId.Settings)
            {
                var button = new Button { Content = new FontIcon { Glyph = "\uE713" }, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
                XamlResourceOverride.Apply(button, "WidgetSettingsAction");
                button.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty); tile = button;
            }
            else
            {
                _values[placement.Id] = value;
                if (placement.Id is NativeWidgetId.FocusTimer or NativeWidgetId.Countdown)
                {
                    content.Children.Remove(title); content.Orientation = Orientation.Horizontal;
                    var timerButton = new Button { Padding = new(4, 1, 4, 1), FontSize = 11 };
                    timerButton.Click += (_, _) => _view.ToggleTimer(placement.Id);
                    _timerButtons[placement.Id] = timerButton; content.Children.Add(timerButton);
                    var menu = new MenuFlyout(); var reset = new MenuFlyoutItem();
                    XamlResourceOverride.Apply(reset, "StopwatchReset");
                    reset.Click += (_, _) => _view.ResetTimer(placement.Id);
                    menu.Items.Add(reset); content.ContextFlyout = menu;
                }
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
                else tile = new Border { CornerRadius = new(14), Padding = new(placement.ColumnSpan == 1 ? 4 : 8), Style = (Style)Application.Current.Resources["DropSpaceOverlayWidgetTileStyle"], Child = content };
            }
            ToolTipService.SetToolTip(tile, _view.Text(placement.Id == NativeWidgetId.Settings ? "WidgetSettingsName" : "Widget" + placement.Id + ".Text"));
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
        foreach (var (id, button) in _timerButtons)
        {
            button.Content = _view.TimerActionLabel(id);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, _view.TimerActionLabel(id));
        }
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
                NativeWidgetId.UtcClock => data.LocalTime.UtcDateTime.ToString("HH:mm", _view.Culture) + " UTC",
                NativeWidgetId.FocusTimer => TimerText(_view.FocusTimer.Remaining),
                NativeWidgetId.Countdown => TimerText(_view.Countdown.Remaining),
                NativeWidgetId.DiskSpace => data.DiskFreeBytes is { } free ? $"{free / 1073741824d:0.0} GB" : "—",
                NativeWidgetId.NetworkStatus => data.NetworkAvailable is { } available ? _view.Text(available ? "WidgetNetworkConnected" : "WidgetNetworkDisconnected") : "—",
                NativeWidgetId.WeekProgress => $"{System.Globalization.ISOWeek.GetWeekOfYear(data.LocalTime.DateTime)} · {(int)((((int)data.LocalTime.DayOfWeek + 6) % 7 + data.LocalTime.TimeOfDay.TotalDays) / 7 * 100)}%",
                NativeWidgetId.Clock => data.LocalTime.ToString("t", _view.Culture),
                NativeWidgetId.Calendar => _placements[id].ColumnSpan == 1 ? data.LocalTime.ToString("dd", _view.Culture) : data.LocalTime.ToString("dddd\nd MMMM", _view.Culture),
                NativeWidgetId.ResourceUsage => $"CPU {Percent(data.CpuPercent)}\nRAM {Percent(data.MemoryPercent)}",
                NativeWidgetId.Battery => data.BatteryPercent is { } battery ? $"{battery}%" : data.OnAcPower == true ? "AC" : "—",
                NativeWidgetId.Uptime => $"{(int)data.Uptime.TotalHours}h {data.Uptime.Minutes}m",
                NativeWidgetId.Stopwatch => _view.StopwatchText,
                _ => string.Empty,
            };
    }
    private static string TimerText(TimeSpan value) => $"{(int)value.TotalMinutes:00}:{value.Seconds:00}";
    private static string Percent(double? value) => value is { } number ? $"{number:0}%" : "—";
}
