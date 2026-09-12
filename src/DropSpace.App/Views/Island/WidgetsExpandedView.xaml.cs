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
    { if (args.PropertyName == nameof(WidgetViewModel.Snapshot)) RenderData(); else Rebuild(); }
    private void Rebuild()
    {
        Tiles.Children.Clear(); _values.Clear();
        if (_view is null) return;
        EmptyText.Visibility = !_view.Enabled || _view.Layout.Expanded.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!_view.Enabled) return;
        foreach (var placement in _view.Layout.Expanded)
        {
            var title = new TextBlock { FontSize = 11, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis };
            XamlResourceOverride.Apply(title, "Widget" + placement.Id);
            var value = new TextBlock { FontSize = placement.Id == NativeWidgetId.Clock ? 26 : 16, TextWrapping = TextWrapping.Wrap };
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
                tile = new Border { CornerRadius = new(14), Padding = new(12), Background = (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"], Child = content };
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
        foreach (var (id, text) in _values)
            text.Text = data is null ? "—" : id switch
            {
                NativeWidgetId.Clock => data.LocalTime.ToString("t"),
                NativeWidgetId.Calendar => data.LocalTime.ToString("dddd\nd MMMM"),
                NativeWidgetId.ResourceUsage => $"CPU {Percent(data.CpuPercent)}\nRAM {Percent(data.MemoryPercent)}",
                _ => string.Empty,
            };
    }
    private static string Percent(double? value) => value is { } number ? $"{number:0}%" : "—";
}
