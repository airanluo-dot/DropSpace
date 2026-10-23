using System.Diagnostics;
using System.Text.Json;
using DropSpace.App.Services.Media;
using DropSpace.App.ViewModels;
using DropSpace.App.Views.Music;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace DropSpace.App.Services.Diagnostics;

internal static partial class MusicWindowCheck
{
    private sealed record PortraitResult(string Status, string ReceiptPath, int Passed, int Failed, string? Error);
    private sealed record PortraitSample(string Phase, double Milliseconds, DateTimeOffset CapturedAt,
        MusicWindowState State, JsonElement Layout);

    // Resize requests are DIPs. Receipts retain the effective rasterization scale,
    // native pixel dimensions and actual XAML geometry so DPI is never assumed.
    private static object ResizeMusicDips(MusicWindow window, double width, double height)
    {
        var scale = (window.Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? 1;
        window.AppWindow.Resize(new SizeInt32((int)Math.Round(width * scale), (int)Math.Round(height * scale)));
        return new { widthDip = width, heightDip = height, scale,
            actualWidthPixels = window.AppWindow.Size.Width, actualHeightPixels = window.AppWindow.Size.Height };
    }

    private static string ProbeOutput(string output, string name, JsonElement command)
    {
        if (!command.TryGetProperty("outputSuffix", out var property)) return Path.Combine(output, name);
        if (property.ValueKind != JsonValueKind.String || property.GetString() is not { Length: > 0 and <= 48 } suffix ||
            suffix.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
            throw new ArgumentException("outputSuffix must contain 1 to 48 ASCII letters, numbers or hyphens.");
        return Path.Combine(output, name + "-" + suffix);
    }

    private static PortraitSample CapturePortrait(MusicWindow window, string phase, Stopwatch elapsed)
    {
        // The window's frame renderer writes the interpolated geometry to its real
        // Canvas elements and XAML Opacity. LayoutSnapshot reads their actual
        // TransformToVisual/ActualWidth/ActualHeight after UpdateLayout; it does
        // not expose destination values from a Composition animation.
        var layout = JsonSerializer.SerializeToElement(window.LayoutSnapshot);
        return new(phase, elapsed.Elapsed.TotalMilliseconds, DateTimeOffset.UtcNow, window.GetState(), layout);
    }

    private static async Task<PortraitResult> VerifyPortraitAsync(string output, MusicWindowService music, MediaViewModel media)
    {
        if (Directory.Exists(output) || File.Exists(output)) throw new IOException("Portrait checks require a new output directory.");
        Directory.CreateDirectory(output);
        var receipt = Path.Combine(output, "receipt.json");
        var checks = new List<CheckResult>();
        var samples = new List<PortraitSample>();
        var frameSamples = new List<PortraitSample>();
        var sizes = new List<object>();
        string? error = null;
        string? frameSamplingError = null;
        EventHandler<object>? frameObserver = null;
        void Check(bool condition, string description, object? evidence = null) =>
            checks.Add(new(condition ? "passed" : "failed", "native-portrait-actual-layout", description, evidence));
        try
        {
            music.Open();
            var window = music.CurrentWindow!;
            var initialTrack = media.Session.TrackIdentity;
            var originalArtwork = media.Artwork;
            sizes.Add(ResizeMusicDips(window, 480, 800));
            music.SetLyricsVisible(false);
            window.DiagnosticNotifyInteraction();
            await UntilAsync(() => window.GetState().Portrait && !window.GetState().TransitionRunning, 3_000);
            var coverMode = CapturePortrait(window, "portrait-cover-settled", Stopwatch.StartNew());
            samples.Add(coverMode);
            Check(coverMode.State.Portrait && !coverMode.State.LyricsVisible &&
                coverMode.Layout.GetProperty("PresentedCover").GetProperty("Width").GetDouble() > 100,
                "Actual native resize enters portrait cover mode with a rendered cover", new { resize = sizes[^1], sample = coverMode });
            if (!coverMode.State.Portrait) throw new InvalidOperationException("The requested native portrait size did not enter portrait layout.");

            async Task<PortraitSample[]> TransitionAsync(string name, bool lyrics)
            {
                window.DiagnosticNotifyInteraction();
                var clock = Stopwatch.StartNew();
                var observations = new List<PortraitSample> { CapturePortrait(window, name + "-before", clock) };
                music.SetLyricsVisible(lyrics);
                observations.Add(CapturePortrait(window, name + "-0", clock));
                foreach (var target in new[] { 100, 220 })
                {
                    var delay = target - clock.Elapsed.TotalMilliseconds;
                    if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay));
                    observations.Add(CapturePortrait(window, name + "-" + target, clock));
                }
                await UntilAsync(() => !window.GetState().TransitionRunning, 2_000);
                observations.Add(CapturePortrait(window, name + "-settled", clock));
                samples.AddRange(observations);
                return observations.ToArray();
            }

            var enter = await TransitionAsync("enter-lyrics", true);
            Check(enter[^1].State.LyricsVisible && !enter[^1].State.TransitionRunning &&
                enter[^1].Layout.GetProperty("ActualLyricsOpacity").GetDouble() > 0.98,
                "Portrait lyrics entry settles with fully visible lyrics", enter[^1]);
            Check(SameElements(enter), "Portrait entry retains the same cover, lyrics, metadata and controls elements", ElementIdentities(enter[0]));
            Check(HasActualIntermediateGeometry(enter, "PresentedCover") && ContinuousGeometry(enter, "PresentedCover") &&
                ContinuousGeometry(enter, "PresentedMetadata") && ContinuousGeometry(enter, "PresentedControls"),
                "Sampled cover, metadata and transport geometry progresses through actual intermediate positions", enter);
            Check(IntermediateOpacity(enter, "ActualLyricsOpacity"),
                "Entering lyrics has an actual XAML opacity between zero and one during the sampled transition", enter);

            var exit = await TransitionAsync("exit-lyrics", false);
            var exitMiddle = exit.Skip(2).Take(2).ToArray();
            Check(exitMiddle.Any(sample => sample.State.TransitionRunning &&
                    sample.Layout.GetProperty("ActualLyricsOpacity").GetDouble() is > 0.01 and < 0.99 && LyricsActuallyVisible(sample)),
                "Lyrics remain in the visual tree with intermediate actual opacity while exiting", exitMiddle);
            Check(!exit[^1].State.LyricsVisible && !exit[^1].State.TransitionRunning &&
                exit[^1].Layout.GetProperty("ActualLyricsOpacity").GetDouble() < 0.02 && !LyricsActuallyVisible(exit[^1]),
                "The lyric panel collapses only after its exit fade settles", exit[^1]);
            Check(SameElements(exit) && HasActualIntermediateGeometry(exit, "PresentedCover") && ContinuousGeometry(exit, "PresentedCover"),
                "Exiting lyrics moves the existing cover through intermediate geometry", exit);

            // Record each delivered native rendering callback during interruption,
            // rather than trusting transition targets or a final screenshot alone.
            var rapidClock = Stopwatch.StartNew();
            frameObserver = (_, _) =>
            {
                try
                {
                    if (frameSamplingError is null && frameSamples.Count < 240)
                        frameSamples.Add(CapturePortrait(window, "rapid-render-frame", rapidClock));
                }
                catch (Exception exception) when (exception is not OutOfMemoryException) { frameSamplingError ??= exception.ToString(); }
            };
            CompositionTarget.Rendering += frameObserver;
            var jumps = new List<object>();
            var rapidControls = new List<PortraitSample>();
            var generationBefore = window.GetState().TransitionGeneration;
            for (var index = 0; index < 6; index++)
            {
                var before = CapturePortrait(window, "rapid-before-" + index, rapidClock);
                music.SetLyricsVisible(index % 2 == 0);
                var after = CapturePortrait(window, "rapid-after-" + index, rapidClock);
                rapidControls.Add(before); rapidControls.Add(after);
                jumps.Add(new { index, elapsedMilliseconds = after.Milliseconds - before.Milliseconds, coverJump = RectDistance(before, after, "PresentedCover"),
                    metadataJump = RectDistance(before, after, "PresentedMetadata"),
                    opacityJump = Math.Abs(before.Layout.GetProperty("ActualLyricsOpacity").GetDouble() - after.Layout.GetProperty("ActualLyricsOpacity").GetDouble()) });
                await Task.Delay(70);
            }
            await UntilAsync(() => !window.GetState().TransitionRunning, 2_000);
            CompositionTarget.Rendering -= frameObserver; frameObserver = null;
            var rapidEnd = CapturePortrait(window, "rapid-settled", rapidClock);
            samples.AddRange(rapidControls); samples.Add(rapidEnd);
            Check(rapidEnd.State.TransitionGeneration >= generationBefore + 6 && !rapidEnd.State.TransitionRunning && !rapidEnd.State.LyricsVisible,
                "Rapid toggles retire older transition generations and settle on the last requested state", rapidEnd);
            Check(rapidControls.Chunk(2).All(pair =>
                RectDistance(pair[0], pair[1], "PresentedCover") < 4 + Math.Max(0, pair[1].Milliseconds - pair[0].Milliseconds) * 4 &&
                Math.Abs(pair[0].Layout.GetProperty("ActualLyricsOpacity").GetDouble() - pair[1].Layout.GetProperty("ActualLyricsOpacity").GetDouble()) <
                    0.03 + Math.Max(0, pair[1].Milliseconds - pair[0].Milliseconds) * 0.007),
                "Retargeting starts from the current cover rectangle and opacity without an immediate sampled jump", jumps);
            Check(frameSamplingError is null && frameSamples.Count >= 4 && SameElements(frameSamples) && frameSamples.All(sample =>
                sample.Layout.GetProperty("PresentedCover").GetProperty("Width").GetDouble() > 0 &&
                (sample.Layout.GetProperty("ActualLyricsOpacity").GetDouble() <= 0.01 || LyricsActuallyVisible(sample))),
                "Observed native render frames retain element identities and do not collapse nontransparent lyrics during rapid toggles",
                new { observedFrames = frameSamples.Count, frameSamplingError, first = frameSamples.FirstOrDefault(), last = frameSamples.LastOrDefault() });

            music.SetLyricsVisible(true);
            await UntilAsync(() => !window.GetState().TransitionRunning, 2_000);
            window.DiagnosticNotifyInteraction();
            var idleClock = Stopwatch.StartNew();
            await Task.Delay(4_500);
            await UntilAsync(() => !window.GetState().ControlsVisible &&
                JsonSerializer.SerializeToElement(window.LayoutSnapshot).GetProperty("ActualControlsOpacity").GetDouble() < 0.02, 1_500);
            var idle = CapturePortrait(window, "idle-hidden", idleClock);
            samples.Add(idle);
            Check(!idle.State.ControlsVisible && idle.Layout.GetProperty("ActualControlsOpacity").GetDouble() < 0.02,
                "After at least four seconds without injected interaction, portrait lyrics controls fade out", idle);
            var wakeClock = Stopwatch.StartNew();
            window.DiagnosticNotifyInteraction();
            samples.Add(CapturePortrait(window, "wake-0", wakeClock));
            await Task.Delay(120);
            samples.Add(CapturePortrait(window, "wake-120", wakeClock));
            await UntilAsync(() => JsonSerializer.SerializeToElement(window.LayoutSnapshot).GetProperty("ActualControlsOpacity").GetDouble() > 0.98, 1_500);
            var awake = CapturePortrait(window, "wake-settled", wakeClock);
            samples.Add(awake);
            Check(awake.State.ControlsVisible && awake.Layout.GetProperty("ActualControlsOpacity").GetDouble() > 0.98,
                "The real interaction callback restores the controls; this is programmatic callback verification", awake);

            sizes.Add(ResizeMusicDips(window, 1040, 700));
            window.DiagnosticNotifyInteraction();
            await UntilAsync(() => !window.GetState().Portrait && !window.GetState().TransitionRunning, 3_000);
            var landscape = CapturePortrait(window, "landscape-restored", Stopwatch.StartNew());
            samples.Add(landscape);
            Check(!landscape.State.Portrait && ReferenceEquals(window, music.CurrentWindow) && media.Session.TrackIdentity == initialTrack &&
                ReferenceEquals(originalArtwork, media.Artwork) && landscape.State.LyricsVisible,
                "Returning to landscape keeps the same native window, media identity, artwork object and explicit lyric choice", landscape);

            // One focused boundary case covers the new Canvas sizing risk. This is
            // the actual music window's native minimum, not the old main-window test.
            window.AppWindow.Resize(new SizeInt32(100, 100));
            window.DiagnosticNotifyInteraction();
            await Task.Delay(550);
            await UntilAsync(() => !window.GetState().TransitionRunning, 2_000);
            var minimum = CapturePortrait(window, "native-music-minimum", Stopwatch.StartNew());
            samples.Add(minimum);
            var minimumPresenter = (OverlappedPresenter)window.AppWindow.Presenter;
            var controlsBounds = Rect(minimum, "PresentedControls");
            var footerBounds = Rect(minimum, "PresentedFooter");
            var lyricsBounds = Rect(minimum, "PresentedLyrics");
            var translationBounds = Rect(minimum, "PresentedTranslation");
            var volumeBounds = Rect(minimum, "PresentedVolume");
            var minimumEvidence = new
            {
                requestedWidthPixels = 100, requestedHeightPixels = 100,
                actualWidthPixels = window.AppWindow.Size.Width, actualHeightPixels = window.AppWindow.Size.Height,
                minimumPresenter.PreferredMinimumWidth, minimumPresenter.PreferredMinimumHeight,
                sample = minimum,
            };
            sizes.Add(minimumEvidence);
            Check(Convert.ToInt32(minimumPresenter.PreferredMinimumWidth) > 0 && Convert.ToInt32(minimumPresenter.PreferredMinimumHeight) > 0 &&
                window.AppWindow.Size.Width >= Convert.ToInt32(minimumPresenter.PreferredMinimumWidth) &&
                window.AppWindow.Size.Height >= Convert.ToInt32(minimumPresenter.PreferredMinimumHeight) &&
                minimum.State.Portrait && minimum.State.LyricsVisible && !minimum.State.TransitionRunning &&
                controlsBounds[2] > 0 && controlsBounds[3] > 0 && footerBounds[2] > 0 && footerBounds[3] > 0 &&
                lyricsBounds[2] > 0 && lyricsBounds[3] > 0 && translationBounds[2] > 0 && translationBounds[3] > 0 &&
                volumeBounds[2] > 0 && volumeBounds[3] > 0 &&
                lyricsBounds[1] + lyricsBounds[3] <= controlsBounds[1] + 1 &&
                new[] { footerBounds, translationBounds }.All(button =>
                    button[0] >= controlsBounds[0] - 1 && button[0] + button[2] <= controlsBounds[0] + controlsBounds[2] + 1 &&
                    button[1] >= controlsBounds[1] && button[1] + button[3] <= controlsBounds[1] + controlsBounds[3] + 1 &&
                    volumeBounds[1] + volumeBounds[3] + 4 <= button[1]),
                "Native undersizing clamps to the music minimum; lyrics end above controls and both action buttons remain inside controls below volume", minimumEvidence);
            sizes.Add(ResizeMusicDips(window, 1040, 700));
            window.DiagnosticNotifyInteraction();
            await UntilAsync(() => !window.GetState().Portrait && !window.GetState().TransitionRunning, 3_000);
            samples.Add(CapturePortrait(window, "landscape-after-minimum", Stopwatch.StartNew()));

            // The visibility guard changed with this layout, so exercise one
            // interruption/lifetime case without repeating hotkeys or volume.
            music.SetLyricsVisible(false);
            await Task.Delay(100);
            var interruptedTransition = CapturePortrait(window, "before-transition-minimize", Stopwatch.StartNew());
            samples.Add(interruptedTransition);
            window.SetMinimized(true);
            await Task.Delay(100);
            var minimizedStart = window.GetState();
            await Task.Delay(300);
            var minimizedEnd = window.GetState();
            music.Open();
            await UntilAsync(() => !window.GetState().IsMinimized && window.GetState().FrameCount > minimizedEnd.FrameCount, 3_000);
            var restored = window.GetState();
            Check(interruptedTransition.State.TransitionRunning && minimizedStart.IsMinimized && minimizedEnd.IsMinimized &&
                !minimizedStart.TransitionRunning && !minimizedEnd.TransitionRunning &&
                !minimizedStart.IsRendering && !minimizedEnd.IsRendering &&
                !minimizedStart.AudioLeaseActive && !minimizedEnd.AudioLeaseActive &&
                minimizedStart.FrameCount > 0 && minimizedStart.FrameCount == minimizedEnd.FrameCount &&
                ReferenceEquals(window, music.CurrentWindow) && !restored.IsMinimized && restored.BackgroundReady &&
                restored.IsRendering && restored.AudioLeaseActive && restored.FrameCount > minimizedEnd.FrameCount,
                "Minimizing during a real transition stops animation, GPU frames and its audio lease; explicit reopen resumes the same window",
                new { interruptedTransition, minimizedStart, minimizedEnd, restored });
            music.SetLyricsVisible(true);
            window.DiagnosticNotifyInteraction();
            await UntilAsync(() => !window.GetState().Portrait && !window.GetState().TransitionRunning, 3_000);
            samples.Add(CapturePortrait(window, "landscape-ready-for-screenshot", Stopwatch.StartNew()));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            error = exception.ToString();
            Check(false, "Portrait verification could not finish", error);
        }
        finally { if (frameObserver is not null) CompositionTarget.Rendering -= frameObserver; }
        var result = new PortraitResult(checks.Any(check => check.Status == "failed") ? "failed" : "passed", receipt,
            checks.Count(check => check.Status == "passed"), checks.Count(check => check.Status == "failed"), error);
        await WriteAsync(receipt, new
        {
            result.Status, result.ReceiptPath, result.Passed, result.Failed, result.Error,
            syntheticMedia = true, nativeWindow = true, baselineRepeated = false,
            evidenceBoundary = "Actual XAML element rectangles and opacity sampled from native render callbacks; not Composition target values, pixel-difference analysis, physical pointer input or real-player playback.",
            sizes, checks, samples, frameSamples,
        });
        return result;
    }

