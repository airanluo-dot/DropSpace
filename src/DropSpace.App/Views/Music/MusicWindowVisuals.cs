using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace DropSpace.App.Views.Music;

internal static class MusicWindowVisuals
{
    internal static FrameworkElement Symbol(string symbol, double size)
    {
        var data = symbol switch
        {
            "play" => "M5,2 Q3,1 3,4 L3,20 Q3,23 5,22 L21,13 Q23,12 21,11 Z",
            "pause" => "M5,1 Q3,1 3,3 L3,21 Q3,23 5,23 L8,23 Q10,23 10,21 L10,3 Q10,1 8,1 Z M16,1 Q14,1 14,3 L14,21 Q14,23 16,23 L19,23 Q21,23 21,21 L21,3 Q21,1 19,1 Z",
            "previous" => "M11,3 Q12,2 12,4 L12,20 Q12,22 10,21 L1,13 Q0,12 1,11 Z M23,3 Q24,2 24,4 L24,20 Q24,22 22,21 L13,13 Q12,12 13,11 Z",
            "next" => "M1,3 Q0,2 0,4 L0,20 Q0,22 2,21 L11,13 Q12,12 11,11 Z M13,3 Q12,2 12,4 L12,20 Q12,22 14,21 L23,13 Q24,12 23,11 Z",
            "lyrics" => MusicWindowGlyphs.Lyrics,
            "translation" => MusicWindowGlyphs.Translation,
            _ => throw new ArgumentOutOfRangeException(nameof(symbol)),
        };
        var viewport = symbol is "lyrics" or "translation" ? 20 : 24;
        var shape = (FrameworkElement)XamlReader.Load($"<Path xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Data='{data}' Fill='White' />");
        var canvas = new Canvas { Width = viewport, Height = viewport };
        canvas.Children.Add(shape);
        return new Viewbox { Width = size, Height = size, Child = canvas, IsHitTestVisible = false };
    }

    internal static void StyleSlider(Slider slider)
    {
        slider.Height = 32;
        slider.IsThumbToolTipEnabled = false;
        slider.Resources["SliderHorizontalThumbWidth"] = 3d;
        slider.Resources["SliderHorizontalThumbHeight"] = 3d;
        slider.Resources["SliderInnerThumbWidth"] = 3d;
        slider.Resources["SliderInnerThumbHeight"] = 3d;
        slider.Resources["SliderTrackThemeHeight"] = 3d;
        slider.Resources["SliderOuterThumbBackground"] = new SolidColorBrush(Colors.Transparent);
        slider.Resources["SliderThumbBorderBrush"] = new SolidColorBrush(Colors.Transparent);
        foreach (var suffix in new[] { "", "PointerOver", "Pressed", "Disabled" })
        {
            slider.Resources["SliderTrackFill" + suffix] = new SolidColorBrush(ColorHelper.FromArgb(55, 255, 255, 255));
            slider.Resources["SliderTrackValueFill" + suffix] = new SolidColorBrush(ColorHelper.FromArgb(suffix == "Disabled" ? (byte)80 : (byte)190, 255, 255, 255));
            slider.Resources["SliderThumbBackground" + suffix] = new SolidColorBrush(ColorHelper.FromArgb(suffix == "Disabled" ? (byte)80 : (byte)210, 255, 255, 255));
        }
    }

    internal static void StyleRoundToggle(ToggleButton button)
    {
        button.Width = button.Height = 44;
        button.MinWidth = button.MinHeight = 44;
        button.CornerRadius = new CornerRadius(22);
        button.Padding = new Thickness(8);
        button.BorderThickness = new Thickness(0);
        foreach (var suffix in new[] { "", "PointerOver", "Pressed", "Checked", "CheckedPointerOver", "CheckedPressed" })
        {
            var alpha = suffix.Contains("Checked", StringComparison.Ordinal) ? (byte)40 : suffix.Length == 0 ? (byte)0 : (byte)24;
            button.Resources["ToggleButtonBackground" + suffix] = new SolidColorBrush(ColorHelper.FromArgb(alpha, 255, 255, 255));
            button.Resources["ToggleButtonBorderBrush" + suffix] = new SolidColorBrush(Colors.Transparent);
            button.Resources["ToggleButtonForeground" + suffix] = new SolidColorBrush(Colors.White);
        }
    }
}
