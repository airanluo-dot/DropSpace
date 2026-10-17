using DropSpace.App.ViewModels;
using DropSpace.Core.Models;
using DropSpace.Core.Widgets;

namespace DropSpace.App.Services.Diagnostics
{
    internal static class BetaUiDiagnosticFixture
    {
        internal static BetaUiDiagnosticSelection ParseSelection(IReadOnlyList<string> arguments)
        {
            var positions = Enumerable.Range(0, arguments.Count)
                .Where(index => arguments[index].Equals("--beta-ui-scenario", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (positions.Length > 1 || positions.Length == 1 && positions[0] + 1 >= arguments.Count)
                throw new ArgumentException("At most one --beta-ui-scenario value is allowed.");
            var selected = positions.Length == 0 ? "all" : arguments[positions[0] + 1];
            return selected switch
            {
                "all" => new(selected, ["timer-menu-retirement", "batch-collapse-selection"],
                    ["timers-initial.png", "timer-menu-FocusTimer.png", "timer-menu-Countdown.png", "timers-reopened.png",
                        "batch-expanded-selected.png", "batch-collapsed-selection.png"]),
                "timers" => new(selected, ["timer-menu-retirement"],
                    ["timers-initial.png", "timer-menu-FocusTimer.png", "timer-menu-Countdown.png", "timers-reopened.png"]),
                "timer-visual" => new(selected, ["timer-first-frame-and-popup-retirement"],
                    ["timers-initial.png", "timer-menu-FocusTimer.png", "timers-reopened.png"]),
                "batch-selection" => new(selected, ["batch-collapse-selection"],
                    ["batch-expanded-selected.png", "batch-collapsed-selection.png"]),
                _ => throw new ArgumentException("--beta-ui-scenario must be all, timers, timer-visual, or batch-selection."),
            };
        }

        internal static string CreateOutput(IReadOnlyList<string> arguments)
        {
            var positions = Enumerable.Range(0, arguments.Count)
                .Where(index => arguments[index].Equals("--beta-ui-output", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (positions.Length != 1 || positions[0] + 1 >= arguments.Count ||
                !Path.IsPathFullyQualified(arguments[positions[0] + 1]))
                throw new ArgumentException("Exactly one absolute --beta-ui-output directory is required.");
            var output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(arguments[positions[0] + 1]));
            if (Directory.Exists(output) || File.Exists(output))
                throw new IOException("The diagnostic output must be a fresh directory.");
            var parent = Path.GetDirectoryName(output);
            if (parent is null || !Directory.Exists(parent))
                throw new IOException("The diagnostic output's parent must already exist.");
            for (var directory = new DirectoryInfo(parent); directory is not null; directory = directory.Parent)
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Diagnostic output must not follow reparse-point directories.");
            Directory.CreateDirectory(output);
            if ((File.GetAttributes(output) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Diagnostic output must not be a reparse point.");
            return output;
        }

        internal static AppSettings Settings => new()
        {
            PrivacyChoicesCompleted = true,
            ClipboardPaused = true,
            StartWithWindows = false,
            EnableDeviceHandoff = false,
            EnableCrossDeviceClipboard = false,
            EnableNearbySharing = false,
            EnableInternetSharing = false,
            AutoCheckForUpdates = false,
            Language = AppLanguagePreference.English,
            Widgets = new()
            {
                Enabled = true,
                // Explicit synthetic 2 x 1 layout, not a claim about the user's saved layout.
                Layout = new WidgetLayout([
                    new(NativeWidgetId.FocusTimer, 0, 0, 2, 1),
                    new(NativeWidgetId.Countdown, 2, 0, 2, 1),
                ], new(null, null, null)),
            },
        };

        internal static void ApplySettings(MainViewModel main, AppSettings settings) =>
            main.ApplyBetaUiDiagnosticSettings(settings);
    }

    internal sealed record BetaUiDiagnosticSelection(string Name, string[] Scenarios, string[] Captures)
    {
        internal bool RunTimers => Name is "all" or "timers" or "timer-visual";
        internal bool RunTimerVisual => Name == "timer-visual";
        internal bool RunBatch => Name is "all" or "batch-selection";
    }
}

namespace DropSpace.App.ViewModels
{
    public sealed partial class MainViewModel
    {
        // The explicit isolated entry point seeds presentation settings without calling
        // InitializeAsync (startup registration, clipboard listening and update recovery).
        internal void ApplyBetaUiDiagnosticSettings(AppSettings settings) => Settings = settings;
    }
}
