using WinRT;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using DropSpace.Core.Overlay;

namespace DropSpace.App.Services;

/// <summary>One long-lived XAML target per surface, with a separately switchable Acrylic controller.</summary>
internal sealed class IslandAcrylicBackdrop : SystemBackdrop, IDisposable
{
    private ICompositionSupportsSystemBackdrop? _connectedTarget;
    private AcrylicCoverageBackdropTarget? _coverageTarget;
    private OverlayRegionSignature? _geometry;
    private DesktopAcrylicController? _controller;
    private SystemBackdropConfiguration? _configuration;
    private FrameworkElement? _root;
    private bool _enabled = true;
    private bool _prepareMotionRequested;
    private bool _disposed;

    internal void SetGeometry(OverlayRegionSignature geometry)
    {
        _geometry = geometry;
        _coverageTarget?.UpdateGeometry(geometry);
    }

    internal object? CaptureCoverageState() => _coverageTarget?.Snapshot();
    internal void SetMotion(IslandMotionBlurFrame frame) => _coverageTarget?.SetMotion(frame);
    internal void PrepareMotion()
    {
        _prepareMotionRequested = true;
        _coverageTarget?.PrepareMotion();
    }
    internal object? CaptureMotionState() => _coverageTarget?.CaptureMotionState();
    internal byte[] Coverage => _coverageTarget?.Coverage ?? [];

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
        _coverageTarget = new AcrylicCoverageBackdropTarget(target);
        if (_geometry is { } geometry) _coverageTarget.UpdateGeometry(geometry);
        _configuration = new SystemBackdropConfiguration { IsInputActive = true };
        _root = xamlRoot.Content as FrameworkElement;
        if (_root is not null) _root.ActualThemeChanged += OnThemeChanged;
        ApplyTheme();
        if (!_disposed && _enabled) AttachController();
    }

    private void AttachController()
    {
        if (_controller is not null || _coverageTarget is null || _configuration is null) return;
        var controller = new DesktopAcrylicController();
        _controller = controller;
        try
        {
            controller.SetSystemBackdropConfiguration(_configuration);
            if (!controller.AddSystemBackdropTarget(_coverageTarget))
                throw new InvalidOperationException("The Acrylic controller rejected its coverage target.");
            // A deferred XAML connection must warm its new graph before animation frames.
            if (_prepareMotionRequested) _coverageTarget.PrepareMotion();
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
            if (_coverageTarget is not null) controller.RemoveSystemBackdropTarget(_coverageTarget);
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
        var coverageTarget = _coverageTarget;
        var errors = new List<Exception>();
        void Clean(Action action) { try { action(); } catch (Exception error) { errors.Add(error); } }
        try { Clean(() => base.OnTargetDisconnected(target)); }
        finally
        {
            if (_root is not null) _root.ActualThemeChanged -= OnThemeChanged;
            try
            {
                Clean(DetachController);
                if (coverageTarget is not null) Clean(coverageTarget.Dispose);
                // The native backdrop link implements IClosable. Rooting prevents early
                // collection, but its last projection may still finalize off-thread after
                // disconnection. Close it on the owning XAML thread before unrooting.
                var targetClosed = false;
                Clean(() => { (connectedTarget ?? target).As<IDisposable>().Dispose(); targetClosed = true; });
                if (targetClosed && coverageTarget is not null) Clean(coverageTarget.ReleaseAfterTargetClosed);
            }
            finally
            {
                _configuration = null;
                _root = null;
                _connectedTarget = null;
                _coverageTarget = null;
                GC.KeepAlive(connectedTarget);
                GC.KeepAlive(target);
            }
        }
        if (errors.Count != 0) throw new AggregateException("Acrylic target cleanup failed.", errors);
    }

    private void OnThemeChanged(FrameworkElement sender, object args) => ApplyTheme();
    private void ApplyTheme()
    {
        if (_configuration is not null)
            _configuration.Theme = _root?.ActualTheme == ElementTheme.Dark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
    }
}
