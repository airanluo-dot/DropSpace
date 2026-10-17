using System.Diagnostics;
using System.Text.Json;
using DropSpace.App.ViewModels;
using DropSpace.App.Views;
using DropSpace.App.Views.Island;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Island;
using DropSpace.Core.Models;
using DropSpace.Core.Overlay;
using DropSpace.Core.Widgets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace DropSpace.App.Services.Diagnostics;

/// <summary>Explicit, independently selected native scenarios; ordinary App startup never calls this runner.</summary>
internal static partial class BetaUiDiagnostics
{
    internal static bool IsRequested(IReadOnlyList<string> arguments) =>
        arguments.Contains("--beta-ui-diagnostics", StringComparer.OrdinalIgnoreCase);

    internal static async Task<int> RunAsync(IReadOnlyList<string> arguments, Func<string, ServiceProvider> createServices,
        Func<Task> drainLogger)
    {
        try { return await RunCoreAsync(arguments, createServices, drainLogger); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A failed receipt write or HWND close must not reach the normal App crash
            // marker, which belongs to the user's profile. The launcher owns this failure.
            Debug.WriteLine("Beta UI diagnostic failed outside its receipt: " + Describe(error));
            return 1;
        }
    }

    private static async Task<int> RunCoreAsync(IReadOnlyList<string> arguments, Func<string, ServiceProvider> createServices,
        Func<Task> drainLogger)
    {
        BetaUiDiagnosticReceipt? receipt = null;
        ServiceProvider? services = null;
        Window? host = null;
        OverlayWindow? overlay = null;
        MainPage? page = null;
        void OnUnhandled(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
        {
            receipt?.Exceptions.Add("UI dispatch: " + Describe(args.Exception));
            args.Handled = true;
        }
        Application.Current.UnhandledException += OnUnhandled;
        try
        {
            // Reject unsafe output before constructing any service or touching user data.
            var selection = BetaUiDiagnosticFixture.ParseSelection(arguments);
            receipt = new(BetaUiDiagnosticFixture.CreateOutput(arguments), selection);
            Stage(receipt, "desktop");
            var desktop = OpenInputDesktop(0, false, 1);
            var accessibleDesktop = desktop != 0;
            if (desktop != 0) CloseDesktop(desktop);
            using var process = Process.GetCurrentProcess();
            receipt.Environment = new
            {
                os = Environment.OSVersion.VersionString, processId = Environment.ProcessId,
                sessionId = process.SessionId, userInteractive = Environment.UserInteractive,
                inputDesktopAccessible = accessibleDesktop,
                screenWidth = GetSystemMetrics(0), screenHeight = GetSystemMetrics(1),
                textScaleFactor = new UISettings().TextScaleFactor,
            };
            if (!Environment.UserInteractive || !accessibleDesktop || GetSystemMetrics(0) <= 0 || GetSystemMetrics(1) <= 0)
            {
                receipt.Status = "blocked";
                receipt.VisualStatus = "blocked";
                receipt.Exceptions.Add("No usable interactive desktop; no scenarios or pixel validation were executed.");
            }
            else
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                var token = deadline.Token;
                Stage(receipt, "construct-isolated-controls");
                var dataRoot = Path.Combine(receipt.Output, "data");
                services = createServices(dataRoot);
                var settings = BetaUiDiagnosticFixture.Settings;
                Stage(receipt, "fixture-save-settings");
                await services.GetRequiredService<ISettingsService>().SaveAsync(settings, token);
                var main = services.GetRequiredService<MainViewModel>();
                BetaUiDiagnosticFixture.ApplySettings(main, settings);
                services.GetRequiredService<AppLanguageService>().Apply(AppLanguagePreference.English);
                var strings = services.GetRequiredService<IAppStringLocalizer>();
                XamlResourceOverride.Initialize(strings);
                services.GetRequiredService<MediaViewModel>().Settings = settings;
                var repository = services.GetRequiredService<IItemRepository>();
                Stage(receipt, "fixture-initialize-repository");
                await repository.InitializeAsync(token);
                Stage(receipt, "fixture-initial-navigation");
                await main.NavigateAsync("Space", token);
                host = new Window { Title = "DropSpace isolated beta UI diagnostics" };
                host.AppWindow.Resize(new SizeInt32(1100, 850));
                if (selection.RunBatch)
                {
                    page = ActivatorUtilities.CreateInstance<MainPage>(services, WinRT.Interop.WindowNative.GetWindowHandle(host));
                    // MainWindow normally supplies the backdrop. This diagnostic host supplies
                    // a declared opaque light backdrop so transparent pixels are not fabricated later.
                    page.RequestedTheme = ElementTheme.Light;
                    page.Background = new SolidColorBrush(Microsoft.UI.Colors.White);
                    host.Content = page;
                    page.SetPresentationActive(true);
                    host.Activate();
                    await WaitAtStageAsync(receipt, "main-page-layout",
                        () => page.IsLoaded && page.ActualWidth > 0 && page.ActualHeight > 0, token);
                    receipt.Observations.Add(new { stage = "batch-fixture", mainScale = page.XamlRoot.RasterizationScale,
                        dataRoot, mainTheme = "Light", mainBackdrop = "opaque-white-diagnostic-host" });
                }
                else
                {
                    // Keep a neutral final HWND alive for service retirement, without
                    // constructing or executing the already-passed MainPage scenario.
                    host.Content = new Grid();
                    host.Activate();
                }
                if (selection.RunTimers)
                {
                    Stage(receipt, "construct-timer-overlay");
                    var monitor = services.GetRequiredService<MonitorLayoutService>().GetPrimaryMonitor();
                    var island = services.GetRequiredService<IslandExperienceCoordinator>();
                    var overlayView = services.GetRequiredService<OverlayViewModel>();
                    var drag = new DragActivationCallbacks(_ => { }, (_, _) => { }, _ => { },
                        (_, _) => Task.FromException(new InvalidOperationException("Drag input is outside this diagnostic.")));
                    overlay = ActivatorUtilities.CreateInstance<OverlayWindow>(services, monitor, drag, (Action)(() => { }));
                    receipt.Observations.Add(new
                    {
                        stage = "timer-fixture", monitorDpi = monitor.Dpi, settings.Widgets.Layout.Expanded, dataRoot,
                        nativeServices = "Widget sampling and system visual preferences only; other service dependencies remain uninitialized.",
                    });
                    if (selection.RunTimerVisual)
                        await RunCaseAsync("timer-first-frame-and-popup-retirement", receipt,
                            () => CheckTimerVisualAsync(overlay, overlayView, island, services.GetRequiredService<WidgetViewModel>(), receipt, token));
                    else
                        await RunCaseAsync("timer-menu-retirement", receipt,
                            () => CheckTimersAsync(overlay, overlayView, island, services.GetRequiredService<WidgetViewModel>(), receipt, token));
                    overlay.ApplySnapshot(overlayView.Snapshot with { State = OverlayState.Hidden },
                        true, true, overlayView.FileDragWakeMode, overlayView.GetOverlayPlacement(overlay.MonitorId));
                }
                if (selection.RunBatch)
                    await RunCaseAsync("batch-collapse-selection", receipt,
                        () => CheckBatchAsync(page!, main, repository, receipt, token));
                var scenarioNamesMatch = receipt.Scenarios.Select(scenario => scenario.Name).SequenceEqual(receipt.ExpectedScenarios);
                var captureNamesMatch = receipt.Captures.Select(capture => capture.File).Order(StringComparer.Ordinal)
                    .SequenceEqual(receipt.ExpectedCaptures.Order(StringComparer.Ordinal));
                receipt.VisualStatus = captureNamesMatch && receipt.CaptureFailures.Count == 0 &&
                    receipt.Captures.All(capture => capture.PixelReadbackPassed)
                    ? "passed" : "blocked";
                receipt.Status = !scenarioNamesMatch || receipt.PassedCaseCount != receipt.PlannedCaseCount || receipt.Exceptions.Count != 0 ? "failed" :
                    receipt.VisualStatus == "passed" ? "passed" : "blocked";
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (receipt is null)
            {
                // No validated writable destination: the launcher records preparation failure.
                Debug.WriteLine("Beta UI diagnostic preparation failed: " + Describe(error));
                return 3;
            }
            receipt.Status = "failed";
            receipt.FailureStage = receipt.Stage;
            receipt.Exceptions.Add(Describe(error));
        }
        finally
        {
            // Keep the final host HWND alive while asynchronous service ownership drains.
            if (receipt is not null)
            {
                receipt.Stage = "retire-controls-and-services";
                async Task RetireAsync(string owner, Func<Task> retire)
                {
                    try { await retire().WaitAsync(TimeSpan.FromSeconds(15)); }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        receipt.Status = "failed";
                        receipt.Exceptions.Add("Retirement (" + owner + "): " + Describe(error));
                    }
                }
                await RetireAsync("page", () => { page?.Retire(); return Task.CompletedTask; });
                await RetireAsync("overlay", () => { overlay?.CloseForShutdown(); return Task.CompletedTask; });
                await RetireAsync("host content", () =>
                {
                    if (host is not null) host.Content = null;
                    return Task.CompletedTask;
                });
                await RetireAsync("services", () => services?.DisposeAsync().AsTask() ?? Task.CompletedTask);
                await RetireAsync("logger", drainLogger);
                if (receipt.Exceptions.Count != 0 && receipt.Status == "passed") receipt.Status = "failed";
                receipt.CompletedAtUtc = DateTimeOffset.UtcNow;
                Stage(receipt, "complete");
                WriteEvidence(receipt, final: true);
                // Closing the last HWND can stop the WinUI loop. Persist its receipt first.
                var errorsBeforeClose = receipt.Exceptions.Count;
                await RetireAsync("host", () => { host?.Close(); host = null; return Task.CompletedTask; });
                if (receipt.Exceptions.Count != errorsBeforeClose)
                {
                    receipt.Status = "failed";
                    WriteEvidence(receipt, final: true);
                }
            }
            Application.Current.UnhandledException -= OnUnhandled;
        }
        return receipt.Status == "passed" ? 0 : receipt.Status == "blocked" ? 2 : 1;
    }