    private static bool LyricsActuallyVisible(PortraitSample sample)
    {
        var value = sample.Layout.GetProperty("ActualLyricsVisibility");
        return value.ValueKind == JsonValueKind.String ? value.GetString() == "Visible" : value.GetInt32() == 0;
    }

    private static object ElementIdentities(PortraitSample sample) => new
    {
        Cover = sample.Layout.GetProperty("CoverIdentity"), Lyrics = sample.Layout.GetProperty("LyricsIdentity"),
        Metadata = sample.Layout.GetProperty("MetadataIdentity"), Controls = sample.Layout.GetProperty("ControlsIdentity"),
    };

    private static bool SameElements(IEnumerable<PortraitSample> values)
    {
        var samples = values.ToArray();
        return samples.Length > 0 && new[] { "CoverIdentity", "LyricsIdentity", "MetadataIdentity", "ControlsIdentity" }.All(key =>
            samples.All(sample => sample.Layout.GetProperty(key).GetRawText() == samples[0].Layout.GetProperty(key).GetRawText()));
    }

    private static double[] Rect(PortraitSample sample, string key)
    {
        var rectangle = sample.Layout.GetProperty(key);
        return [rectangle.GetProperty("X").GetDouble(), rectangle.GetProperty("Y").GetDouble(),
            rectangle.GetProperty("Width").GetDouble(), rectangle.GetProperty("Height").GetDouble()];
    }
    private static double RectDistance(PortraitSample left, PortraitSample right, string key) =>
        Rect(left, key).Zip(Rect(right, key), (a, b) => Math.Abs(a - b)).Max();
    private static bool HasActualIntermediateGeometry(IReadOnlyList<PortraitSample> samples, string key) =>
        RectDistance(samples[0], samples[^1], key) > 6 && samples.Skip(2).Take(2).Any(sample =>
            RectDistance(samples[0], sample, key) > 0.5 && RectDistance(samples[^1], sample, key) > 0.5);
    private static bool ContinuousGeometry(IReadOnlyList<PortraitSample> samples, string key)
    {
        var start = Rect(samples[0], key); var end = Rect(samples[^1], key);
        return samples.All(sample => Rect(sample, key).Select((value, index) =>
            double.IsFinite(value) && value >= Math.Min(start[index], end[index]) - 3 && value <= Math.Max(start[index], end[index]) + 3).All(valid => valid));
    }
    private static bool IntermediateOpacity(IEnumerable<PortraitSample> samples, string key) =>
        samples.Any(sample => sample.Layout.GetProperty(key).GetDouble() is > 0.01 and < 0.99);
}
