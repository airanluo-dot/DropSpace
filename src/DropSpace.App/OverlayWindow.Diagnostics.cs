using DropSpace.App.Views.Island;
using Microsoft.UI.Xaml;

namespace DropSpace.App;

public sealed partial class OverlayWindow
{
    internal FrameworkElement DiagnosticSurface => Surface;
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
