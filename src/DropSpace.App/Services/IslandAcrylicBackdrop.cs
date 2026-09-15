using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace DropSpace.App.Services;

/// <summary>One bounded SystemBackdropElement target; never attached to the host Window.</summary>
internal sealed class IslandAcrylicBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? _controller;
    private SystemBackdropConfiguration? _configuration;
    private FrameworkElement? _root;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);
        if (_controller is not null) throw new InvalidOperationException("Island material instances cannot be shared.");
        // The no-activate island is interactive while another application has focus.
        // Its material focus policy is independent of the transparent host HWND.
        _configuration = new SystemBackdropConfiguration { IsInputActive = true };
        _root = xamlRoot.Content as FrameworkElement;
        if (_root is not null) _root.ActualThemeChanged += OnThemeChanged;
        ApplyTheme();
        _controller = new DesktopAcrylicController();
        _controller.SetSystemBackdropConfiguration(_configuration);
        _controller.AddSystemBackdropTarget(target);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        if (_root is not null) _root.ActualThemeChanged -= OnThemeChanged;
        _controller?.RemoveSystemBackdropTarget(target);
        _controller?.Dispose(); _controller = null; _configuration = null; _root = null;
        base.OnTargetDisconnected(target);
    }

    private void OnThemeChanged(FrameworkElement sender, object args) => ApplyTheme();
    private void ApplyTheme()
    {
        if (_configuration is not null)
            _configuration.Theme = _root?.ActualTheme == ElementTheme.Dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
    }
}
