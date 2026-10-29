using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace DropSpace.App.Views.Island;

public sealed partial class WidgetsExpandedView
{
    internal void InvokeMusicWindowWidgetForDiagnostic()
    {
        if (!_active || !IsLoaded || _view is null) throw new InvalidOperationException("Widgets must be visible.");
        var button = Tiles.Children.OfType<Button>().Single(item =>
            AutomationProperties.GetName(item) == _view.Text("WidgetMusicWindow.Text"));
        var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(button);
        ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)peer.GetPattern(
            Microsoft.UI.Xaml.Automation.Peers.PatternInterface.Invoke)).Invoke();
    }
    internal WidgetModuleObservation ObserveModuleWidgets() => new(
        ModuleStrip.Visibility == Visibility.Visible,
        ModuleCards.Children.OfType<Button>().Select(button => new ModuleChipObservation(
            AutomationProperties.GetName(button), button.ActualWidth, button.ActualHeight)).ToArray(),
        Tiles.Children.OfType<FrameworkElement>().Select((tile, index) =>
        {
            var origin = tile.TransformToVisual(Tiles).TransformPoint(new Point(0, 0));
            return new WidgetTileObservation(index, origin.X, origin.Y, tile.ActualWidth, tile.ActualHeight);
        }).ToArray());
}

internal sealed record ModuleChipObservation(string Name, double Width, double Height);
internal sealed record WidgetTileObservation(int Index, double X, double Y, double Width, double Height);
internal sealed record WidgetModuleObservation(bool StripVisible, ModuleChipObservation[] Chips, WidgetTileObservation[] Tiles);
