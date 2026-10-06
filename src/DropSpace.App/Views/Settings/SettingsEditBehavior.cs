using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace DropSpace.App.Views.Settings;

/// <summary>Only settings surfaces end single-line editing; IME and interactive descendants retain input ownership.</summary>
internal static class SettingsEditBehavior
{
    private sealed class EditState { public bool Composing; }
    private static readonly ConditionalWeakTable<TextBox, EditState> States = new();
    public static bool IsComposing(XamlRoot root) => FocusManager.GetFocusedElement(root) is TextBox box &&
        States.TryGetValue(box, out var state) && state.Composing;
    public static void Attach(FrameworkElement surface, Control focusTarget)
    {
        surface.Loaded += (_, _) => Hook(surface, focusTarget);
        surface.Tapped += (_, e) =>
        {
            if (e.OriginalSource is not (Grid or Border or StackPanel or ScrollViewer)) return;
            for (var node = e.OriginalSource as DependencyObject; node is not null && node != surface; node = VisualTreeHelper.GetParent(node))
                if (node is Control and not (ScrollViewer or UserControl)) return;
            focusTarget.Focus(FocusState.Programmatic);
        };
    }
    private static void Hook(DependencyObject root, Control focusTarget)
    {
        if (root is TextBox box && !box.AcceptsReturn && !States.TryGetValue(box, out _))
        {
            var state = States.GetOrCreateValue(box);
            box.TextCompositionStarted += (_, _) => state.Composing = true;
            box.TextCompositionEnded += (_, _) => state.Composing = false;
            box.KeyDown += (_, e) =>
            {
                if (e.Key != VirtualKey.Enter || state.Composing || box.Name == "QuickPanelHotkeyText") return;
                // Placement owns its apply action. Other NumberBoxes validate on losing focus.
                for (var node = VisualTreeHelper.GetParent(box); node is not null; node = VisualTreeHelper.GetParent(node))
                    if (node is NumberBox { Name: "OverlayPlacementXNumber" or "OverlayPlacementYNumber" }) return;
                focusTarget.Focus(FocusState.Programmatic); e.Handled = true;
            };
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) Hook(VisualTreeHelper.GetChild(root, i), focusTarget);
    }
}
