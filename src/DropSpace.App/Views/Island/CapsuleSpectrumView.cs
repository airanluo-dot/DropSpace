using DropSpace.Core.Media;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Shapes;

namespace DropSpace.App.Views.Island;

public sealed partial class CapsuleSpectrumView : UserControl
{
    private readonly Canvas _canvas = new() { UseLayoutRounding = false };
    internal IReadOnlyList<Rectangle> Bars { get; }

    public CapsuleSpectrumView()
    {
        UseLayoutRounding = false;
        Content = _canvas;
        var bars = new Rectangle[SpectrumAnalyzer.BandCount];
        for (var i = 0; i < bars.Length; i++)
        {
            var bar = new Rectangle { UseLayoutRounding = false };
            bar.SetBinding(Shape.FillProperty, new Binding { Source = this, Path = new PropertyPath(nameof(Foreground)) });
            bars[i] = bar;
            _canvas.Children.Add(bar);
        }
        Bars = bars;
    }

    internal void Present(IReadOnlyList<double> levels)
    {
        // Scale the upstream 4:3 bar/space geometry to the existing accessory slot.
        const double expansion = .145;
        var unit = Width / (4 * Bars.Count + 3 * (Bars.Count - 1) + 4 * expansion);
        var minimum = 4 * unit;
        for (var i = 0; i < Bars.Count; i++)
        {
            var value = Math.Clamp(levels[i], 0, 1);
            var bar = Bars[i];
            bar.Width = minimum * (1 + expansion * value);
            bar.Height = minimum + (.9 * Height - minimum) * value;
            bar.RadiusX = bar.RadiusY = bar.Width / 2;
            Canvas.SetLeft(bar, minimum * expansion / 2 + i * 7 * unit + (minimum - bar.Width) / 2);
            Canvas.SetTop(bar, (Height - bar.Height) / 2);
        }
    }
}
