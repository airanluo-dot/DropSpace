using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace DropSpace.App;

public sealed partial class OverlayWindow
{
    private bool _retrievalVisible;
    private nint _retrievalReturnWindow;
    private uint _retrievalReturnProcess;

    internal static nint CaptureRetrievalForeground() => RetrievalGetForegroundWindow();

    private void OpenRetrieval(nint returnWindow)
    {
        RememberRetrievalForeground(returnWindow);
        _retrievalVisible = true;
        RetrievalView.ResetPreview();
        ApplyPageTransition(); ApplyRetrievalVisibility();
        RetrievalView.SetActive(true);
    }

    private void RememberRetrievalForeground(nint returnWindow)
    {
        if (returnWindow != 0 && returnWindow != _windowHandle)
        {
            _retrievalReturnWindow = returnWindow;
            _ = RetrievalGetWindowThreadProcessId(returnWindow, out _retrievalReturnProcess);
        }
    }

    private void CloseRetrieval()
    {
        _retrievalVisible = false;
        RetrievalView.SetActive(false);
        RetrievalView.Visibility = Visibility.Collapsed;
        ApplyPageTransition(); ApplyRetrievalVisibility();
    }

    private void ApplyRetrievalVisibility()
    {
        RetrievalView.Visibility = _retrievalVisible ? Visibility.Visible : Visibility.Collapsed;
        ExpandedToolbar.Visibility = _retrievalVisible ? Visibility.Collapsed : Visibility.Visible;
        RetrievalOpenButton.Visibility = _retrievalVisible ? Visibility.Collapsed : Visibility.Visible;
        if (_retrievalVisible)
        {
            PreviousPageRail.Visibility = Visibility.Collapsed;
            NextPageRail.Visibility = Visibility.Collapsed;
            OtherPageCollapse.Visibility = Visibility.Collapsed;
        }
        else
        {
            var page = _experience.Current.Page;
            PreviousPageRail.Visibility = page == Core.Island.IslandPage.Widgets ? Visibility.Collapsed : Visibility.Visible;
            NextPageRail.Visibility = page == Core.Island.IslandPage.Clipboard ? Visibility.Collapsed : Visibility.Visible;
            OtherPageCollapse.Visibility = Visibility.Visible;
        }
    }

    private void OnRetrievalOpen(object sender, RoutedEventArgs args)
    {
        OpenRetrieval(CaptureRetrievalForeground());
        TryActivateQuickPanel();
    }

    private void OnRetrievalBack(object? sender, EventArgs args)
    {
        CloseRetrieval();
        RetrievalOpenButton.Focus(FocusState.Keyboard);
    }

    private void OnRetrievalTaken(object? sender, EventArgs args)
    {
        // Do not restore foreground after an asynchronous copy if the user has
        // already switched to a different app while it was being prepared.
        var ownsForeground = RetrievalGetForegroundWindow() == _windowHandle;
        CloseRetrieval();
        _experience.Collapse(); _viewModel.Collapse();
        if (ownsForeground && _retrievalReturnWindow != 0 && RetrievalIsWindow(_retrievalReturnWindow))
        {
            _ = RetrievalGetWindowThreadProcessId(_retrievalReturnWindow, out var process);
            if (process == _retrievalReturnProcess) _ = RetrievalSetForegroundWindow(_retrievalReturnWindow);
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern nint RetrievalGetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "SetForegroundWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RetrievalSetForegroundWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "IsWindow")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RetrievalIsWindow(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static extern uint RetrievalGetWindowThreadProcessId(nint window, out uint processId);
}
