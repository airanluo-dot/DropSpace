using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace DropSpace.App.Views.Settings;

/// <summary>Measures whole buttons and shares each wrapped row's maximum height.</summary>
public sealed class DownloadActionPanel : Panel
{
    private const double Gap = 8;
    public static Button Create(string text, string? glyph = null, params string[] alternateLabels)
    {
        var content = new Grid { ColumnSpacing = glyph is null ? 0 : Gap };
        content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new());
        if (glyph is not null)
        {
            var icon = new FontIcon
            {
                Glyph = glyph, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 16,
                Width = 16, Height = 16, IsTextScaleFactorEnabled = false,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
            content.Children.Add(icon);
        }
        // Alternate labels reserve the same native measured slot across task states.
        // They are noninteractive sizing elements, excluded from the accessible name.
        foreach (var label in alternateLabels)
        {
            var reserve = Label(label);
            reserve.Opacity = 0; reserve.IsHitTestVisible = false;
            AutomationProperties.SetAccessibilityView(reserve, AccessibilityView.Raw);
            Grid.SetColumn(reserve, 1); content.Children.Add(reserve);
        }
        var caption = Label(text);
        Grid.SetColumn(caption, 1); content.Children.Add(caption);
        return new Button
        {
            Content = content, MinWidth = 36, MinHeight = 36,
            Padding = new Thickness(12, 6, 12, 6), FontSize = 14,
            VerticalContentAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
    }
    private static TextBlock Label(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    public static void SetContent(Button button, string text, string glyph)
    {
        var content = (Grid)button.Content;
        var caption = (TextBlock)content.Children[^1];
        if (caption.Text != text) caption.Text = text;
        var icon = (FontIcon)content.Children[0];
        if (icon.Glyph != glyph) icon.Glyph = glyph;
    }
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
