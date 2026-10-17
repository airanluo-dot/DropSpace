using DropSpace.App.ViewModels;
using DropSpace.Core.Models;
using DropSpace.Core.Widgets;

namespace DropSpace.App.Services.Diagnostics
{
    internal static class BetaUiDiagnosticFixture
    {
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
