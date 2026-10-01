using DropSpace.Core.Abstractions;
using DropSpace.Core.Lyrics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace DropSpace.App.Views.Music;

/// <summary>The platform slider owns pointer, keyboard, focus and range-value accessibility.</summary>
public sealed class LyricsGlowModeControl : UserControl
{
    private const double ThumbWidth = 144;
    private readonly Slider _slider;
    private readonly IAppStringLocalizer _strings;
    private readonly string[] _labels;
    private bool _syncing;

    public LyricsGlowModeControl(IAppStringLocalizer strings)
    {
        _strings = strings;
        _labels = [strings.Get("LyricsGlowOff"), strings.Get("LyricsGlowAi"), strings.Get("LyricsGlowMusic")];
        _slider = new Slider
        {
            Minimum = 0, Maximum = 2, StepFrequency = 1, SmallChange = 1, LargeChange = 1,
            SnapsTo = SliderSnapsTo.StepValues, IsThumbToolTipEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        // Keep WinUI's complete input/focus template; only enlarge its native rounded thumb.
        // Theme resources continue to supply light, dark and high-contrast colors.
        _slider.Resources["SliderHorizontalThumbWidth"] = ThumbWidth;
        _slider.Resources["SliderHorizontalThumbHeight"] = 88d;
        _slider.Resources["SliderThumbCornerRadius"] = new CornerRadius(44);
        _slider.Resources["SliderOuterThumbBackground"] = Application.Current.Resources["ControlFillColorDefaultBrush"];
        _slider.Resources["SliderInnerThumbWidth"] = 0d;
        _slider.Resources["SliderInnerThumbHeight"] = 0d;
        _slider.Resources["SliderTrackThemeHeight"] = 14d;
        _slider.Resources["SliderTrackCornerRadius"] = new CornerRadius(7);
        _slider.Resources["SliderHorizontalHeight"] = 100d;
        AutomationProperties.SetAutomationId(_slider, "LyricsGlowMode");
        AutomationProperties.SetHelpText(_slider, strings.Get("LyricsGlowKeyboardHelp"));
        _slider.ValueChanged += (_, _) =>
        {
            UpdateAccessibleValue();
            if (!_syncing) ModeChanged?.Invoke(this, EventArgs.Empty);
        };

        var dots = new Grid { Margin = new Thickness(ThumbWidth / 2 - 4, 0, ThumbWidth / 2 - 4, 0), Height = 8, IsHitTestVisible = false };
        foreach (var alignment in new[] { HorizontalAlignment.Left, HorizontalAlignment.Center, HorizontalAlignment.Right })
        {
            dots.Children.Add(new Ellipse { Width = 8, Height = 8, HorizontalAlignment = alignment,
                Fill = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        }
        var labels = new Grid { ColumnSpacing = 8 };
        for (var index = 0; index < _labels.Length; index++)
        {
            labels.ColumnDefinitions.Add(new ColumnDefinition());
            var label = new TextBlock { Text = _labels[index], TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Top };
            Grid.SetColumn(label, index);
            labels.Children.Add(label);
        }
        var panel = new StackPanel { Spacing = 8, MaxWidth = 440, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(_slider);
        panel.Children.Add(dots);
        panel.Children.Add(labels);
        Content = panel;
        UpdateAccessibleValue();
    }

    public event EventHandler? ModeChanged;
    public LyricsGlowMode Mode
    {
        get => (LyricsGlowMode)(int)Math.Round(_slider.Value);
        set
        {
            _syncing = true;
            try { _slider.Value = Math.Clamp((int)value, 0, 2); UpdateAccessibleValue(); }
            finally { _syncing = false; }
        }
    }

    private void UpdateAccessibleValue()
    {
        var label = _labels[(int)Math.Clamp(Math.Round(_slider.Value), 0, 2)];
        AutomationProperties.SetName(_slider, _strings.Format("LyricsGlowAccessibleValue", label));
        AutomationProperties.SetItemStatus(_slider, label);
    }
}
