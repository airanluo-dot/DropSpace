using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace DropSpace.App.Views.Settings;

/// <summary>Measures whole buttons and shares each wrapped row's maximum height.</summary>
public sealed class DownloadActionPanel : Panel
{
    private const double Gap = 8;
    public static Button Create(string text) => new()
    {
        Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
        MinHeight = 36, Padding = new Thickness(12, 6, 12, 6), FontSize = 14,
        VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Stretch,
    };
    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children) child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        return Layout(availableSize.Width, false);
    }
    protected override Size ArrangeOverride(Size finalSize)
    { Layout(finalSize.Width, true); return finalSize; }
    private Size Layout(double width, bool arrange)
    {
        var x = 0d; var y = 0d; var height = 0d; var maximum = 0d;
        var row = new List<(UIElement Child, double X, double Width)>();
        void Flush()
        {
            if (arrange) foreach (var item in row) item.Child.Arrange(new Rect(item.X, y, item.Width, height));
            maximum = Math.Max(maximum, Math.Max(0, x - Gap));
            y += height + Gap; x = 0; height = 0; row.Clear();
        }
        foreach (var child in Children.Where(child => child.Visibility == Visibility.Visible))
        {
            var childWidth = Math.Min(width, child.DesiredSize.Width);
            if (row.Count > 0 && x + childWidth > width) Flush();
            row.Add((child, x, childWidth));
            x += childWidth + Gap; height = Math.Max(height, child.DesiredSize.Height);
        }
        if (row.Count > 0) Flush();
        return new Size(maximum, Math.Max(0, y - Gap));
    }
}