    private static async Task RunCaseAsync(string name, BetaUiDiagnosticReceipt receipt, Func<Task> run)
    {
        var scenario = new BetaUiDiagnosticScenario(name);
        receipt.Scenarios.Add(scenario);
        Stage(receipt, name);
        try { await run(); scenario.Status = "passed"; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            scenario.Status = "failed";
            scenario.FailureStage = receipt.Stage;
            scenario.Failure = Describe(error);
        }
        WriteEvidence(receipt);
    }

    private static object ObserveOverlay(OverlayWindow overlay) => new
    {
        overlay = overlay.ObserveModuleOverlay(), overlay.NativeVisibilityDiagnostics, overlay.HasActiveFrameSubscription,
        layers = Descendants(overlay.Content).OfType<FrameworkElement>()
            .Where(element => element.Name is "Surface" or "ExpandedPanel" or "WidgetsExpanded")
            .Select(PresentationState).ToArray(),
    };

    private static async Task CheckTimersAsync(OverlayWindow overlay, OverlayViewModel overlayView,
        IslandExperienceCoordinator island, WidgetViewModel widgets, BetaUiDiagnosticReceipt receipt, CancellationToken token)
    {
        void Present(bool visible)
        {
            if (visible) island.Open(IslandPage.Widgets);
            else island.DismissNow();
            overlay.ApplySnapshot(overlayView.Snapshot with { State = visible ? island.Current.State : OverlayState.Hidden },
                true, true, overlayView.FileDragWakeMode, overlayView.GetOverlayPlacement(overlay.MonitorId));
        }
        Stage(receipt, "timer-overlay-present");
        Present(true);
        await WaitAtStageAsync(receipt, "timer-overlay-ready",
            () => overlay.ObserveModuleOverlay() is { Loaded: true, Visible: true, WidgetsActive: true }, token,
            () => ObserveOverlay(overlay));
        var view = Descendants(overlay.Content).OfType<WidgetsExpandedView>().Single();
        var tiles = Descendants(view).OfType<Grid>().Single(grid => grid.Name == "Tiles");
        await WaitAtStageAsync(receipt, "timer-tiles-layout",
            () => tiles.Children.Count == 2 && tiles.Children.OfType<FrameworkElement>()
                .All(tile => tile.ActualWidth > 0 && tile.ActualHeight > 0), token, () => ObserveOverlay(overlay));
        Require(widgets.Layout.Expanded.All(placement => placement.ColumnSpan == 2 && placement.RowSpan == 1),
            "The diagnostic timers did not use the explicit 2 x 1 fixture layout.");
        await CaptureAsync(overlay.DiagnosticSurface, "timers-initial", receipt, token);
        await CaptureDesktopAsync(overlay, null, "timers-initial", receipt, token);

        foreach (var id in new[] { NativeWidgetId.FocusTimer, NativeWidgetId.Countdown })
        {
            Stage(receipt, "timer-" + id + "-controls-geometry");
            var placement = widgets.Layout.Expanded.Single(item => item.Id == id);
            var tile = tiles.Children.OfType<FrameworkElement>().Single(element =>
                Grid.GetColumn(element) == placement.Column && Grid.GetRow(element) == placement.Row);
            var buttons = Descendants(tile).OfType<Button>().ToArray();
            var durationButton = buttons.Single(button => button.Flyout is MenuFlyout);
            var startButton = buttons.Single(button => button.Flyout is null);
            foreach (var button in buttons)
            {
                var origin = button.TransformToVisual(tile).TransformPoint(new Point(0, 0));
                Require(button.ActualWidth > 0 && button.ActualHeight > 0 && origin.X >= -1 && origin.Y >= -1 &&
                    origin.X + button.ActualWidth <= tile.ActualWidth + 1 && origin.Y + button.ActualHeight <= tile.ActualHeight + 1,
                    id + " controls overflowed their real 2 x 1 tile.");
            }
            var menu = (MenuFlyout)durationButton.Flyout;
            var timer = id == NativeWidgetId.FocusTimer ? widgets.FocusTimer : widgets.Countdown;
            ToggleMenuFlyoutItem? choice = null;
            string? inputPattern = null;
            string? peerType = null;
            var invokeSupported = false;
            var toggleSupported = false;
            var closed = 0;
            var choiceClicks = 0;
            var durationClicks = 0;
            var startClicks = 0;
            void OnClosed(object? sender, object args) => closed++;
            void OnChoiceClick(object sender, RoutedEventArgs args) => choiceClicks++;
            void OnDurationClick(object sender, RoutedEventArgs args) => durationClicks++;
            void OnStartClick(object sender, RoutedEventArgs args) => startClicks++;
            object Snapshot() => new
            {
                timer = id.ToString(), durationMinutes = timer.Duration.TotalMinutes, timer.IsRunning, timer.RemainingSeconds,
                inputPattern, peerType, invokeSupported, toggleSupported,
                choiceClicks, durationClicks, startClicks, menuClosedEvents = closed, menuOpen = menu.IsOpen,
                choiceChecked = choice?.IsChecked, choiceLoaded = choice?.IsLoaded, choiceEnabled = choice?.IsEnabled,
                viewLoaded = view.IsLoaded, currentViewModel = ReferenceEquals(view.ViewModel, widgets),
                tileRetained = tiles.Children.Contains(tile), durationEnabled = durationButton.IsEnabled, startEnabled = startButton.IsEnabled,
                presentation = ObserveOverlay(overlay),
            };
            void ActionStep(string step, Action action)
            {
                var stage = "timer-" + id + "-action-" + step;
                Stage(receipt, stage);
                receipt.Observations.Add(new { stage, outcome = "before", state = Snapshot() });
                WriteEvidence(receipt);
                try { action(); }
                finally
                {
                    receipt.Observations.Add(new { stage, outcome = "after", state = Snapshot() });
                    WriteEvidence(receipt);
                }
            }
            Task WaitStep(string step, Func<bool> condition) => WaitAtStageAsync(receipt,
                "timer-" + id + "-wait-" + step, condition, token, Snapshot);
            menu.Closed += OnClosed;
            durationButton.Click += OnDurationClick;
            startButton.Click += OnStartClick;
            try
            {
                var selectionMenuClosedBaseline = closed;
                ActionStep("open-duration-menu", () => Invoke(durationButton));
                await WaitStep("duration-menu-open", () => menu.IsOpen);
                var submenu = menu.Items.OfType<MenuFlyoutSubItem>().Single();
                var submenuPeer = FrameworkElementAutomationPeer.CreatePeerForElement(submenu)
                    ?? throw new InvalidOperationException("Duration submenu has no real automation peer.");
                ActionStep("expand-duration-submenu", () =>
                    (submenuPeer.GetPattern(PatternInterface.ExpandCollapse) as IExpandCollapseProvider
                        ?? throw new InvalidOperationException("Duration submenu has no ExpandCollapse pattern.")).Expand());
                choice = submenu.Items.OfType<ToggleMenuFlyoutItem>().Single(item => item.Text == widgets.TimerDurationLabel(10));
                choice.Click += OnChoiceClick;
                await WaitStep("duration-choice-realized", () => choice.IsLoaded && choice.ActualWidth > 0);
                var popup = VisualTreeHelper.GetOpenPopupsForXamlRoot(view.XamlRoot)
                    .Select(item => item.Child).OfType<FrameworkElement>()
                    .Single(root => ReferenceEquals(root, choice) || Descendants(root).Any(child => ReferenceEquals(child, choice)));
                receipt.Observations.Add(new
                {
                    stage = "timer-menu-expanded", timer = id.ToString(), popup = Bounds(popup, popup),
                    choice = Bounds(choice, popup), menuOpen = menu.IsOpen,
                });
                await CaptureAsync(popup, "timer-menu-" + id, receipt, token);
                await CaptureDesktopAsync(overlay, popup, "timer-menu-" + id, receipt, token);
                Stage(receipt, "timer-" + id + "-choice-automation-pattern");
                var choicePeer = FrameworkElementAutomationPeer.CreatePeerForElement(choice)
                    ?? throw new InvalidOperationException("Duration choice has no real automation peer.");
                peerType = choicePeer.GetType().FullName;
                var invoke = choicePeer.GetPattern(PatternInterface.Invoke) as IInvokeProvider;
                var toggle = choicePeer.GetPattern(PatternInterface.Toggle) as IToggleProvider;
                invokeSupported = invoke is not null;
                toggleSupported = toggle is not null;
                inputPattern = invokeSupported ? "Invoke" : toggleSupported ? "Toggle" : "none";
                // Preserve the first run's control path. Open-source WinUI raises Click
                // for both patterns; record actual callbacks/state instead of assuming a fix.
                ActionStep("select-ten-minutes", () =>
                {
                    if (invoke is not null) invoke.Invoke();
                    else (toggle ?? throw new InvalidOperationException("Duration choice has no Invoke or Toggle pattern.")).Toggle();
                });
                await WaitStep("duration-ten-minutes", () => timer.Duration == TimeSpan.FromMinutes(10));
                ActionStep("close-after-selection", menu.Hide);
                // IsOpen becomes false before the native close animation completes.
                // Wait for this opening cycle's Closed event before invoking the button again.
                // Capture the baseline before opening: Toggle may close synchronously.
                await WaitStep("menu-closed-after-selection", () => !menu.IsOpen && closed > selectionMenuClosedBaseline);
                ActionStep("start", () => Invoke(startButton));
                await WaitStep("running", () => timer.IsRunning);
                ActionStep("pause", () => Invoke(startButton));
                await WaitStep("paused", () => !timer.IsRunning);
                ActionStep("open-before-hide", () => Invoke(durationButton));
                await WaitStep("menu-open-before-hide", () => menu.IsOpen);
                var beforeHide = closed;
                ActionStep("hide-overlay", () => Present(false));
                await WaitStep("menu-retired-and-overlay-hidden",
                    () => !menu.IsOpen && closed > beforeHide && !overlay.ObserveModuleOverlay().Visible);
                ActionStep("reopen-overlay", () => Present(true));
                await WaitStep("overlay-reopened", () => overlay.ObserveModuleOverlay() is { Visible: true, WidgetsActive: true });
                Require(!menu.IsOpen, "A retired timer menu reopened with the retained overlay.");
                var reopenedMenuClosedBaseline = closed;
                ActionStep("open-menu-after-reopen", () => Invoke(durationButton));
                await WaitStep("menu-open-after-reopen", () => menu.IsOpen);
                Stage(receipt, "timer-" + id + "-selected-duration-retained");
                Require(choice.IsChecked, "The duration submenu lost its selected value after hide/reopen.");
                receipt.Observations.Add(new
                {
                    stage = "timer-reopened", state = Snapshot(),
                    tile = Bounds(tile, tiles), durationButton = Bounds(durationButton, tile), startButton = Bounds(startButton, tile),
                    scale = view.XamlRoot.RasterizationScale,
                });
                ActionStep("close-after-reopen", menu.Hide);
                await WaitStep("menu-closed-after-reopen", () => !menu.IsOpen && closed > reopenedMenuClosedBaseline);
            }
            finally
            {
                if (choice is not null) choice.Click -= OnChoiceClick;
                durationButton.Click -= OnDurationClick;
                startButton.Click -= OnStartClick;
                menu.Closed -= OnClosed;
                menu.Hide();
            }
        }
        await CaptureAsync(overlay.DiagnosticSurface, "timers-reopened", receipt, token);
        await CaptureDesktopAsync(overlay, null, "timers-reopened", receipt, token);
        Stage(receipt, "timer-overlay-final-hide");
        Present(false);
    }

