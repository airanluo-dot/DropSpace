using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace DropSpace.App.Services.Diagnostics;

internal static partial class BetaUiDiagnostics
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static void WriteEvidence(BetaUiDiagnosticReceipt receipt, bool final = false)
    {
        var path = Path.Combine(receipt.Output, final ? "receipt.json" : "stage.json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(receipt, Json));
        File.Move(temporary, path, true);
    }

    private static void Stage(BetaUiDiagnosticReceipt receipt, string stage)
    {
        receipt.Stage = stage;
        WriteEvidence(receipt);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try { while (!condition()) await Task.Delay(40, deadline.Token); }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        { throw new TimeoutException("The named UI condition did not complete within 15 seconds.", error); }
    }

    private static async Task WaitAtStageAsync(BetaUiDiagnosticReceipt receipt, string stage,
        Func<bool> condition, CancellationToken token, Func<object>? snapshot = null)
    {
        Stage(receipt, stage);
        void Observe(string outcome)
        {
            if (snapshot is not null)
                receipt.Observations.Add(new { stage, outcome, state = snapshot() });
            WriteEvidence(receipt);
        }
        Observe("waiting");
        try { await WaitUntilAsync(condition, token); }
        catch { Observe("failed"); throw; }
        Observe("satisfied");
    }

    private static object PresentationState(FrameworkElement root) => new
    {
        root.Name, root.ActualWidth, root.ActualHeight, root.IsLoaded,
        xamlOpacity = root.Opacity,
        compositionOpacity = ElementCompositionPreview.GetElementVisual(root).Opacity,
        visibility = root.Visibility.ToString(),
    };

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Invoke(FrameworkElement element)
    {
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(element)
            ?? throw new InvalidOperationException("The real control has no automation peer.");
        (peer.GetPattern(PatternInterface.Invoke) as IInvokeProvider
            ?? throw new InvalidOperationException("The real control has no Invoke pattern.")).Invoke();
    }

    private static object Bounds(FrameworkElement element, UIElement relativeTo)
    {
        var origin = element.TransformToVisual(relativeTo).TransformPoint(new Point(0, 0));
        return new { origin.X, origin.Y, width = element.ActualWidth, height = element.ActualHeight,
            visibility = element.Visibility.ToString() };
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task CaptureAsync(FrameworkElement root, string name,
        BetaUiDiagnosticReceipt receipt, CancellationToken token)
    {
        try
        {
            var previousWidth = double.NaN;
            var previousHeight = double.NaN;
            var stable = 0;
            await WaitAtStageAsync(receipt, "capture-" + name + "-layout", () =>
            {
                root.UpdateLayout();
                stable = root.ActualWidth > 0 && root.ActualHeight > 0 &&
                    Math.Abs(root.ActualWidth - previousWidth) < .1 && Math.Abs(root.ActualHeight - previousHeight) < .1 ? stable + 1 : 0;
                previousWidth = root.ActualWidth;
                previousHeight = root.ActualHeight;
                return stable >= 4;
            }, token, () => PresentationState(root));
            var bitmap = new RenderTargetBitmap();
            Stage(receipt, "capture-" + name + "-render");
            await bitmap.RenderAsync(root).AsTask().WaitAsync(TimeSpan.FromSeconds(5), token);
            Stage(receipt, "capture-" + name + "-read-pixels");
            var buffer = await bitmap.GetPixelsAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), token);
            var pixels = new byte[buffer.Length];
            using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
            Require(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0 &&
                pixels.LongLength == (long)bitmap.PixelWidth * bitmap.PixelHeight * 4,
                "Control pixel readback had invalid dimensions or incomplete bytes.");
            var valid = MusicVisualSmokeOptions.HasPixelContent(pixels);
            var opaquePixels = 0;
            var transparentPixels = 0;
            var colors = new HashSet<uint>();
            for (var index = 0; index < pixels.Length; index += 4)
            {
                if (pixels[index + 3] > 240) opaquePixels++;
                if (pixels[index + 3] == 0) transparentPixels++;
                if (colors.Count < 64)
                    colors.Add((uint)(pixels[index] | pixels[index + 1] << 8 | pixels[index + 2] << 16 | pixels[index + 3] << 24));
            }
            Stage(receipt, "capture-" + name + "-encode");
            using var encoded = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, encoded);
            var scale = root.XamlRoot.RasterizationScale;
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96 * scale, 96 * scale, pixels);
            await encoder.FlushAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5), token);
            using var pngReader = new DataReader(encoded.GetInputStreamAt(0));
            Stage(receipt, "capture-" + name + "-read-png");
            await pngReader.LoadAsync(checked((uint)encoded.Size)).AsTask().WaitAsync(TimeSpan.FromSeconds(5), token);
            var png = new byte[checked((int)encoded.Size)];
            pngReader.ReadBytes(png);
            var file = name + ".png";
            Stage(receipt, "capture-" + name + "-write-png");
            await File.WriteAllBytesAsync(Path.Combine(receipt.Output, file), png, token);
            receipt.Captures.Add(new(file, Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant(),
                bitmap.PixelWidth, bitmap.PixelHeight, scale, valid,
                name.StartsWith("timer-menu-", StringComparison.Ordinal) ? "live-menu-popup-subtree" :
                    name.StartsWith("timers-", StringComparison.Ordinal) ? "live-overlay-surface" : "live-main-page",
                opaquePixels, transparentPixels, pixels.Length / 4 - transparentPixels, colors.Count, root.Opacity,
                ElementCompositionPreview.GetElementVisual(root).Opacity));
            if (!valid) receipt.CaptureFailures.Add(name + ": empty, uniform or insufficiently opaque pixel readback.");
        }
        catch (Exception error) when (error is not OutOfMemoryException && !token.IsCancellationRequested)
        {
            receipt.CaptureFailures.Add(name + ": " + Describe(error));
        }
        WriteEvidence(receipt);
    }

    private static string Describe(Exception error) =>
        error.GetType().Name + " (0x" + error.HResult.ToString("X8") + "): " + error.Message;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}

