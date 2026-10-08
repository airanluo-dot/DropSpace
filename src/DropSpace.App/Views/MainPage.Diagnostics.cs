using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DropSpace.App.Views;

// Internal observations of the actual shell controls. Only the explicit isolated
// FeatureModuleUiSmoke entry point calls these; no alternate production UI is built.
public sealed partial class MainPage
{
    internal Task SelectDiagnosticSectionAsync(string key) => SelectSectionAsync(key);
    internal ModuleShellObservation ObserveModuleShell() => new(
        Navigation.MenuItems.Concat(Navigation.FooterMenuItems).OfType<NavigationViewItem>()
            .Select(item => item.Tag as string ?? "").ToArray(),
        (Navigation.SelectedItem as NavigationViewItem)?.Tag as string,
        _viewModel.CurrentSection, _selectedModuleId,
        BuiltInSurfaces.Visibility == Visibility.Visible,
        ModuleContent.Visibility == Visibility.Visible,
        _modulePage?.ActualWidth ?? 0, _modulePage?.ActualHeight ?? 0,
        IsLoaded, XamlRoot?.RasterizationScale ?? 0);
    internal FrameworkElement? DiagnosticModuleContent => _modulePage;
}

internal sealed record ModuleShellObservation(string[] NavigationKeys, string? SelectedKey,
    string BuiltInSection, string? SelectedModuleId, bool BuiltInVisible, bool ModuleVisible,
    double ModuleWidth, double ModuleHeight, bool Loaded, double RasterizationScale);
