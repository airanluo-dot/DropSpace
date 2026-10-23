using DropSpace.App.Views.Island;
using DropSpace.Core.Island;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace DropSpace.App;

public sealed partial class OverlayWindow
{
    internal FrameworkElement DiagnosticSurface => Surface;
    internal QuickRetrievalView DiagnosticRetrievalView => RetrievalView;
    internal void DiagnosticShowPage(IslandPage page)
    {
        CloseRetrieval();
        _experience.Open(page);
    }

    internal object ObserveIslandToolbar()
    {
        object Observe(FrameworkElement button)
        {
            var bounds = button.TransformToVisual(Root).TransformBounds(new Rect(0, 0, button.ActualWidth, button.ActualHeight));
            var center = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
            var top = VisualTreeHelper.FindElementsInHostCoordinates(center, Root).FirstOrDefault();
            var belongsToButton = false;
            for (var current = top as DependencyObject; current is not null; current = VisualTreeHelper.GetParent(current))
                if (ReferenceEquals(current, button)) { belongsToButton = true; break; }
            return new { bounds.X, bounds.Y, bounds.Width, bounds.Height,
                Visible = button.Visibility == Visibility.Visible && ExpandedToolbar.Visibility == Visibility.Visible,
                Name = AutomationProperties.GetName(button), HitTestReachesButton = belongsToButton };
        }
        FrameworkElement content = _experience.Current.Page switch
        {
            IslandPage.Music => MusicExpanded,
            IslandPage.Widgets => WidgetsExpanded,
            IslandPage.Clipboard => ClipboardExpanded,
            _ => FilesExpanded,
        };
        return new { Page = _experience.Current.Page.ToString(), Retrieval = _retrievalVisible,
            Search = Observe(RetrievalOpenButton), Collapse = Observe(OtherPageCollapse),
            ContentTop = content.TransformToVisual(Root).TransformPoint(new Point()).Y };
    }
    internal OverlayModuleObservation ObserveModuleOverlay() => new(
        _experience.Current.State.ToString(), _experience.Current.Page.ToString(),
        _experience.Current.Variant.ToString(), _experience.IsManuallyOpen,
        Root.IsLoaded, _isVisible, Surface.ActualWidth, Surface.ActualHeight,
        FilesExpanded.Visibility == Visibility.Visible && FilesExpanded.IsHitTestVisible,
        MusicExpanded.Visibility == Visibility.Visible && MusicExpanded.IsHitTestVisible,
        WidgetsExpanded.Visibility == Visibility.Visible && WidgetsExpanded.IsHitTestVisible,
        ClipboardExpanded.Visibility == Visibility.Visible && ClipboardExpanded.IsHitTestVisible,
        ModuleCompactText.Visibility == Visibility.Visible, ModuleCompactText.Text,
        WidgetsExpanded.ObserveModuleWidgets());
}

internal sealed record OverlayModuleObservation(string State, string Page, string Variant,
    bool ManuallyOpen, bool Loaded, bool Visible, double Width, double Height,
    bool FilesActive, bool MusicActive, bool WidgetsActive, bool ClipboardActive,
    bool ModuleCompactVisible, string ModuleCompactText, WidgetModuleObservation Widgets);
