using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using DropSpace.App.Services.Media;
using DropSpace.App.ViewModels;
using DropSpace.App.Views.Island;
using DropSpace.App.Views.Music;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Island;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Media;
using DropSpace.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI.ViewManagement;

namespace DropSpace.App.Services.Diagnostics;

/// <summary>Real WinUI control pixels with synthetic data; no native media/clipboard runtime is started.</summary>
internal static class MusicVisualSmoke
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string[] Cases = ["normal"];

    internal static bool IsRequested(IReadOnlyList<string> arguments) =>
        arguments.Contains(MusicVisualSmokeOptions.Switch, StringComparer.OrdinalIgnoreCase);

    internal static async Task<int> RunAsync(IReadOnlyList<string> arguments, Func<ServiceProvider> createServices)
    {
        try { return await RunCoreAsync(arguments, createServices); }
        catch (Exception error)
        {
            // Never escape into App's ordinary crash marker, which targets the user's normal data directory.
            Debug.WriteLine($"Visual diagnostic failed: {error.GetType().Name} (0x{error.HResult:X8})");
            return 1;
        }
    }

    private static async Task<int> RunCoreAsync(IReadOnlyList<string> arguments, Func<ServiceProvider> createServices)
    {
        MusicVisualSmokeOptions options;
        try
        {
            options = MusicVisualSmokeOptions.Parse(arguments,
                Environment.GetEnvironmentVariable("DROPSPACE_TEST_DATA_ROOT"), Path.GetTempPath());
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Visual diagnostic rejected: {error.GetType().Name}");
            return 3; // Never construct production services or write to an unvalidated destination.
        }

        Directory.CreateDirectory(options.Root);
        var output = Path.Combine(options.Root, "captures");
        Directory.CreateDirectory(output);
        var captures = new List<CaptureEvidence>();
        var lyricsLayoutChecks = new List<LyricsLayoutEvidence>();
        var compactRenderChecks = new List<CompactRenderEvidence>();
        var expandedRenderChecks = new List<ExpandedRenderEvidence>();
        var uiErrors = new List<string>();
        var status = "failed";
        var stage = "graphical-session";
        string? failure = null;
        Window? host = null;
        var desktop = OpenInputDesktop(0, false, 1);
        var interactiveDesktop = desktop != 0;
        if (desktop != 0) CloseDesktop(desktop);
        using var process = Process.GetCurrentProcess();
        void OnUnhandled(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
        {
            uiErrors.Add($"{args.Exception.GetType().Name} (0x{args.Exception.HResult:X8})");
            args.Handled = true;
        }
        Application.Current.UnhandledException += OnUnhandled;
        try
        {
            if (!interactiveDesktop || GetSystemMetrics(0) <= 0 || GetSystemMetrics(1) <= 0)
            {
                status = "blocked";
                failure = "No usable interactive Windows desktop is available; no pixel validation was performed.";
                return 2;
            }
            stage = "construct-isolated-controls";
            await using var services = createServices();
            var language = services.GetRequiredService<AppLanguageService>();
            AppLanguageService.TryParseSupportedLanguage(options.Language, out var preference);
            language.Apply(preference);
            var strings = services.GetRequiredService<IAppStringLocalizer>();
            XamlResourceOverride.Initialize(strings);
            using var enhancement = new NeteaseEnhancementViewModel(new SyntheticEnhancement(), strings, DispatcherQueue.GetForCurrentThread());
            var sessions = services.GetRequiredService<WindowsMediaSessionService>();
            var experience = services.GetRequiredService<MediaExperienceService>();
            // These instances are deliberately NEVER initialized/enabled. The page only needs
            // their inert presentation dependencies. The refresh action is inspected, not invoked.
            if (sessions.IsAvailable || sessions.AvailableSources.Count != 0)
                throw new InvalidOperationException("The visual fixture unexpectedly started native media discovery.");
            // Keep one window alive for the whole run. Closing the last WinUI window between
            // captures can terminate the process before the remaining evidence is written.
            host = new Window { Title = "DropSpace synthetic visual diagnostic" };
            stage = "compact-render-lifecycle";
            compactRenderChecks.Add(await CheckCompactRenderLifecycleAsync(host, strings));
            stage = "expanded-render-lifecycle";
            expandedRenderChecks.Add(await CheckExpandedRenderLifecycleAsync(host, strings));
            stage = "lyrics-layout-regressions";
            await CheckLyricsLayoutsAsync(host, strings, lyricsLayoutChecks);
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            foreach (var scenario in Cases)
            {
                var media = CreateFixture(strings, scenario);
                var expanded = new ExpandedIslandMusicView { ViewModel = media, Margin = new Thickness(18, 0, 18, 0) };
                var islandRoot = CreateRoot(theme, 560, 340);
                islandRoot.Children.Add(expanded);
                stage = $"expanded-{theme}-{scenario}";
                captures.Add(await CaptureAsync(host, islandRoot, expanded, media, strings, output, stage, "expanded-island-component", scenario));

                var page = new MusicPage(services.GetRequiredService<NativeSettingsEditor>(), media, sessions,
                    experience, services.GetRequiredService<MediaApplicationIconService>(), strings, 0, enhancement,
                    services.GetRequiredService<Services.Dlc.DlcManagerService>(), () => { });
                var pageRoot = CreateRoot(theme, 980, 680);
                pageRoot.Children.Add(page);
                stage = $"music-page-{theme}-{scenario}";
                captures.Add(await CaptureAsync(host, pageRoot, page, media, strings, output, stage, "music-page-component", scenario));
            }
            if (sessions.IsAvailable || sessions.AvailableSources.Count != 0)
                throw new InvalidOperationException("Native media discovery was activated during the fixture.");
            status = uiErrors.Count == 0 && captures.All(capture => capture.Failures.Count == 0) &&
                lyricsLayoutChecks.All(check => check.Failures.Count == 0) &&
                compactRenderChecks.All(check => check.Failures.Count == 0) &&
                expandedRenderChecks.All(check => check.Failures.Count == 0) ? "passed" : "failed";
            stage = "complete";
            return status == "passed" ? 0 : 1;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            status = "failed";
            failure = $"{error.GetType().Name} (0x{error.HResult:X8}): {error.Message}";
            return 1;
        }
        finally
        {
            Application.Current.UnhandledException -= OnUnhandled;
            await File.WriteAllTextAsync(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, status, stage, failure, language = options.Language,
                commit = Environment.GetEnvironmentVariable("GITHUB_SHA") ?? "local-unverified",
                processId = Environment.ProcessId, sessionId = process.SessionId,
                userInteractive = Environment.UserInteractive, interactiveDesktop,
                os = Environment.OSVersion.VersionString,
                capturedAtUtc = DateTimeOffset.UtcNow,
                evidenceKind = "native-winui-control-render-target-bitmap",
                dataSource = "synthetic-only", captures, lyricsLayoutChecks, compactRenderChecks, expandedRenderChecks, uiErrors,
                limitations = new[]
                {
                    "Component captures use real controls in a diagnostic host, not the production overlay HWND or main-window shell.",
                    "Only the recorded actual DPI and system text scale were observed; no 100/150/200-percent matrix is implied.",
                    "No Acrylic, native-region, game/fullscreen, physical mixed-DPI, Apple Music, or soft-restart recovery claim.",
                    "Passing geometry and nonblank readback still require human visual review of the PNGs."
                },
            }, JsonOptions));
            host?.Close();
        }
    }

    private static Grid CreateRoot(ElementTheme theme, double width, double height) => new()
    {
        Width = width, Height = height, RequestedTheme = theme,
        Background = new SolidColorBrush(theme == ElementTheme.Dark ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White),
    };

    private static async Task<CompactRenderEvidence> CheckCompactRenderLifecycleAsync(Window window, IAppStringLocalizer strings)
    {
        var failures = new List<string>();
        var media = CreateFixture(strings, "normal");
        var compact = new MediaCompactView { ViewModel = media };
        var root = CreateRoot(ElementTheme.Dark, 560, 180);
        root.Children.Add(compact);
        window.Content = root;
        window.Activate();
        await WaitForLayoutAsync(root);

        var observedRefreshNotifications = 0;
        void ObserveNotification(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName is nameof(MediaViewModel.Position) or nameof(MediaViewModel.Lyrics))
                observedRefreshNotifications++;
        }
        media.PropertyChanged += ObserveNotification;
        var before = compact.RefreshCount;
        for (var index = 0; index < 1000; index++)
        {
            media.Position += TimeSpan.FromMilliseconds(1);
            media.Lyrics = media.Lyrics with { WordProgress = (index + 1) / 1000d };
        }
        media.PropertyChanged -= ObserveNotification;
        if (compact.RefreshCount != before) failures.Add("A media notification performed synchronous presentation work.");
        await WaitForPresentationFrameAsync(root);
        var burstRefreshes = compact.RefreshCount - before;
        if (burstRefreshes != 1) failures.Add("The notification burst did not render exactly once.");

        compact.SetActive(false);
        before = compact.RefreshCount;
        for (var index = 0; index < 1000; index++) media.Position += TimeSpan.FromMilliseconds(1);
        await WaitForPresentationFrameAsync(root);
        var hiddenRefreshes = compact.RefreshCount - before;
        if (hiddenRefreshes != 0) failures.Add("A hidden host performed compact presentation work.");

        // Cancel a frame and re-open before CompositionTarget dispatches it. The
        // replacement must own its frame, even if a retired callback was captured.
        compact.SetActive(true);
        compact.SetActive(false);
        compact.SetActive(true);
        before = compact.RefreshCount;
        await WaitForPresentationFrameAsync(root);
        var reopenRefreshes = compact.RefreshCount - before;
        if (reopenRefreshes != 1) failures.Add("Rapid hide/re-open lost or duplicated the current frame.");

        compact.SetActive(false);
        var line = new LyricsLine(TimeSpan.Zero, TimeSpan.FromMinutes(1),
            string.Join(" ", Enumerable.Repeat("Latest hidden lyric", 15)), null, []);
        media.Session = media.Session with { TrackTitle = "Latest hidden synthetic song" };
        media.SetLyricsDocument(new([line], LyricsProviderKind.LocalLrc));
        media.Lyrics = new(line, -1, 0, 1);
        before = compact.RefreshCount;
        compact.RefreshForPresentation();
        var preparedRefreshes = compact.RefreshCount - before;
        var primary = Descendants(compact).OfType<TextBlock>().Single(element => element.Name == "BaseLine");
        if (preparedRefreshes != 1 || primary.Text != LyricsDisplayPolicy.CompactText(media.CurrentLyricText) ||
            compact.IdealIslandWidth <= 0 || compact.IdealIslandHeight <= 0)
            failures.Add("First-frame geometry did not synchronously consume the latest hidden track.");
        compact.RefreshForPresentation();
        if (compact.RefreshCount != before + preparedRefreshes) failures.Add("Unchanged geometry repeated presentation work.");
        await WaitForPresentationFrameAsync(root);
        if (compact.RefreshCount != before + preparedRefreshes) failures.Add("Geometry preparation left a stale frame queued.");
        window.Content = null;
        return new(observedRefreshNotifications, burstRefreshes, hiddenRefreshes, reopenRefreshes, preparedRefreshes, failures);
    }

    private static async Task<ExpandedRenderEvidence> CheckExpandedRenderLifecycleAsync(Window window, IAppStringLocalizer strings)
    {
        var failures = new List<string>();
        var media = CreateFixture(strings, "normal");
        var expanded = new ExpandedIslandMusicView { ViewModel = media };
        var root = CreateRoot(ElementTheme.Dark, 560, 340);
        root.Children.Add(expanded);
        window.Content = root;
        window.Activate();
        await WaitForLayoutAsync(root);
        var progress = Descendants(expanded).OfType<Slider>().Single(element => element.Name == "Progress");
        progress.Value += 10;
        if (!expanded.HasPendingSeekWork) failures.Add("The visible seek fixture did not create pending work.");
        expanded.SetActive(false);
        var seekWorkRetired = !expanded.HasPendingSeekWork;
        progress.Value += 1;
        if (!seekWorkRetired || expanded.HasPendingSeekWork || expanded.IsTranslationActuallyVisible)
            failures.Add("A hidden expanded host retained or restarted seek/translation presentation ownership.");

        var before = expanded.RenderCount;
        for (var index = 0; index < 1000; index++) media.Position += TimeSpan.FromMilliseconds(1);
        await WaitForPresentationFrameAsync(root);
        var hiddenRenders = expanded.RenderCount - before;
        if (hiddenRenders != 0) failures.Add("A hidden expanded host performed presentation work.");
        media.Session = media.Session with { TrackTitle = "Latest hidden expanded song" };
        before = expanded.RenderCount;
        expanded.RefreshForPresentation();
        var preparedRenders = expanded.RenderCount - before;
        if (preparedRenders != 1 || expanded.HasPendingSeekWork)
            failures.Add("First expanded presentation did not consume the current track without stale seek state.");
        expanded.SetActive(true);
        expanded.SetActive(false);
        expanded.SetActive(true);
        before = expanded.RenderCount;
        await WaitForPresentationFrameAsync(root);
        var reopenRenders = expanded.RenderCount - before;
        if (reopenRenders != 1 || expanded.HasPendingSeekWork || Math.Abs(progress.Value - media.PositionSeconds) > .01)
            failures.Add("Rapid expanded hide/re-open lost its frame or retained the old seek preview.");
        window.Content = null;
        return new(hiddenRenders, reopenRenders, preparedRenders, seekWorkRetired, failures);
    }

    private static async Task CheckLyricsLayoutsAsync(Window window, IAppStringLocalizer strings, List<LyricsLayoutEvidence> evidence)
    {
        var selectorFailures = new List<string>();
        var glowMode = new LyricsGlowModeControl(strings);
        var standardSelection = new ComboBox { ItemsSource = new[] { strings.Get("LyricsGlowOff") }, SelectedIndex = 0 };
        var selectors = new StackPanel { Spacing = 8, Children = { glowMode, standardSelection } };
        var selectorRoot = CreateRoot(ElementTheme.Light, 320, 140);
        selectorRoot.Children.Add(selectors);
        window.Content = selectorRoot;
        window.Activate();
        await WaitForLayoutAsync(selectorRoot);
        var nativeSelector = Descendants(glowMode).OfType<ComboBox>().Single();
        if (Descendants(glowMode).OfType<Slider>().Any() || nativeSelector.Items.Count != 3 ||
            nativeSelector.FontSize != standardSelection.FontSize || Math.Abs(nativeSelector.ActualHeight - standardSelection.ActualHeight) > .5)
            selectorFailures.Add("Glow modes did not use the standard native selection dimensions.");
        var changes = 0;
        glowMode.ModeChanged += (_, _) => changes++;
        glowMode.Mode = LyricsGlowMode.Music;
        if (changes != 0 || nativeSelector.SelectedIndex != 2) selectorFailures.Add("Settings synchronization changed selection semantics.");
        nativeSelector.SelectedIndex = 1;
        if (changes != 1 || glowMode.Mode != LyricsGlowMode.AiLyrics) selectorFailures.Add("Native selection did not persist the requested glow mode.");
        evidence.Add(new(standardSelection.FontSize, selectorRoot.XamlRoot.RasterizationScale, false, false, 0, 0, 0,
            selectorFailures, "native-selection"));
        window.Content = null;

        foreach (var fontSize in new[] { 12d, 16d, 17.375, 28d })
        foreach (var longOriginal in new[] { false, true })
        foreach (var longTranslation in new[] { false, true })
        {
            var failures = new List<string>();
            var media = CreateFixture(strings, "normal");
            media.IsReducedMotion = false;
            media.Settings = media.Settings with { Lyrics = media.Settings.Lyrics with { FontSize = fontSize },
                IslandActivity = media.Settings.IslandActivity with { CompactDynamicWidth = false } };
            var original = longOriginal ? string.Join(" ", Enumerable.Repeat("Original lyric", 15)) : "Hi";
            var translation = longTranslation ? string.Join(" ", Enumerable.Repeat("Complete translation", 15)) : "OK";
            var line = new LyricsLine(TimeSpan.Zero, TimeSpan.FromMinutes(1), original, translation, [])
            { TranslationOrigin = LyricsTranslationOrigin.LocalAi, TranslationLanguage = strings.Culture.Name };
            media.SetLyricsDocument(new([line], LyricsProviderKind.LocalLrc));
            media.Lyrics = new(line, -1, 0, 1);
            var compact = new MediaCompactView { ViewModel = media };
            var root = CreateRoot(ElementTheme.Dark, 320, 140);
            root.Children.Add(compact);
            window.Content = root;
            window.Activate();
            await WaitForLayoutAsync(root);
            var descendants = Descendants(compact).OfType<FrameworkElement>().ToArray();
            var secondary = (TextBlock)descendants.Single(element => element.Name == "SecondaryLine");
            var primary = (TextBlock)descendants.Single(element => element.Name == "BaseLine");
            var viewport = (Grid)descendants.Single(element => element.Name == "SecondaryViewport");
            var transform = (CompositeTransform)secondary.RenderTransform;
            var viewportWidth = viewport.ActualWidth;
            if (primary.FontSize != fontSize || secondary.FontSize != fontSize * .875)
                failures.Add("Selected decimal font size or translation ratio was not rendered.");
            if (secondary.Text != "AI · " + translation || secondary.TextTrimming != TextTrimming.None)
                failures.Add("The complete labeled translation was not retained for measurement.");
            if ((secondary.ActualWidth > viewport.ActualWidth) != longTranslation)
                failures.Add("Independent translation measurement did not match its short/long fixture.");
            if (viewport.Clip is not RectangleGeometry clip || Math.Abs(clip.Rect.Width - viewport.ActualWidth) > .01)
                failures.Add("Translation clipping does not match its actual viewport.");
            if (VisualTreeHelper.GetParent(secondary) is not Canvas)
                failures.Add("The full translation must be arranged in an unconstrained canvas before viewport clipping.");
            var arrangedSlot = Microsoft.UI.Xaml.Controls.Primitives.LayoutInformation.GetLayoutSlot(secondary);
            if (arrangedSlot.Width + .5 < secondary.ActualWidth)
                failures.Add("The translation's layout slot clips its tail before the scrolling transform.");
            if (longTranslation)
            {
                var viewportBefore = viewport.TransformToVisual(root).TransformPoint(new Point());
                transform.TranslateX = -(secondary.ActualWidth - viewport.ActualWidth);
                root.UpdateLayout();
                var viewportAfter = viewport.TransformToVisual(root).TransformPoint(new Point());
                var tail = secondary.TransformToVisual(viewport).TransformBounds(
                    new Rect(secondary.ActualWidth - 12, 0, 12, secondary.ActualHeight));
                if (Math.Abs(viewportBefore.X - viewportAfter.X) > .01 || tail.Left < -.5 || tail.Right > viewport.ActualWidth + .5)
                    failures.Add("The viewport moved or the complete translation tail could not enter its visible area.");
                transform.TranslateX = 0;
            }
            media.Position += TimeSpan.FromSeconds(4);
            await WaitForPresentationFrameAsync(root);
            var offset = -transform.TranslateX;
            if ((offset > 0) != longTranslation) failures.Add("Translation marquee depended on original length or did not advance.");
            var width = secondary.ActualWidth;
            media.Settings = media.Settings with { Lyrics = media.Settings.Lyrics with { ShowAiLyricsLabel = false } };
            await WaitForPresentationFrameAsync(root);
            if (secondary.Text != translation || transform.TranslateX != 0 || secondary.ActualWidth >= width)
                failures.Add("AI label toggle failed to remeasure and reset the translation.");
            if (media.LyricPresentation.Line?.TranslationOrigin != LyricsTranslationOrigin.LocalAi)
                failures.Add("AI label toggle changed the displayed translation origin.");
            media.Position += TimeSpan.FromSeconds(4);
            root.Width = 260;
            root.UpdateLayout();
            if (transform.TranslateX != 0) failures.Add("Viewport resize did not restart the readable leading hold.");
            var progressive = line with { Secondary = translation + " appended text" };
            media.SetLyricsDocument(new([progressive], LyricsProviderKind.LocalLrc));
            await WaitForPresentationFrameAsync(root);
            if (secondary.Text != progressive.Secondary || transform.TranslateX != 0)
                failures.Add("Progressive text was not remeasured before the next highlight frame.");
            media.Position += TimeSpan.FromSeconds(4);
            media.IsReducedMotion = true;
            await WaitForPresentationFrameAsync(root);
            if (transform.TranslateX != 0) failures.Add("Reduced motion did not stop the translation marquee.");
            media.IsReducedMotion = false;
            media.Position += TimeSpan.FromSeconds(4);
            media.Settings = media.Settings with { Lyrics = media.Settings.Lyrics with { Scrolling = false } };
            await WaitForPresentationFrameAsync(root);
            if (transform.TranslateX != 0) failures.Add("Disabling scrolling did not restore the full-text leading edge.");
            media.Session = media.Session with { TrackTitle = "Next synthetic song" };
            await WaitForPresentationFrameAsync(root);
            if (secondary.Visibility != Visibility.Collapsed || compact.IsTranslationActuallyVisible)
                failures.Add("A track change retained the previous translation.");
            evidence.Add(new(fontSize, root.XamlRoot.RasterizationScale, longOriginal, longTranslation,
                width, viewportWidth, offset, failures));
            window.Content = null;
        }

        foreach (var fontSize in new[] { 12d, 16d, 17.375, 28d })
        {
            var failures = new List<string>();
            var media = CreateFixture(strings, "normal");
            media.IsReducedMotion = false;
            media.Settings = media.Settings with { Lyrics = media.Settings.Lyrics with { FontSize = fontSize } };
            var source = LyricsParser.Parse(string.Join("\n", Enumerable.Repeat("Original paragraph", 8)), LyricsProviderKind.LocalLrc);
            var line = source.Lines.Single() with { Secondary = string.Join("\r\n", Enumerable.Repeat("Complete translated paragraph", 8)),
                TranslationOrigin = LyricsTranslationOrigin.LocalAi, TranslationLanguage = strings.Culture.Name };
            media.SetLyricsDocument(source with { Lines = [line] });
            media.Lyrics = new(line, -1, 0, 1);
            var compact = new MediaCompactView { ViewModel = media };
            var body = new Border { Width = 300, Height = 100, Child = compact };
            var root = CreateRoot(ElementTheme.Dark, 340, 380);
            root.Children.Add(body);
            window.Content = root;
            await WaitForLayoutAsync(root);
            compact.Height = compact.IdealIslandHeight;
            body.Height = IslandGeometry.ForMusicCompact(compact.IdealIslandWidth, compact.IdealIslandHeight, 1).Height;
            root.UpdateLayout();
            var elements = Descendants(compact).OfType<FrameworkElement>().ToArray();
            var primary = (TextBlock)elements.Single(element => element.Name == "BaseLine");
            var secondary = (TextBlock)elements.Single(element => element.Name == "SecondaryLine");
            var viewport = (Grid)elements.Single(element => element.Name == "SecondaryViewport");
            var transform = (CompositeTransform)secondary.RenderTransform;
            if (primary.Text != LyricsDisplayPolicy.CompactText(line.Text) ||
                secondary.Text != LyricsDisplayPolicy.CompactText("AI · " + line.Secondary))
                failures.Add("Untimed paragraphs were not retained in full single-line presentation.");
            if (Math.Abs(compact.Height - body.Height) > .01 || !compact.IsTranslationVisibleWithin(body))
                failures.Add("Measured compact lyrics exceeded the body or their translation was not visible.");
            media.Position += TimeSpan.FromSeconds(4);
            await WaitForPresentationFrameAsync(root);
            var offset = -transform.TranslateX;
            if (offset <= 0) failures.Add("A multi-line provider translation did not use its independent marquee.");
            transform.TranslateY = body.Height + secondary.ActualHeight;
            if (compact.IsTranslationVisibleWithin(body)) failures.Add("A positive-size translation outside the body was marked visible.");
            transform.TranslateY = 0;
            if (!compact.IsTranslationVisibleWithin(body)) failures.Add("Returning the translation to the body did not restore visibility.");
            if (media.LyricPresentation.Line?.Text != line.Text || media.LyricPresentation.Line?.Secondary != line.Secondary)
                failures.Add("Compact presentation changed the stored multi-line lyrics.");
            evidence.Add(new(fontSize, root.XamlRoot.RasterizationScale, true, true,
                secondary.ActualWidth, viewport.ActualWidth, offset, failures, "compact-untimed-body"));
            window.Content = null;
        }

        var expandedMedia = CreateFixture(strings, "normal");
        expandedMedia.Settings = expandedMedia.Settings with { Lyrics = expandedMedia.Settings.Lyrics with { FontSize = 28 } };
        var longLine = new LyricsLine(TimeSpan.Zero, TimeSpan.FromMinutes(1),
            string.Join(" ", Enumerable.Repeat("Full original lyric", 80)), "Visible translation", [])
        { TranslationOrigin = LyricsTranslationOrigin.LocalAi, TranslationLanguage = strings.Culture.Name };
        expandedMedia.SetLyricsDocument(new([longLine], LyricsProviderKind.LocalLrc));
        expandedMedia.Lyrics = new(longLine, -1, 0, 1);
        var expanded = new ExpandedIslandMusicView { ViewModel = expandedMedia };
        var expandedRoot = CreateRoot(ElementTheme.Dark, 560, 340);
        expandedRoot.Children.Add(expanded);
        window.Content = expandedRoot;
        await WaitForLayoutAsync(expandedRoot);
        var expandedFailures = new List<string>();
        if (expanded.IsTranslationActuallyVisible) expandedFailures.Add("Translation below the viewport was treated as visible.");
        var scroll = Descendants(expanded).OfType<ScrollViewer>().Single(view => view.Name == "CurrentLyricsViewport");
        if (!scroll.IsTabStop || AutomationProperties.GetName(scroll) != strings.Get("MusicLyricsSection") || !scroll.Focus(FocusState.Keyboard))
            expandedFailures.Add("Full lyrics cannot be reached with native keyboard focus.");
        var visibilityEvents = 0;
        expanded.TranslationVisibilityChanged += (_, _) => visibilityEvents++;
        scroll.ChangeView(null, scroll.ScrollableHeight, null, disableAnimation: true);
        await WaitForLayoutAsync(expandedRoot);
        if (!expanded.IsTranslationActuallyVisible || visibilityEvents == 0)
            expandedFailures.Add("Scrolling translation into view did not update actual visibility.");
        evidence.Add(new(28, expandedRoot.XamlRoot.RasterizationScale, true, false, 0, scroll.ViewportWidth, 0, expandedFailures, "expanded-viewport"));
        window.Content = null;
    }

    private static MediaViewModel CreateFixture(IAppStringLocalizer strings, string scenario)
    {
        var media = new MediaViewModel(new SyntheticMedia(), strings, NullLogger<MediaViewModel>.Instance)
        {
            IsReducedMotion = true, PositionEstimated = false,
            Settings = new AppSettings
            {
                Lyrics = new LyricsSettings { Enabled = scenario != "lyrics-off", SecondaryLyrics = scenario != "no-translation", FontSize = scenario == "long-font32" ? 32 : 16 },
                IslandActivity = new IslandActivitySettings { ShowSpectrum = false },
            },
        };
        if (scenario == "no-session") return media;
        var chinese = strings.Culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        var title = chinese ? "\u539f\u521b\u6d4b\u8bd5\u97f3\u4e50" : "Synthetic visual test track";
        var text = chinese ? "\u6e05\u6668\u7684\u5149\u843d\u5728\u7a97\u8fb9" : "Morning light rests beside the window";
        var next = chinese ? "\u4e0b\u4e00\u53e5\u6b4c\u8bcd\u4ec5\u663e\u793a\u4e00\u884c" : "The next original lyric stays on one line";
        if (scenario == "long-font32") { text = string.Join(" / ", Enumerable.Repeat(text, 5)); next = string.Join(" / ", Enumerable.Repeat(next, 4)); }
        media.Session = MediaSessionSnapshot.Empty with
        {
            SessionId = "visual-fixture", SourceAppUserModelId = "DropSpace.VisualFixture", SourceDisplayName = "Synthetic fixture",
            TrackTitle = title, Artist = "Test artist", AlbumTitle = "Original test material",
            PlaybackState = MediaPlaybackState.Playing, CanPlay = true, CanPause = true, CanSeek = true, CanSkipNext = true, CanSkipPrevious = true,
            Timeline = new(TimeSpan.FromSeconds(14), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(190), 1, DateTimeOffset.UtcNow),
        };
        var translation = chinese ? "\u8bd1\u6587\u6d4b\u8bd5\uff1a\u7a97\u8fb9\u7684\u6668\u5149" : "Translation fixture: light by the window";
        var lines = new LyricsLine[]
        {
            new(TimeSpan.Zero, TimeSpan.FromSeconds(8), text, translation, []) { TranslationLanguage = strings.Culture.Name },
            new(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16), next, null, []),
        };
        media.SetLyricsDocument(new(lines, LyricsProviderKind.LocalLrc));
        var last = scenario == "last-line";
        media.Position = TimeSpan.FromSeconds(last ? 20 : 14);
        media.Lyrics = new(lines[last ? 1 : 0], -1, 0, 1);
        media.LyricsStatus = LyricsQueryStatus.Found;
        return media;
    }

    private static async Task<CaptureEvidence> CaptureAsync(Window window, Grid root, FrameworkElement subject, MediaViewModel media,
        IAppStringLocalizer strings, string output, string name, string surface, string scenario)
    {
        window.Content = root;
        try
        {
            window.AppWindow.MoveAndResize(new RectInt32(0, 0, (int)root.Width + 32, (int)root.Height + 64));
            window.Activate();
            await WaitForLayoutAsync(root);
            var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var scale = root.XamlRoot.RasterizationScale;
            window.AppWindow.Resize(new SizeInt32((int)Math.Ceiling(root.Width * scale) + 32, (int)Math.Ceiling(root.Height * scale) + 64));
            await WaitForLayoutAsync(root);
            if (!IsWindowVisible(handle)) throw new InvalidOperationException("The diagnostic HWND is not visible.");
            var elements = Descendants(subject).OfType<FrameworkElement>().ToArray();
            var boxes = elements.Where(element => !string.IsNullOrWhiteSpace(element.Name)).Select(element => Measure(element, root)).ToArray();
            var failures = InspectLayout(subject, root, media, strings, surface, elements);
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(root).AsTask().WaitAsync(TimeSpan.FromSeconds(15));
            var buffer = await bitmap.GetPixelsAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
            var pixels = new byte[buffer.Length];
            using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(pixels);
            if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0 ||
                pixels.LongLength != (long)bitmap.PixelWidth * bitmap.PixelHeight * 4 ||
                !MusicVisualSmokeOptions.HasPixelContent(pixels))
                throw new InvalidOperationException("The native pixel capture is empty, blank, uniform, or incomplete.");
            using var encoded = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, encoded);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96 * scale, 96 * scale, pixels);
            await encoder.FlushAsync();
            encoded.Seek(0);
            using var pngReader = new DataReader(encoded.GetInputStreamAt(0));
            await pngReader.LoadAsync(checked((uint)encoded.Size));
            var png = new byte[checked((int)encoded.Size)];
            pngReader.ReadBytes(png);
            await File.WriteAllBytesAsync(Path.Combine(output, name + ".png"), png);
            var capture = new CaptureEvidence(name + ".png", surface, scenario, root.ActualTheme.ToString(),
                bitmap.PixelWidth, bitmap.PixelHeight, root.ActualWidth, root.ActualHeight,
                root.XamlRoot.RasterizationScale, GetDpiForWindow(handle), new UISettings().TextScaleFactor,
                new AccessibilitySettings().HighContrast, media.Settings.Lyrics.FontSize, boxes, failures);
            await File.WriteAllTextAsync(Path.Combine(output, name + ".json"), JsonSerializer.Serialize(capture, JsonOptions));
            return capture;
        }
        finally { window.Content = null; }
    }

    private static async Task WaitForLayoutAsync(FrameworkElement root)
    {
        var deadline = Stopwatch.StartNew();
        var stable = 0;
        var previous = new Size(-1, -1);
        while (deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(80);
            root.UpdateLayout();
            var size = new Size(root.ActualWidth, root.ActualHeight);
            if (root.IsLoaded && root.XamlRoot is not null && size.Width > 0 && size.Height > 0 && size == previous)
            { if (++stable >= 3) return; }
            else stable = 0;
            previous = size;
        }
        throw new TimeoutException("The native WinUI visual tree did not reach a loaded, nonzero, stable layout.");
    }

    private static async Task WaitForPresentationFrameAsync(FrameworkElement root)
    {
        var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnFrame(object? sender, object args)
        {
            CompositionTarget.Rendering -= OnFrame;
            frame.TrySetResult();
        }
        CompositionTarget.Rendering += OnFrame;
        try
        {
            await frame.Task.WaitAsync(TimeSpan.FromSeconds(10));
            root.UpdateLayout();
        }
        finally { CompositionTarget.Rendering -= OnFrame; }
    }

    private static List<string> InspectLayout(FrameworkElement subject, FrameworkElement root, MediaViewModel media,
        IAppStringLocalizer strings, string surface, FrameworkElement[] elements)
    {
        var failures = new List<string>();
        var refresh = elements.OfType<Button>().Count(button => AutomationProperties.GetName(button) == strings.Get("MusicRefresh"));
        if (refresh != (surface == "music-page-component" ? 1 : 0)) failures.Add("Refresh action is on the wrong surface or duplicated.");
        var next = elements.OfType<TextBlock>().Where(element => element.Name == "NextLyric").ToArray();
        if (surface == "music-page-component")
        {
            if (next.Length != 0) failures.Add("The main player unexpectedly contains the island upcoming line.");
            var lyricViewport = elements.OfType<ScrollViewer>().Single(view => view.Name == "CurrentLyricsViewport");
            if (!lyricViewport.IsTabStop || AutomationProperties.GetName(lyricViewport) != strings.Get("MusicLyricsSection"))
                failures.Add("The main card's complete lyrics are not exposed to keyboard navigation.");
            var refreshButton = elements.OfType<Button>().SingleOrDefault(button => AutomationProperties.GetAutomationId(button) == "MusicRefresh");
            if (refreshButton is null || refreshButton.Content is not StackPanel refreshContent ||
                !refreshContent.Children.OfType<FontIcon>().Any(icon => icon.Glyph == "\uE72C" && icon.FontSize == 16) ||
                !refreshContent.Children.OfType<TextBlock>().Any(label => label.Text == strings.Get("MusicRefresh")) ||
                !double.IsNaN(refreshButton.Width) || !double.IsNaN(refreshButton.Height))
                failures.Add("Refresh does not expose the standard complete icon and label at native button size.");
            return failures;
        }
        if (next.Length != 1) failures.Add("The island must contain exactly one upcoming lyric element.");
        else
        {
            if (next[0].MaxLines != 1 || next[0].TextWrapping != TextWrapping.NoWrap) failures.Add("Upcoming text is not constrained to one line.");
            var visible = IsVisible(next[0], root);
            if (visible != !string.IsNullOrWhiteSpace(media.NextLyricText)) failures.Add("Upcoming visibility does not match the current fixture.");
            if (visible && next[0].Text != media.NextLyricText) failures.Add("Upcoming text does not match the current track.");
        }
        var timeline = elements.Single(element => element.Name == "TimelineRow");
        var controls = elements.Single(element => element.Name == "ControlsRow");
        var timelineBox = Measure(timeline, root);
        var controlsBox = Measure(controls, root);
        if (!string.IsNullOrEmpty(media.Title))
        {
            if (!timelineBox.Visible || !controlsBox.Visible) failures.Add("Playback controls disappeared for an active track.");
            if (timelineBox.Y + timelineBox.Height > controlsBox.Y + 1) failures.Add("Timeline overlaps playback controls.");
            if (!Contained(timelineBox, root) || !Contained(controlsBox, root)) failures.Add("Playback controls exceed the fixed island bounds.");
            var preview = elements.Single(element => element.Name == "NextLyricPreview");
            if (IsVisible(preview, root))
            {
                var previewBox = Measure(preview, root);
                if (previewBox.Y + previewBox.Height > timelineBox.Y + 1 || !Contained(previewBox, root))
                    failures.Add("Upcoming lyric overlaps the timeline or exceeds the island bounds.");
                var viewport = Measure(elements.Single(element => element.Name == "CurrentLyricsViewport"), root);
                if (viewport.Y + viewport.Height > previewBox.Y + 1) failures.Add("Current lyrics overlap the upcoming preview.");
            }
        }
        return failures;
    }

    private static bool Contained(ElementBox box, FrameworkElement root) => box.Width > 0 && box.Height > 0 &&
        box.X >= -1 && box.Y >= -1 && box.X + box.Width <= root.ActualWidth + 1 && box.Y + box.Height <= root.ActualHeight + 1;

    private static ElementBox Measure(FrameworkElement element, FrameworkElement root)
    {
        var bounds = element.TransformToVisual(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return new(element.Name, element.GetType().Name, IsVisible(element, root), bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    private static bool IsVisible(DependencyObject element, DependencyObject root)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement ui && (ui.Visibility != Visibility.Visible || ui.Opacity <= 0)) return false;
            if (ReferenceEquals(current, root)) return true;
        }
        return false;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private sealed record ElementBox(string Name, string Type, bool Visible, double X, double Y, double Width, double Height);
    private sealed record LyricsLayoutEvidence(double FontSize, double ActualRasterizationScale,
        bool LongOriginal, bool LongTranslation, double TranslationWidth, double ViewportWidth, double MarqueeOffset,
        IReadOnlyList<string> Failures, string Surface = "compact-marquee");
    private sealed record CompactRenderEvidence(int ObservedRefreshNotifications, long BurstRefreshes,
        long HiddenRefreshes, long ReopenRefreshes, long PreparedRefreshes, IReadOnlyList<string> Failures);
    private sealed record ExpandedRenderEvidence(long HiddenRenders, long ReopenRenders,
        long PreparedRenders, bool SeekWorkRetired, IReadOnlyList<string> Failures);
    private sealed record CaptureEvidence(string File, string Surface, string Scenario, string Theme,
        int PixelWidth, int PixelHeight, double ActualWidth, double ActualHeight, double RasterizationScale,
        uint WindowDpi, double SystemTextScale, bool HighContrast, double ApplicationLyricFontSize,
        IReadOnlyList<ElementBox> Elements, IReadOnlyList<string> Failures);

    private sealed class SyntheticMedia : IMediaSessionService
    {
        public event EventHandler<MediaSessionSnapshot>? Changed { add { } remove { } }
        public MediaSessionSnapshot Current => MediaSessionSnapshot.Empty;
        public bool IsAvailable => true;
        public string AvailabilityReason => "SyntheticOnly";
        public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task PlayPauseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SkipNextAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SkipPreviousAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SyntheticEnhancement : INeteaseEnhancementService
    {
        public NeteaseEnhancementState Current => new(NeteaseEnhancementStage.NotInstalled);
        public event EventHandler<NeteaseEnhancementState>? Changed { add { } remove { } }
        public Task InspectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task EnhanceAsync(bool reinstall = false, CancellationToken cancellationToken = default) => Task.FromException(new InvalidOperationException("Unavailable in synthetic diagnostics."));
        public Task RemoveAsync(CancellationToken cancellationToken = default) => Task.FromException(new InvalidOperationException("Unavailable in synthetic diagnostics."));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseDesktop(nint desktop);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint window);
}