    private static async Task CheckBatchAsync(MainPage page, MainViewModel main, IItemRepository repository,
        BetaUiDiagnosticReceipt receipt, CancellationToken token)
    {
        Stage(receipt, "batch-seed-and-realize");
        var anchorItem = await repository.AddSpaceTextAsync(new("Diagnostic trailing row", Guid.NewGuid().ToString("N"),
            ItemKind.Text, "Diagnostic trailing row", DetectedSubtype.Plain, DetectionConfidence.High, null), cancellationToken: token);
        var batch = Guid.NewGuid();
        for (var index = 0; index < 3; index++)
        {
            var text = "Isolated beta diagnostic batch item " + index;
            await repository.AddSpaceTextAsync(new(text, Guid.NewGuid().ToString("N"), ItemKind.Text, text,
                DetectedSubtype.Plain, DetectionConfidence.High, null),
                JsonSerializer.Serialize(new DropBatchMetadata(batch, null, index, 3, "diagnostic")), token);
        }
        Stage(receipt, "batch-navigation");
        await page.SelectDiagnosticSectionAsync("Space").WaitAsync(token);
        var list = Descendants(page).OfType<ListView>().Single(control => control.Name == "ItemsList");
        var cards = main.Items.Where(card => card.DropBatchId == batch).ToArray();
        Require(cards.Length == 3, "The real repository projection did not contain the synthetic batch.");
        var header = cards.Single(card => card.IsBatchHeader);
        list.ScrollIntoView(header);
        await WaitAtStageAsync(receipt, "batch-header-realized",
            () => list.ContainerFromItem(header) is ListViewItem { ActualWidth: > 0 }, token);
        var headerContainer = (ListViewItem)list.ContainerFromItem(header);
        Button Toggle() => Descendants(headerContainer).OfType<Button>().Single(button =>
            ReferenceEquals(button.Tag, header) && button.Content is string text && text == header.BatchToggleLabel);
        Invoke(Toggle());
        await WaitAtStageAsync(receipt, "batch-expanded-members-realized", () => cards.All(card => card.IsBatchMemberVisible) &&
            cards.All(card => list.ContainerFromItem(card) is ListViewItem { ActualHeight: > 0, Visibility: Visibility.Visible }), token);
        foreach (var card in cards) list.SelectedItems.Add(card);
        Require(list.SelectedItems.Count == 3, "The real ListView did not accept the expanded multi-selection.");
        var containers = cards.Select(card => (ListViewItem)list.ContainerFromItem(card)).ToArray();
        var anchor = main.Items.Single(card => card.Id == anchorItem.Id);
        list.ScrollIntoView(anchor);
        await WaitAtStageAsync(receipt, "batch-anchor-realized",
            () => list.ContainerFromItem(anchor) is ListViewItem { ActualHeight: > 0 }, token);
        var anchorContainer = (ListViewItem)list.ContainerFromItem(anchor);
        var scroll = Descendants(list).OfType<ScrollViewer>().First();
        list.UpdateLayout();
        var expandedAnchorY = anchorContainer.TransformToVisual(list).TransformPoint(new Point(0, 0)).Y;
        var expandedScrollOffset = scroll.VerticalOffset;
        var expandedContentY = expandedAnchorY + expandedScrollOffset;
        await CaptureAsync(page, "batch-expanded-selected", receipt, token);
        Stage(receipt, "batch-collapse");
        Invoke(Toggle());
        await WaitAtStageAsync(receipt, "batch-hidden-selection-retired", () => cards.Where(card => !card.IsBatchHeader).All(card =>
            list.ContainerFromItem(card) is ListViewItem { Visibility: Visibility.Collapsed }) &&
            list.SelectedItems.Cast<ItemCardViewModel>().All(card => card.IsBatchMemberVisible), token);
        list.UpdateLayout();
        Require(list.SelectedItems.Count == 1 && ReferenceEquals(list.SelectedItems[0], header),
            "Collapsing the real batch did not retain only its selected header.");
        Require(main.SelectedItem is null || main.IsCurrentVisibleItem(main.SelectedItem),
            "Two-way SelectedItem retained a hidden batch member.");
        await WaitAtStageAsync(receipt, "batch-collapsed-layout-retired", () =>
            anchorContainer.TransformToVisual(list).TransformPoint(new Point(0, 0)).Y + scroll.VerticalOffset < expandedContentY - 1, token);
        var collapsedAnchorY = anchorContainer.TransformToVisual(list).TransformPoint(new Point(0, 0)).Y;
        var collapsedScrollOffset = scroll.VerticalOffset;
        var collapsedContentY = collapsedAnchorY + collapsedScrollOffset;
        Require(collapsedContentY < expandedContentY - 1,
            "The actual trailing row did not move into space retired by collapsed containers.");
        receipt.Observations.Add(new
        {
            stage = "batch-collapsed", selectedCount = list.SelectedItems.Count,
            expandedAnchorY, expandedScrollOffset, expandedContentY, collapsedAnchorY, collapsedScrollOffset, collapsedContentY,
            containers = containers.Select(container => Bounds(container, list)).ToArray(),
            hiddenMembers = cards.Count(card => !card.IsBatchMemberVisible), scale = page.XamlRoot.RasterizationScale,
        });
        await CaptureAsync(page, "batch-collapsed-selection", receipt, token);
    }
}
