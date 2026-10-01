using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace DropSpace.App.Services;

/// <summary>One long-lived XAML target per surface, with a separately switchable Acrylic controller.</summary>
internal sealed class IslandAcrylicBackdrop : SystemBackdrop, IDisposable
{
    private ICompositionSupportsSystemBackdrop? _connectedTarget;
    private DesktopAcrylicController? _controller;
    private SystemBackdropConfiguration? _configuration;
    private FrameworkElement? _root;
    private bool _enabled = true;
    private bool _disposed;

    internal void SetEnabled(bool enabled)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _enabled = enabled;
        if (enabled) AttachController();
        else DetachController();
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop target, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(target, xamlRoot);
        _connectedTarget = target;
        _configuration = new SystemBackdropConfiguration { IsInputActive = true };
        _root = xamlRoot.Content as FrameworkElement;
        if (_root is not null) _root.ActualThemeChanged += OnThemeChanged;
        ApplyTheme();
        if (!_disposed && _enabled) AttachController();
    }

    private void AttachController()
    {
        if (_controller is not null || _connectedTarget is null || _configuration is null) return;
        var controller = new DesktopAcrylicController();
        _controller = controller;
        try
        {
            controller.SetSystemBackdropConfiguration(_configuration);
            controller.AddSystemBackdropTarget(_connectedTarget);
        }
        catch
        {
            _controller = null;
            controller.Dispose();
            throw;
        }
    }

    private void DetachController()
    {
        var controller = _controller;
        _controller = null;
        if (controller is null) return;
        try
        {
            if (_connectedTarget is not null) controller.RemoveSystemBackdropTarget(_connectedTarget);
        }
        finally { controller.Dispose(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _enabled = false;
        // Do not clear SystemBackdrop or release its target here. XAML still owns the
        // native ContentExternalBackdropLink until it disconnects the actual surface.
        DetachController();
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop target)
    {
        var connectedTarget = _connectedTarget;
        try { base.OnTargetDisconnected(target); }
        finally
        {
            if (_root is not null) _root.ActualThemeChanged -= OnThemeChanged;
            try { DetachController(); }
            finally
            {
                _configuration = null;
                _root = null;
                _connectedTarget = null;
                GC.KeepAlive(connectedTarget);
                GC.KeepAlive(target);
            }
        }
    }

    private void OnThemeChanged(FrameworkElement sender, object args) => ApplyTheme();
    private void ApplyTheme()
    {
        if (_configuration is not null)
            _configuration.Theme = _root?.ActualTheme == ElementTheme.Dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
    }
}