internal sealed class BetaUiDiagnosticReceipt(string output, BetaUiDiagnosticSelection selection)
{
    public int SchemaVersion { get; } = 1;
    public string Output { get; } = output;
    public string Status { get; set; } = "running";
    public string VisualStatus { get; set; } = "not-run";
    public string Stage { get; set; } = "prepare";
    public string? FailureStage { get; set; }
    public string SelectedScenario { get; } = selection.Name;
    public string[] ExpectedScenarios { get; } = selection.Scenarios;
    public string[] ExpectedCaptures { get; } = selection.Captures;
    public int PlannedCaseCount => ExpectedScenarios.Length;
    public int ActualCaseCount => Scenarios.Count;
    public int PassedCaseCount => Scenarios.Count(scenario => scenario.Status == "passed");
    public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public object? Environment { get; set; }
    public List<BetaUiDiagnosticScenario> Scenarios { get; } = [];
    public List<object> Observations { get; } = [];
    public List<BetaUiDiagnosticCapture> Captures { get; } = [];
    public List<string> CaptureFailures { get; } = [];
    public List<BetaUiSupplementalCapture> SupplementalCaptures { get; } = [];
    public List<BetaUiSupplementalFailure> SupplementalCaptureFailures { get; } = [];
    public List<string> Exceptions { get; } = [];
    public string[] Limitations { get; } = [
        "Only the explicitly selected synthetic scenarios run on the recorded desktop, DPI, text scale and en-US language; unselected cases are not rerun or counted.",
        "The batch scenario uses production MainPage in a diagnostic host Window; the timer scenario uses production OverlayWindow/WidgetsExpandedView HWND and controls.",
        "AutomationPeer Invoke/Toggle/ExpandCollapse and ListView.SelectedItems are programmatic control input, not physical pointer or keyboard coverage.",
        "RenderTargetBitmap PNGs are real control readbacks, not desktop compositor captures; no Acrylic/native-region/occlusion/physical multi-monitor claim.",
        "Overlay composition opacity and Acrylic are not necessarily represented by XAML RenderTargetBitmap; invalid readbacks neither pass visual validation nor prove the on-screen HWND is empty.",
        "Supplemental desktop BitBlt ROI PNGs are separate manual-review evidence. They never alter the RenderTargetBitmap gate, case counts or final status, and their alpha is synthesized for PNG encoding.",
        "Nonblank pixels and geometry require human PNG review; no screenshot comparison or complete visual correctness claim.",
        "The fixture owns a fresh data directory and 2 x 1 timer layout. It does not initialize clipboard capture, startup registration, media discovery, AI, updates or module workers.",
    ];
}

internal sealed class BetaUiDiagnosticScenario(string name)
{
    public string Name { get; } = name;
    public string Status { get; set; } = "running";
    public string? Failure { get; set; }
    public string? FailureStage { get; set; }
}

internal sealed record BetaUiDiagnosticCapture(string File, string Sha256, int PixelWidth,
    int PixelHeight, double RasterizationScale, bool PixelReadbackPassed, string TargetKind,
    int OpaquePixelCount, int TransparentPixelCount, int NonzeroAlphaPixelCount, int DistinctColorCountUpTo64,
    double XamlOpacity, float CompositionOpacity);
