using DropSpace.App.Views.Island;
using DropSpace.Core.Island;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace DropSpace.App;

public sealed partial class OverlayWindow
{
    internal object ObserveGlassMaterial() => _materialController.CaptureGlassState();
    internal async Task<object> CheckGlassHostMovementAsync()
    {
        if (_materialController.MaterialTier != DropSpace.Core.Overlay.OverlayMaterialTier.LiquidGlass)
            throw new InvalidOperationException("The movement check requires a visible, ready glass surface.");
        await WaitForMotionSettledAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var original = _resolvedPlacement;
        var offset = original.HostLeftPixels + _positionedHostWidthPixels / 2 < _monitor.Left + _monitor.Width / 2 ? 32 : -32;
        var originalX = AppWindow.Position.X;
        FreezeGlassForDiagnosticCapture();
        try
        {
            var before = ObserveGlassMaterial();
            // Both moves arrive before XAML composes. Frozen WGC rules out a new capture as the cause of a redraw.
            foreach (var delta in new[] { offset / 2, offset })
            {
                _resolvedPlacement = original with { HostLeftPixels = original.HostLeftPixels + delta };
                if (!PositionFixedHost()) throw new InvalidOperationException("Cannot move the diagnostic island.");
            }
            await Task.Delay(160);
            var actualOffset = AppWindow.Position.X - originalX;
            var moved = ObserveGlassMaterial();
            if (!PositionFixedHost()) throw new InvalidOperationException("Cannot revalidate the diagnostic island.");
            await Task.Delay(160);
            var repeated = ObserveGlassMaterial();
            _resolvedPlacement = original;
            if (!PositionFixedHost()) throw new InvalidOperationException("Cannot restore the diagnostic island position.");
            await Task.Delay(160);
            return new { actualOffset, before, moved, repeated, restoredX = AppWindow.Position.X - originalX, restored = ObserveGlassMaterial() };
        }
        finally
        {
            _resolvedPlacement = original;
            PositionFixedHost();
            _materialController.SetVisible(false);
            _materialController.SetVisible(true);
        }
    }
    internal void BeginGlassMeasurement() => _materialController.BeginGlassMeasurement();
    internal object? EndGlassMeasurement() => _materialController.EndGlassMeasurement();
    internal object CompareGlassSampling(bool compareWidth = false) => _materialController.CompareGlassSampling(compareWidth);
    internal void FreezeGlassForDiagnosticCapture() => _materialController.FreezeGlassForDiagnosticCapture();
    internal FrameworkElement DiagnosticSurface => Surface;
    internal void InvokeMusicWindowWidgetForDiagnostic() => WidgetsExpanded.InvokeMusicWindowWidgetForDiagnostic();
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
            ContentTop = content.TransformToVisual(Root).TransformPoint(new Point()).Y,
            MusicLayout = _experience.Current.Page == IslandPage.Music ? MusicExpanded.ObserveLayout() : null };
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
