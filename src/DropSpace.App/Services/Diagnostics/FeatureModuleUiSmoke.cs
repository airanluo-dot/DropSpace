using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DropSpace.App.Services.Dlc;
using DropSpace.App.Views;
using DropSpace.Core.Dlc;
using DropSpace.Core.Island;
using DropSpace.Infrastructure.Dlc;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace DropSpace.App.Services.Diagnostics;

/// <summary>One explicitly requested lifecycle in the real shell, using a disposable data root.</summary>
internal static class FeatureModuleUiSmoke
{
    internal const string Switch = "--feature-module-ui-smoke";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly string[] BuiltInKeys = ["Space", "Clipboard", "Pinned", "Music", "Settings"];

    internal static bool IsRequested(IReadOnlyList<string> arguments) => arguments.Contains(Switch, StringComparer.OrdinalIgnoreCase);
    internal static FeatureModuleUiSmokeOptions Parse(IReadOnlyList<string> arguments, string? root)
    {
        if (!IsRequested(arguments) || !arguments.Contains("--test-mode", StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Module UI diagnostics require explicit test mode.");
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new ArgumentException("An isolated absolute test root is required.");
        string Required(string key)
        {
            var matches = Enumerable.Range(0, arguments.Count).Where(i => arguments[i].Equals(key, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1 || matches[0] + 1 >= arguments.Count || !Path.IsPathFullyQualified(arguments[matches[0] + 1]))
                throw new ArgumentException("An explicit absolute diagnostic package and official descriptor are required.");
            return Path.GetFullPath(arguments[matches[0] + 1]);
        }
        return new(Path.GetFullPath(root), Required("--module-ui-package"), Required("--module-ui-record"));
    }

    // App validates/creates the fresh root with FeatureModuleUiSmokeFixture before BuildServices.
    // App then runs ordinary startup, awaits module initialization, and calls this on its UI dispatcher.
    internal static async Task<FeatureModuleUiSmokeReceipt> RunAsync(FeatureModuleUiSmokeOptions options,
        MainWindow main, OverlayWindowService overlay, FeatureModuleRuntime runtime, ModulePackageStore store,
        IslandExperienceCoordinator island, CancellationToken cancellationToken = default)
    {
        var receipt = new FeatureModuleUiSmokeReceipt(options.Root);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var token = deadline.Token;
        var captures = Path.Combine(options.Root, "module-ui-evidence");
        Directory.CreateDirectory(captures);
        try
        {
            main.ShowAndActivate();
            var page = main.DiagnosticPage;
            await WaitUntilAsync(() => page.IsLoaded && page.ActualWidth > 0 && page.ActualHeight > 0, token);
            receipt.Stage = "no-dlc-startup";
            var initial = page.ObserveModuleShell();
            RequireBuiltIns(initial);
            Require(initial.NavigationKeys.All(key => !key.StartsWith("module:", StringComparison.Ordinal)) &&
                runtime.Snapshots.All(item => item.Installation.Version is null && !item.Installation.Enabled && item.RunState == ModuleRunState.Stopped) &&
                runtime.IslandContents.Count == 0 && !Directory.Exists(Path.Combine(options.Root, "Modules")), "No-DLC startup unexpectedly activated a module.");
            receipt.Observations.Add(new(receipt.Stage, initial, overlay.DiagnosticActiveWindow.ObserveModuleOverlay()));
            await CaptureBestEffortAsync(main.Content as FrameworkElement ?? page, captures, "no-dlc-main", receipt, token);

            receipt.Stage = "install";
            var package = await FeatureModuleUiSmokeFixture.StagePackageAsync(options.Root, options.PackagePath, options.RecordPath, store, token);
            var official = runtime.GetPackage(package.Id);
            Require(official == package, "The diagnostic artifact differs from the official host catalog.");
            receipt.PackageId = package.Id; receipt.PackageSha256 = package.Sha256;
            await runtime.InstallAsync(package.Id).WaitAsync(token);
            await WaitUntilAsync(() => page.ObserveModuleShell().NavigationKeys.Any(key => key.StartsWith("module:" + package.Id + "/", StringComparison.Ordinal)), token);
            var installed = runtime.Snapshots.Single(item => item.Installation.Id == package.Id);
            Require(installed.Installation.Enabled && installed.RunState == ModuleRunState.Running && installed.Manifest is not null, "Module activation did not complete.");
            var manifest = installed.Manifest!;
            var moduleKey = "module:" + package.Id + "/" + manifest.Ui.Pages.Single().Id;
            await page.SelectDiagnosticSectionAsync("Space");
            await page.SelectDiagnosticSectionAsync(moduleKey);
            await WaitUntilAsync(() => page.DiagnosticModuleContent is { ActualWidth: > 0, ActualHeight: > 0 }, token);
            var opened = page.ObserveModuleShell(); RequireBuiltIns(opened);
            Require(opened.ModuleVisible && !opened.BuiltInVisible && opened.SelectedModuleId == package.Id, "The declared page was not rendered in the real shell.");
            receipt.Observations.Add(new("module-page", opened, overlay.DiagnosticActiveWindow.ObserveModuleOverlay()));
            await CaptureBestEffortAsync(main.Content as FrameworkElement ?? page, captures, "module-main", receipt, token);

            // Observe native widget geometry before contribution, then submit through the actual page button.
            island.Open(IslandPage.Widgets);
            await WaitUntilAsync(() => overlay.DiagnosticActiveWindow.ObserveModuleOverlay() is { WidgetsActive: true, Visible: true }, token);
            await Task.Delay(180, token);
            var nativeTiles = overlay.DiagnosticActiveWindow.ObserveModuleOverlay().Widgets.Tiles;
            Require(nativeTiles.Length > 0 && nativeTiles.All(tile => tile.Width > 0 && tile.Height > 0), "The existing native widgets were not rendered.");
            island.SelectPage(IslandPage.Files);
            await WaitUntilAsync(() => overlay.DiagnosticActiveWindow.ObserveModuleOverlay().FilesActive, token);
            receipt.Stage = "page-action-and-island";
            var actionId = "ModuleAction-" + package.Id + "-" + manifest.Ui.Pages.Single().Actions.Single().Id;
            Button? action = null;
            await WaitUntilAsync(() =>
            {
                action = Descendants(page.DiagnosticModuleContent!).OfType<Button>()
                    .SingleOrDefault(button => AutomationProperties.GetAutomationId(button) == actionId);
                return action is { IsEnabled: true };
            }, token);
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(action!) ?? new ButtonAutomationPeer(action!);
            (peer.GetPattern(PatternInterface.Invoke) as IInvokeProvider ?? throw new InvalidOperationException("The module action has no host invoke provider.")).Invoke();
            await WaitUntilAsync(() => runtime.IslandContents.Count == 1, token);
            var protectedFiles = overlay.DiagnosticActiveWindow.ObserveModuleOverlay();
            Require(protectedFiles.Page == "Files" && protectedFiles.FilesActive && protectedFiles.ManuallyOpen, "Module content stole the user's native Island page.");
            receipt.Observations.Add(new("manual-files-protected", page.ObserveModuleShell(), protectedFiles));
            island.SelectPage(IslandPage.Widgets);
            await WaitUntilAsync(() => overlay.DiagnosticActiveWindow.ObserveModuleOverlay().Widgets is { StripVisible: true, Chips.Length: 1 }, token);
            await Task.Delay(180, token);
            var contributed = overlay.DiagnosticActiveWindow.ObserveModuleOverlay();
            Require(contributed.WidgetsActive && contributed.Widgets.Chips.All(chip => chip.Width > 0 && chip.Height > 0) &&
                EqualTiles(nativeTiles, contributed.Widgets.Tiles), "The module contribution changed native widget geometry or failed to render.");
            receipt.Observations.Add(new("widgets-contribution", page.ObserveModuleShell(), contributed));
            await CaptureBestEffortAsync(overlay.DiagnosticActiveWindow.DiagnosticSurface, captures, "module-island", receipt, token);

            receipt.Stage = "disable-selected-page";
            var disabling = runtime.DisableAsync(package.Id);
            await WaitUntilAsync(() => !page.ObserveModuleShell().ModuleVisible &&
                page.ObserveModuleShell().NavigationKeys.All(key => !key.StartsWith("module:", StringComparison.Ordinal)), token);
            var retired = page.ObserveModuleShell(); RequireBuiltIns(retired);
            Require(retired.BuiltInVisible && retired.BuiltInSection == "Space" && runtime.IslandContents.Count == 0, "Retirement did not return to the retained built-in page.");
            await disabling.WaitAsync(token);
            await WaitUntilAsync(() => overlay.DiagnosticActiveWindow.ObserveModuleOverlay().Widgets.Chips.Length == 0, token);
            receipt.Observations.Add(new(receipt.Stage, retired, overlay.DiagnosticActiveWindow.ObserveModuleOverlay()));
            await CaptureBestEffortAsync(main.Content as FrameworkElement ?? page, captures, "retired-main", receipt, token);

            receipt.Stage = "enable-and-uninstall";
            await runtime.EnableAsync(package.Id).WaitAsync(token);
            await WaitUntilAsync(() => page.ObserveModuleShell().NavigationKeys.Contains(moduleKey), token);
            await page.SelectDiagnosticSectionAsync(moduleKey);
            var data = store.GetDataDirectory(package.Id);
            var marker = Path.Combine(data, "diagnostic-retained.txt");
            await File.WriteAllTextAsync(marker, "isolated module data retention probe", token);
            await runtime.UninstallAsync(package.Id).WaitAsync(token);
            await WaitUntilAsync(() => !page.ObserveModuleShell().ModuleVisible &&
                page.ObserveModuleShell().NavigationKeys.All(key => !key.StartsWith("module:", StringComparison.Ordinal)), token);
            var removed = runtime.Snapshots.Single(item => item.Installation.Id == package.Id);
            Require(removed.Installation.Version is null && !removed.Installation.Enabled && removed.RunState == ModuleRunState.Stopped &&
                removed.Installation.Transaction == ModuleTransactionState.None && runtime.IslandContents.Count == 0 &&
                !Directory.Exists(store.GetVersionDirectory(package.Id, package.Version)) && File.Exists(marker), "Uninstall or retained-data contract failed.");
            RequireBuiltIns(page.ObserveModuleShell());
            receipt.Observations.Add(new(receipt.Stage, page.ObserveModuleShell(), overlay.DiagnosticActiveWindow.ObserveModuleOverlay()));
            receipt.Status = "awaiting-normal-shutdown";
            receipt.Stage = "controls-complete";
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            receipt.Status = "failed";
            receipt.Failure = error.GetType().Name + ": " + error.Message;
            Debug.WriteLine("Module UI diagnostic failed at " + receipt.Stage + ": " + receipt.Failure);
        }
        // An incomplete receipt remains honest if the process exits before ordinary shutdown returns.
        WriteReceipt(receipt);
        return receipt;
    }

    // App calls this only AFTER awaiting its ordinary ShutdownAsync, then exits using the result.
    internal static int Complete(FeatureModuleUiSmokeReceipt receipt, object runtimeAfterShutdown, bool runtimeReleased, int overlaySurfacesAfterShutdown)
    {
        receipt.RuntimeAfterShutdown = runtimeAfterShutdown;
        receipt.OverlaySurfacesAfterShutdown = overlaySurfacesAfterShutdown;
        receipt.ShutdownCompletedAtUtc = DateTimeOffset.UtcNow;
        if (receipt.Status == "awaiting-normal-shutdown" && runtimeReleased && overlaySurfacesAfterShutdown == 0) receipt.Status = "passed";
        else if (receipt.Status != "failed") { receipt.Status = "failed"; receipt.Failure = "Normal shutdown retained a module resource or overlay surface."; }
        WriteReceipt(receipt);
        return receipt.Status == "passed" ? 0 : 1;
    }

    private static void RequireBuiltIns(ModuleShellObservation observation) => Require(observation.Loaded &&
        BuiltInKeys.All(key => observation.NavigationKeys.Count(item => item == key) == 1), "Existing host navigation was missing or duplicated.");
    private static void Require(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
    private static bool EqualTiles(Views.Island.WidgetTileObservation[] before, Views.Island.WidgetTileObservation[] after) =>
        before.Length == after.Length && before.Zip(after).All(pair => pair.First.Index == pair.Second.Index &&
            Math.Abs(pair.First.X - pair.Second.X) < .5 && Math.Abs(pair.First.Y - pair.Second.Y) < .5 &&
            Math.Abs(pair.First.Width - pair.Second.Width) < .5 && Math.Abs(pair.First.Height - pair.Second.Height) < .5);
    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken token)
    {
        var until = Stopwatch.StartNew();
        while (!predicate())
        {
            token.ThrowIfCancellationRequested();
            if (until.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException("The actual UI did not reach its expected state.");
            await Task.Delay(50, token);
        }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var descendant in Descendants(VisualTreeHelper.GetChild(root, index))) yield return descendant;
    }
    private static async Task CaptureBestEffortAsync(FrameworkElement root, string output, string name,
        FeatureModuleUiSmokeReceipt receipt, CancellationToken token)
    {
        try
        {
            // Allow one brief presentation turn; capture is optional evidence, never a lifecycle assertion.
            await Task.Delay(50, token);
            var capture = await CaptureAsync(root, output, name, token);
            receipt.Captures.Add(capture);
            if (!capture.PixelReadbackPassed)
                receipt.CaptureWarnings.Add(new(name, "The actual pixel readback was uniform or lacked sufficient visible content; the unmodified readback was saved, and pixel validation did not pass.", DateTimeOffset.UtcNow));
        }
        catch (Exception error) when (error is not OutOfMemoryException && !token.IsCancellationRequested)
        {
            receipt.CaptureWarnings.Add(new(name, error.GetType().Name + " (0x" + error.HResult.ToString("X8") + "): " + error.Message, DateTimeOffset.UtcNow));
            Debug.WriteLine("Optional module UI capture failed: " + name + ": " + error.GetType().Name);
        }
    }
    private static async Task<FeatureModuleCapture> CaptureAsync(FrameworkElement root, string output, string name, CancellationToken token)
    {
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(root).AsTask().WaitAsync(TimeSpan.FromSeconds(3), token);
        var buffer = await bitmap.GetPixelsAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), token);
        var pixels = new byte[buffer.Length];
        using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
        Require(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0 && pixels.LongLength == (long)bitmap.PixelWidth * bitmap.PixelHeight * 4,
            "The actual control pixel readback had invalid dimensions or incomplete bytes.");
        var pixelReadbackPassed = MusicVisualSmokeOptions.HasPixelContent(pixels);
        using var encoded = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, encoded);
        var scale = root.XamlRoot.RasterizationScale;
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96 * scale, 96 * scale, pixels);
        await encoder.FlushAsync().AsTask().WaitAsync(token);
        using var pngReader = new DataReader(encoded.GetInputStreamAt(0));
        await pngReader.LoadAsync(checked((uint)encoded.Size)).AsTask().WaitAsync(token);
        var png = new byte[checked((int)encoded.Size)]; pngReader.ReadBytes(png);
        await File.WriteAllBytesAsync(Path.Combine(output, name + ".png"), png, token);
        return new(name + ".png", bitmap.PixelWidth, bitmap.PixelHeight, scale, Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant(), pixelReadbackPassed);
    }
    private static void WriteReceipt(FeatureModuleUiSmokeReceipt receipt) => File.WriteAllText(
        Path.Combine(receipt.Root, "module-ui-evidence", "receipt.json"), JsonSerializer.Serialize(receipt, Json));
}

