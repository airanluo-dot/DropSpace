namespace DropSpace.App.Services;

/// <summary>
/// Keeps Windows 11 DWM backdrop, corner, theme, frame refresh, and teardown in one lifecycle
/// boundary. It never activates the transient HWND or changes the foreground window.
/// </summary>
internal sealed class IslandBackdropController : IDisposable
{
    private readonly nint _windowHandle;
    private readonly bool _modernDwmAttributes;
    private bool _disposed;

    public IslandBackdropController(nint windowHandle, bool modernDwmAttributes)
    {
        _windowHandle = windowHandle;
        _modernDwmAttributes = modernDwmAttributes;
    }

    public OverlayNativeConfigurationResult Attach(bool darkMode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return OverlayWindowInterop.ConfigureVisualWindow(_windowHandle, _modernDwmAttributes, darkMode);
    }

    public OverlayNativeConfigurationResult Refresh(bool darkMode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_modernDwmAttributes)
        {
            return new OverlayNativeConfigurationResult(true, []);
        }

        return OverlayWindowInterop.RefreshDwmFrame(_windowHandle, darkMode);
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
