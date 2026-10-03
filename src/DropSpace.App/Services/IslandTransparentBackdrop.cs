using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace DropSpace.App.Services;

/// <summary>Allows rounded XAML edge coverage to blend with the desktop and halo,
/// rather than WinUI's default opaque black HWND backing.</summary>
internal sealed class IslandTransparentBackdrop : SystemBackdrop
{
    private Windows.UI.Composition.Compositor? _compositor;
    private Windows.UI.Composition.CompositionColorBrush? _brush;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnTargetConnected(target, root);
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().EnsureSystemDispatcherQueue();
        _compositor = new Windows.UI.Composition.Compositor();
        _brush = _compositor.CreateColorBrush(Microsoft.UI.Colors.Transparent);
        target.SystemBackdrop = _brush;
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        try
        {
            target.SystemBackdrop = null;
            base.OnTargetDisconnected(target);
        }
        finally
        {
            _brush?.Dispose(); _brush = null;
            _compositor?.Dispose(); _compositor = null;
        }
    }

    protected override void OnDefaultSystemBackdropConfigurationChanged(ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        // This backing stays transparent across theme/focus changes. The rounded
        // material owns those preferences; no default material configuration is used.
    }
}