internal sealed record FeatureModuleUiSmokeOptions(string Root, string PackagePath, string RecordPath);
internal sealed record FeatureModuleCapture(string File, int Width, int Height, double RasterizationScale, string Sha256, bool PixelReadbackPassed);
internal sealed record FeatureModuleCaptureWarning(string Capture, string Reason, DateTimeOffset RecordedAtUtc);
internal sealed record FeatureModuleObservation(string Stage, ModuleShellObservation Shell, OverlayModuleObservation Island);
internal sealed class FeatureModuleUiSmokeReceipt(string root)
{
    public int SchemaVersion { get; } = 1;
    public int FunctionalScenarioExecutions { get; } = 1;
    public string EvidenceKind { get; } = "actual-main-window-and-overlay-winui-controls";
    public string Root { get; } = root;
    public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
    public string Status { get; set; } = "running";
    public string Stage { get; set; } = "startup";
    public string? Failure { get; set; }
    public string? PackageId { get; set; }
    public string? PackageSha256 { get; set; }
    public List<FeatureModuleObservation> Observations { get; } = [];
    public List<FeatureModuleCapture> Captures { get; } = [];
    public List<FeatureModuleCaptureWarning> CaptureWarnings { get; } = [];
    public object? RuntimeAfterShutdown { get; set; }
    public int? OverlaySurfacesAfterShutdown { get; set; }
    public DateTimeOffset? ShutdownCompletedAtUtc { get; set; }
    public string[] Limitations { get; } = ["One actual Windows desktop/DPI/language only; no full regression matrix.",
        "Pixel capture is best-effort and does not gate lifecycle assertions; failed readbacks are recorded separately and passing lifecycle checks does not imply passing pixel validation.",
        "Saved control pixel readbacks require visual review; no complete OS sandbox or external media behavior claim.",
        "No user clipboard capture, updater run, model inference, drag stress or legacy broad smoke suite was requested by this entry point."];
}
