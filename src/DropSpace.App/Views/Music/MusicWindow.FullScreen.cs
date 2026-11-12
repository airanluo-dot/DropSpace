using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;
using WinRT.Interop;

namespace DropSpace.App.Views.Music;

public sealed partial class MusicWindow
{
    private const double WindowedTitleBarHeight = 32;
    private readonly RowDefinition _titleBarRow = new() { Height = new GridLength(WindowedTitleBarHeight) };
    private bool _windowedWasMaximized;

    private void OnWindowKeyDown(object sender, KeyRoutedEventArgs args)
    {
        _keyboardControlsIntent = true;
        NotifyInteraction();
        if (args.Key != VirtualKey.F11 || args.KeyStatus.IsMenuKeyDown ||
            IsDown(VirtualKey.Control) || IsDown(VirtualKey.Shift) ||
            IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) return;

        args.Handled = true;
        if (args.KeyStatus.WasKeyDown) return;

        if (AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen)
        {
            // Windows restores the original default presenter and its windowed placement.
            AppWindow.SetPresenter(AppWindowPresenterKind.Default);
            if (_windowedWasMaximized && AppWindow.Presenter is OverlappedPresenter restored &&
                restored.State != OverlappedPresenterState.Maximized)
                restored.Maximize();
        }
        else if (AppWindow.Presenter is OverlappedPresenter windowed)
        {
            _windowedWasMaximized = windowed.State == OverlappedPresenterState.Maximized;
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        }

        static bool IsDown(VirtualKey key) =>
            InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
    }

    private void SetFullScreenMinimized(bool minimized)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        if (IsIconic(hwnd) != minimized)
        {
            const int showMinimized = 6, restore = 9;
            ShowWindow(hwnd, minimized ? showMinimized : restore);
        }
        RefreshWindowVisibility();
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint window, int command);
}
