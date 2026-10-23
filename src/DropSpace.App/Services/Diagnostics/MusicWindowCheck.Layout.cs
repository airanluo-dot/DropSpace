using System.Diagnostics;
using DropSpace.App.Services.Media;

namespace DropSpace.App.Services.Diagnostics;

internal static partial class MusicWindowCheck
{
    private sealed record LayoutCheckResult(string Status, string ReceiptPath, int Passed, int Failed, string? Error);

    // A separate check for the two action buttons missed by the portrait ledger.
    // It samples the real window after native resize; source geometry is not evidence.
    private static async Task<LayoutCheckResult> VerifyLayoutActionsAsync(string output, MusicWindowService music)
    {
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Layout checks require a new output directory.");
        Directory.CreateDirectory(output);
        var receipt = Path.Combine(output, "receipt.json");
        var checks = new List<CheckResult>();
        string? error = null;
        try
        {
            music.Open();
            var window = music.CurrentWindow!;
            await music.SetTranslationEnabledAsync(true);
            foreach (var (width, height, lyrics, name) in new[]
            {
                (1040d, 700d, true, "landscape"),
                (600d, 600d, true, "square-boundary"),
                (480d, 800d, false, "portrait-cover"),
            })
            {
                var requested = ResizeMusicDips(window, width, height);
                music.SetLyricsVisible(lyrics);
                window.DiagnosticNotifyInteraction();
                // Allow native resize delivery before waiting on the transition flag.
                await Task.Delay(550);
                await UntilAsync(() => !window.GetState().TransitionRunning, 2_000);
                var sample = CapturePortrait(window, "action-layout-" + name, Stopwatch.StartNew());
                var footer = Rect(sample, "PresentedFooter");
                var translation = Rect(sample, "PresentedTranslation");
                var controls = Rect(sample, "PresentedControls");
                var volume = Rect(sample, "PresentedVolume");
                var overlap = footer[0] < translation[0] + translation[2] && translation[0] < footer[0] + footer[2] &&
                    footer[1] < translation[1] + translation[3] && translation[1] < footer[1] + footer[3];
                var horizontalGap = Math.Max(0, Math.Max(translation[0] - footer[0] - footer[2], footer[0] - translation[0] - translation[2]));
                var verticalGap = Math.Max(0, Math.Max(translation[1] - footer[1] - footer[3], footer[1] - translation[1] - translation[3]));
                var separation = Math.Max(horizontalGap, verticalGap);
                var rowDelta = Math.Abs(footer[1] - translation[1]);
                var footerCenterDelta = Math.Abs(footer[0] + footer[2] / 2 - controls[0] - controls[2] / 4);
                var translationCenterDelta = Math.Abs(translation[0] + translation[2] / 2 - controls[0] - controls[2] * 3 / 4);
                var insideControls = new[] { footer, translation }.All(button => button[0] >= controls[0] - 1 &&
                    button[0] + button[2] <= controls[0] + controls[2] + 1 && button[1] >= controls[1] - 1 &&
                    button[1] + button[3] <= controls[1] + controls[3] + 1);
                var belowVolume = new[] { footer, translation }.All(button => volume[1] + volume[3] + 4 <= button[1]);
                var passed = footer.Concat(translation).Concat(controls).Concat(volume).All(double.IsFinite) && footer[2] > 0 && footer[3] > 0 &&
                    translation[2] > 0 && translation[3] > 0 && !sample.State.TransitionRunning &&
                    controls[2] > 0 && controls[3] > 0 && volume[2] > 0 && volume[3] > 0 &&
                    sample.State.LyricsVisible == lyrics && sample.State.ControlsVisible && sample.State.TranslationEnabled &&
                    sample.Layout.GetProperty("ActualTranslationOpacity").GetDouble() > 0.98 &&
                    sample.Layout.GetProperty("ActualFooterOpacity").GetDouble() > 0.98 &&
                    sample.Layout.GetProperty("ActualControlsOpacity").GetDouble() > 0.98 &&
                    !overlap && horizontalGap >= 12 && rowDelta < 1 && footerCenterDelta < 3 && translationCenterDelta < 3 &&
                    insideControls && belowVolume && (name != "landscape" || !sample.State.Portrait) &&
                    (name != "portrait-cover" || sample.State.Portrait);
                checks.Add(new(passed ? "passed" : "failed", "native-action-layout",
                    $"{width:0} x {height:0} DIP ({name}): actual lyrics and translation buttons share one row below volume, centered at control-area quarters with at least 12 DIP gap",
                    new { requested, lyrics, actualWidthPixels = window.AppWindow.Size.Width, actualHeightPixels = window.AppWindow.Size.Height,
                        overlap, horizontalGap, verticalGap, axisSeparationDip = separation, rowDelta, footerCenterDelta,
                        translationCenterDelta, insideControls, belowVolume, sample }));
            }
            ResizeMusicDips(window, 1040, 700);
            music.SetLyricsVisible(true);
            window.DiagnosticNotifyInteraction();
            await Task.Delay(550);
            await UntilAsync(() => !window.GetState().TransitionRunning, 2_000);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            error = exception.ToString();
            checks.Add(new("failed", "native-action-layout", "Targeted action layout verification could not finish", error));
        }
        var result = new LayoutCheckResult(checks.Any(check => check.Status == "failed") ? "failed" : "passed", receipt,
            checks.Count(check => check.Status == "passed"), checks.Count(check => check.Status == "failed"), error);
        await WriteAsync(receipt, new
        {
            result.Status, result.ReceiptPath, result.Passed, result.Failed, result.Error,
            syntheticMedia = true, nativeWindow = true, baselineRepeated = false, portraitLedgerRepeated = false,
            evidenceBoundary = "Actual XAML element rectangles and opacity after native resize and transition settling. This checks action alignment at three requested sizes including portrait cover mode; it does not establish other layout sizes, physical input, touch scrolling or pixel-level appearance.",
            checks,
        });
        return result;
    }
}
