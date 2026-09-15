using DropSpace.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DropSpace.App.Views.Island;

public sealed partial class ClipboardIslandView : UserControl
{
    public ClipboardIslandView() { InitializeComponent(); Unloaded += (_, _) => SetActive(false); }
    public ClipboardIslandViewModel? ViewModel { get; set; }
    public event EventHandler? OpenMainRequested;
    public void SetActive(bool active) { DataContext = ViewModel; ViewModel?.SetVisible(this, active); }
    private async void OnCopy(object sender, RoutedEventArgs args) { if (ViewModel is { } view && sender is FrameworkElement { Tag: ItemCardViewModel card }) await view.CopyAsync(card); }
    private async void OnPin(object sender, RoutedEventArgs args) { if (ViewModel is { } view && sender is FrameworkElement { Tag: ItemCardViewModel card }) await view.PinAsync(card); }
    private async void OnRemove(object sender, RoutedEventArgs args) { if (ViewModel is { } view && sender is FrameworkElement { Tag: ItemCardViewModel card }) await view.RemoveAsync(card); }
    private async void OnPause(object sender, RoutedEventArgs args) { if (ViewModel is { } view) await view.TogglePauseAsync(); }
    private void OnOpenMain(object sender, RoutedEventArgs args) => OpenMainRequested?.Invoke(this, EventArgs.Empty);
}
