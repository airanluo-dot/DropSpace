using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace DropSpace.App.Views.Settings;

/// <summary>Native slider with an actual-value readout and integer keyboard steps on logarithmic ranges.</summary>
public sealed class SettingsValueSlider : UserControl
{
    private readonly Slider _slider = new() { MinWidth = 140 };
    private readonly TextBlock _readout = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private bool _syncing;
    private uint? _pointer;
    public bool IsInteracting => _pointer is not null;
    public event EventHandler<double>? ValueChanged;
    public event EventHandler? InteractionCompleted;
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double),
        typeof(SettingsValueSlider), new PropertyMetadata(1d, OnPropertyChanged));
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(nameof(Minimum), typeof(double),
        typeof(SettingsValueSlider), new PropertyMetadata(1d, OnPropertyChanged));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double),
        typeof(SettingsValueSlider), new PropertyMetadata(100d, OnPropertyChanged));
    public static readonly DependencyProperty NonlinearProperty = DependencyProperty.Register(nameof(Nonlinear), typeof(bool),
        typeof(SettingsValueSlider), new PropertyMetadata(false, OnPropertyChanged));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public bool Nonlinear { get => (bool)GetValue(NonlinearProperty); set => SetValue(NonlinearProperty, value); }
    public SettingsValueSlider()
    {
        var grid = new Grid { ColumnSpacing = 12, MinWidth = 230 };
        grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = new GridLength(64) });
        grid.Children.Add(_slider); Grid.SetColumn(_readout, 1); grid.Children.Add(_readout); Content = grid;
        _slider.ValueChanged += (_, e) =>
        {
            if (_syncing) return;
            Commit(Nonlinear ? Math.Pow(2, e.NewValue) : e.NewValue);
        };
        _slider.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down)) return;
            Commit(Value + (e.Key is VirtualKey.Right or VirtualKey.Up ? 1 : -1)); e.Handled = true;
        };
        _slider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) =>
        {
            var point = args.GetCurrentPoint(_slider);
            if (point.IsInContact || point.Properties.IsLeftButtonPressed) _pointer = args.Pointer.PointerId;
        }), true);
        _slider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(FinishPointer), true);
        _slider.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(FinishPointer), true);
        _slider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(FinishPointer), true);
        LostFocus += (_, _) => { if (!IsInteracting) InteractionCompleted?.Invoke(this, EventArgs.Empty); };
        Unloaded += (_, _) => { _pointer = null; InteractionCompleted?.Invoke(this, EventArgs.Empty); };
        Loaded += (_, _) =>
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_slider,
                Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(this)); Refresh();
        };
    }
    private void FinishPointer(object sender, PointerRoutedEventArgs args)
    {
        if (_pointer != args.Pointer.PointerId) return;
        _pointer = null;
        InteractionCompleted?.Invoke(this, EventArgs.Empty);
    }
    private void Commit(double value)
    {
        value = Math.Clamp(Math.Round(value), Minimum, Maximum);
        if (Value == value) return;
        Value = value; ValueChanged?.Invoke(this, value);
    }
    private static void OnPropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) => ((SettingsValueSlider)sender).Refresh();
    private void Refresh()
    {
        if (_slider is null || Maximum < Minimum) return;
        _syncing = true;
        try
        {
            _slider.Minimum = Nonlinear ? Math.Log2(Math.Max(1, Minimum)) : Minimum;
            _slider.Maximum = Nonlinear ? Math.Log2(Math.Max(1, Maximum)) : Maximum;
            _slider.StepFrequency = Nonlinear ? 0.01 : 1;
            _slider.SmallChange = Nonlinear ? 0.01 : 1;
            _slider.Value = Nonlinear ? Math.Log2(Math.Clamp(Value, Math.Max(1, Minimum), Maximum)) : Value;
            _readout.Text = Value.ToString("0.###", System.Globalization.CultureInfo.GetCultureInfo(Language));
            ToolTipService.SetToolTip(_readout, _readout.Text);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(_slider, _readout.Text);
        }
        finally { _syncing = false; }
    }
}
