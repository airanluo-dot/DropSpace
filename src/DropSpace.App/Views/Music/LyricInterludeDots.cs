using DropSpace.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Shapes;

namespace DropSpace.App.Views.Music;

public sealed partial class LyricInterludeDots : UserControl
{
    private readonly Ellipse[] _dots = new Ellipse[3];

    public LyricInterludeDots()
    {
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        for (var i = 0; i < _dots.Length; i++)
        {
            var dot = new Ellipse { Width = 4, Height = 4 };
            dot.SetBinding(Shape.FillProperty, new Binding { Source = this, Path = new PropertyPath(nameof(Foreground)) });
            _dots[i] = dot;
            strip.Children.Add(dot);
        }
        Content = new Viewbox { Child = strip };
    }

    internal void Present(MediaViewModel media, bool visible)
    {
        Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) return;
        var phase = media.Position.TotalSeconds * Math.PI;
        for (var i = 0; i < _dots.Length; i++)
            _dots[i].Opacity = media.IsReducedMotion ? .65 : .55 + .35 * Math.Sin(phase - i * .5);
    }
}
