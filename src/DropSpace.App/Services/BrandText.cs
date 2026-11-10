using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace DropSpace.App.Services;

public sealed class BrandText : DependencyObject
{
    // Recycled text elements use the content key to recover their original ink.
    public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
        "Key", typeof(string), typeof(BrandText), new PropertyMetadata(null, UpdateInk));
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(BrandText), new PropertyMetadata(false, UpdateInk));

    public static string? GetKey(DependencyObject target) => (string?)target.GetValue(KeyProperty);
    public static void SetKey(DependencyObject target, string? value) => target.SetValue(KeyProperty, value);
    public static bool GetEnabled(DependencyObject target) => (bool)target.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject target, bool value) => target.SetValue(EnabledProperty, value);

    private static readonly Lazy<Color[][]> Ramps = new(() =>
    {
        using var stream = typeof(BrandText).Assembly.GetManifestResourceStream("DropSpace.TextGradients.json")!;
        var ramps = JsonSerializer.Deserialize<string[][]>(stream)!;
        return ramps.Select(ramp => ramp.Select(hex =>
        {
            var rgb = int.Parse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }).ToArray()).ToArray();
    });

    private static void UpdateInk(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not TextBlock text) return;
        if (!GetEnabled(text))
        {
            text.ClearValue(TextBlock.ForegroundProperty);
            return;
        }
        var key = GetKey(text);
        var seed = key is null ? RuntimeHelpers.GetHashCode(text) : HashCode.Combine(key, text.Name);
        var random = new Random(seed);
        var ramp = Ramps.Value[random.Next(Ramps.Value.Length)];
        var reverse = random.Next(2) != 0;
        var ink = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
        ink.GradientStops.Add(new GradientStop { Offset = 0, Color = ramp[reverse ? 2 : 0] });
        ink.GradientStops.Add(new GradientStop { Offset = 0.32 + random.NextDouble() * 0.36, Color = ramp[1] });
        ink.GradientStops.Add(new GradientStop { Offset = 1, Color = ramp[reverse ? 0 : 2] });
        text.Foreground = ink;
    }
}
