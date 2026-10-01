using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace DropSpace.App.Services;

/// <summary>Exercises native backdrop disconnection and collection only in the smoke-test process.</summary>
internal static class BackdropLifetimeSmoke
{
    public static async Task RunAsync(Action<string> report)
    {
        if (!DesktopAcrylicController.IsSupported()) return;
        // The window is hidden immediately after XAML connection. Collection and material
        // changes then happen without opening the app's main window or a user interaction.
        for (var cycle = 0; cycle < 12; cycle++)
        {
            report($"hidden-backdrop-window-{cycle}-create");
            var backdrop = new SystemBackdropElement { Width = 240, Height = 100 };
            var window = new Window { Content = new Grid { Children = { backdrop } } };
            window.AppWindow.MoveAndResize(new RectInt32(-2000, -2000, 320, 160));
            using var acrylic = new IslandAcrylicBackdrop();
            backdrop.SystemBackdrop = acrylic;
            window.Activate();
            window.AppWindow.Hide();
            try
            {
                for (var change = 0; change < 4; change++)
                {
                    report($"hidden-backdrop-window-{cycle}-change-{change}-enable");
                    acrylic.SetEnabled(true);
                    await Task.Delay(25);
                    report($"hidden-backdrop-window-{cycle}-change-{change}-enabled-gc");
                    await CollectAsync();
                    report($"hidden-backdrop-window-{cycle}-change-{change}-disable");
                    acrylic.SetEnabled(false);
                    await Task.Delay(25);
                    await CollectAsync();
                }
            }
            finally
            {
                report($"hidden-backdrop-window-{cycle}-close");
                acrylic.Dispose();
                window.Close();
            }
            await Task.Delay(25);
            report($"hidden-backdrop-window-{cycle}-closed-gc");
            await CollectAsync();
        }
    }

    private static Task CollectAsync() => Task.Run(() =>
    {
        // Never block the XAML dispatcher while COM finalizers may marshal back to it.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    });
}
