using System.ComponentModel;
using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace DropSpace.App.Views.Settings;

/// <summary>Native, localized settings rows with live transaction rollback refresh.</summary>
public sealed class SettingsForm : UserControl
{
    private readonly NativeSettingsEditor _editor;
    private readonly IAppStringLocalizer _strings;
    private readonly List<Action> _refresh = [];
    private bool _syncing;
    public StackPanel Rows { get; } = new() { Spacing = 10 };
    public SettingsForm(NativeSettingsEditor editor, IAppStringLocalizer strings)
    {
        _editor = editor; _strings = strings; Content = Rows;
        Loaded += (_, _) => { _editor.PropertyChanged += OnChanged; Refresh(); };
        Unloaded += async (_, _) => { _editor.PropertyChanged -= OnChanged; await _editor.FlushEditsAsync(); };
    }
    public void AddHeading(string key) => Rows.Children.Add(new TextBlock { Text = _strings.Get(key), FontSize = 20, TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new(0, 12, 0, 4) });
    public ToggleSwitch AddToggle(string key, Func<AppSettings, bool> read, Func<AppSettings, bool, AppSettings> write, Func<bool, Task<bool>>? beforeChange = null, Func<AppSettings, bool>? isEnabled = null)
    {
        var toggle = new ToggleSwitch(); AddRow(key, toggle);
        _refresh.Add(() =>
        {
            toggle.IsOn = read(_editor.Settings);
            toggle.IsEnabled = isEnabled?.Invoke(_editor.Settings) ?? true;
        }); Refresh();
        var revision = 0L;
        toggle.Toggled += async (_, _) =>
        {
            if (_syncing) return;
            var changeRevision = ++revision;
            var value = toggle.IsOn;
            var allowed = beforeChange is null || await beforeChange(value);
            if (changeRevision != revision) return;
            if (!allowed) { Refresh(); return; }
            await _editor.UpdateAsync(settings => write(settings, value));
        };
        return toggle;
    }
    public ComboBox AddChoice<T>(string key, IEnumerable<(T Value, string Label)> choices, Func<AppSettings, T> read, Func<AppSettings, T, AppSettings> write)
    {
        var combo = new ComboBox { MinWidth = 150 };
        foreach (var (value, label) in choices) combo.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        AddRow(key, combo);
        _refresh.Add(() => combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(item => EqualityComparer<T>.Default.Equals((T)item.Tag, read(_editor.Settings)))); Refresh();
        combo.SelectionChanged += async (_, _) => { if (!_syncing && combo.SelectedItem is ComboBoxItem item) { var value = (T)item.Tag; await _editor.UpdateAsync(settings => write(settings, value)); } };
        return combo;
    }
    public NumberBox AddNumber(string key, double minimum, double maximum, double step, Func<AppSettings, double> read, Func<AppSettings, double, AppSettings> write)
    {
        var number = new NumberBox { Minimum = minimum, Maximum = maximum, SmallChange = step, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 150 };
        AddRow(key, number);
        _refresh.Add(() => number.Value = read(_editor.Settings)); Refresh();
        number.ValueChanged += async (_, args) => { if (!_syncing && double.IsFinite(args.NewValue)) { var value = args.NewValue; await _editor.UpdateAsync(settings => write(settings, value)); } };
        return number;
    }
    public Slider AddSlider(string key, double minimum, double maximum, double step, Func<AppSettings, double> read,
        Func<AppSettings, double, AppSettings> write, Func<double, string> format)
    {
        var slider = new Slider { Minimum = minimum, Maximum = maximum, StepFrequency = step, SmallChange = step,
            LargeChange = step, HorizontalAlignment = HorizontalAlignment.Stretch };
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        var panel = new Grid { ColumnSpacing = 12, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.ColumnDefinitions.Add(new()); panel.ColumnDefinitions.Add(new() { Width = new GridLength(160) });
        panel.Children.Add(slider); Grid.SetColumn(label, 1); panel.Children.Add(label);
        AutomationProperties.SetName(slider, _strings.Get(key)); AutomationProperties.SetAutomationId(slider, key);
        // Give the full available row to precision-sensitive ranges. A fixed
        // readout column keeps signs, digit counts and unlimited/off wording
        // from changing either rail endpoint while the thumb is captured.
        var body = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
        body.Children.Add(new TextBlock { Text = _strings.Get(key), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(panel);
        Rows.Children.Add(new Border { CornerRadius = new(8), Padding = new(14, 10, 14, 10),
            Style = (Style)Application.Current.Resources["DropSpaceCardStyle"], Child = body });
        uint? pointer = null;
        _refresh.Add(() =>
        {
            if (pointer is not null || _editor.HasPendingEdit(key)) return;
            var value = read(_editor.Settings); slider.Value = value; label.Text = format(value);
            ToolTipService.SetToolTip(label, label.Text); AutomationProperties.SetHelpText(slider, label.Text);
        });
        Refresh();
        slider.ValueChanged += (_, args) =>
        {
            if (_syncing || !double.IsFinite(args.NewValue)) return;
            var value = Math.Clamp(Math.Round(args.NewValue / step, MidpointRounding.AwayFromZero) * step, minimum, maximum);
            label.Text = format(value);
            ToolTipService.SetToolTip(label, label.Text); AutomationProperties.SetHelpText(slider, label.Text);
            _editor.QueueEdit(key, settings => write(settings, value));
        };
        slider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) =>
        {
            var point = args.GetCurrentPoint(slider);
            if (point.IsInContact || point.Properties.IsLeftButtonPressed) pointer = args.Pointer.PointerId;
        }), true);
        async void FinishPointer(object sender, PointerRoutedEventArgs args)
        {
            if (pointer != args.Pointer.PointerId) return;
            pointer = null;
            await _editor.FlushEditsAsync(); Refresh();
        }
        slider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(FinishPointer), true);
        slider.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(FinishPointer), true);
        slider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(FinishPointer), true);
        slider.LostFocus += async (_, _) => { if (pointer is null) { await _editor.FlushEditsAsync(); Refresh(); } };
        return slider;
    }
    public void AddRow(string key, FrameworkElement control)
    {
        AutomationProperties.SetName(control, _strings.Get(key)); AutomationProperties.SetAutomationId(control, key);
        var grid = new Grid { ColumnSpacing = 20, Padding = new(14, 10, 14, 10) };
        grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = _strings.Get(key), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(control, 1); grid.Children.Add(control);
        Rows.Children.Add(new Border { CornerRadius = new(8), Style = (Style)Application.Current.Resources["DropSpaceCardStyle"], Child = grid });
    }
    private void OnChanged(object? sender, PropertyChangedEventArgs args) { if (args.PropertyName == nameof(NativeSettingsEditor.Settings)) Refresh(); }
    private void Refresh() { _syncing = true; try { foreach (var refresh in _refresh) refresh(); } finally { _syncing = false; } }
}
