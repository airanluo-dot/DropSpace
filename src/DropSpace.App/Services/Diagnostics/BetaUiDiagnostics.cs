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

/// <summary>Two explicit native scenarios; the ordinary App startup path never calls this runner.</summary>
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
            receipt = new(BetaUiDiagnosticFixture.CreateOutput(arguments));
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
                await services.GetRequiredService<ISettingsService>().SaveAsync(settings, token);
                var main = services.GetRequiredService<MainViewModel>();
                BetaUiDiagnosticFixture.ApplySettings(main, settings);
                services.GetRequiredService<AppLanguageService>().Apply(AppLanguagePreference.English);
                var strings = services.GetRequiredService<IAppStringLocalizer>();
                XamlResourceOverride.Initialize(strings);
                services.GetRequiredService<MediaViewModel>().Settings = settings;
                var repository = services.GetRequiredService<IItemRepository>();
                await repository.InitializeAsync(token);
                await main.NavigateAsync("Space", token);
                host = new Window { Title = "DropSpace isolated beta UI diagnostics" };
                host.AppWindow.Resize(new SizeInt32(1100, 850));
                page = ActivatorUtilities.CreateInstance<MainPage>(services, WinRT.Interop.WindowNative.GetWindowHandle(host));
                // MainWindow normally supplies the backdrop. This diagnostic host supplies
                // a declared opaque light backdrop so transparent pixels are not fabricated later.
                page.RequestedTheme = ElementTheme.Light;
                page.Background = new SolidColorBrush(Microsoft.UI.Colors.White);
                host.Content = page;
                page.SetPresentationActive(true);
                host.Activate();
                await WaitUntilAsync(() => page.IsLoaded && page.ActualWidth > 0 && page.ActualHeight > 0, token);

                var monitors = services.GetRequiredService<MonitorLayoutService>();
                var monitor = monitors.GetPrimaryMonitor();
                var island = services.GetRequiredService<IslandExperienceCoordinator>();
                var overlayView = services.GetRequiredService<OverlayViewModel>();
                var drag = new DragActivationCallbacks(_ => { }, (_, _) => { }, _ => { },
                    (_, _) => Task.FromException(new InvalidOperationException("Drag input is outside this diagnostic.")));
                overlay = ActivatorUtilities.CreateInstance<OverlayWindow>(services, monitor, drag, (Action)(() => { }));
                receipt.Observations.Add(new
                {
                    stage = "fixture", mainScale = page.XamlRoot.RasterizationScale, monitorDpi = monitor.Dpi,
                    settings.Widgets.Layout.Expanded, dataRoot, mainTheme = "Light", mainBackdrop = "opaque-white-diagnostic-host",
                    nativeServices = "Widget sampling and system visual preferences only; other service dependencies remain uninitialized.",
                });
                await RunCaseAsync("timer-menu-retirement", receipt,
                    () => CheckTimersAsync(overlay, overlayView, island, services.GetRequiredService<WidgetViewModel>(), receipt, token));
                overlay.ApplySnapshot(overlayView.Snapshot with { State = OverlayState.Hidden },
                    true, true, overlayView.FileDragWakeMode, overlayView.GetOverlayPlacement(overlay.MonitorId));
                await RunCaseAsync("batch-collapse-selection", receipt,
                    () => CheckBatchAsync(page, main, repository, receipt, token));
                string[] expectedCaptures = ["timers-initial.png", "timer-menu-FocusTimer.png", "timer-menu-Countdown.png",
                    "timers-reopened.png", "batch-expanded-selected.png", "batch-collapsed-selection.png"];
                receipt.VisualStatus = receipt.CaptureFailures.Count == 0 &&
                    expectedCaptures.All(file => receipt.Captures.Any(capture => capture.File == file && capture.PixelReadbackPassed))
                    ? "passed" : "blocked";
                receipt.Status = receipt.PassedCaseCount != 2 || receipt.Exceptions.Count != 0 ? "failed" :
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
        Present(true);
        await WaitUntilAsync(() => overlay.ObserveModuleOverlay() is { Loaded: true, Visible: true, WidgetsActive: true }, token);
        var view = Descendants(overlay.Content).OfType<WidgetsExpandedView>().Single();
        var tiles = Descendants(view).OfType<Grid>().Single(grid => grid.Name == "Tiles");
        await WaitUntilAsync(() => tiles.Children.Count == 2 && tiles.Children.OfType<FrameworkElement>()
            .All(tile => tile.ActualWidth > 0 && tile.ActualHeight > 0), token);
        Require(widgets.Layout.Expanded.All(placement => placement.ColumnSpan == 2 && placement.RowSpan == 1),
            "The diagnostic timers did not use the explicit 2 x 1 fixture layout.");
        await CaptureAsync(overlay.DiagnosticSurface, "timers-initial", receipt, token);

        foreach (var id in new[] { NativeWidgetId.FocusTimer, NativeWidgetId.Countdown })
        {
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
            var closed = 0;
            void OnClosed(object? sender, object args) => closed++;
            menu.Closed += OnClosed;
            try
            {
                Stage(receipt, "timer-duration-" + id);
                Invoke(durationButton);
                await WaitUntilAsync(() => menu.IsOpen, token);
                var submenu = menu.Items.OfType<MenuFlyoutSubItem>().Single();
                var submenuPeer = FrameworkElementAutomationPeer.CreatePeerForElement(submenu)
                    ?? throw new InvalidOperationException("Duration submenu has no real automation peer.");
                (submenuPeer.GetPattern(PatternInterface.ExpandCollapse) as IExpandCollapseProvider
                    ?? throw new InvalidOperationException("Duration submenu has no ExpandCollapse pattern.")).Expand();
                var choice = submenu.Items.OfType<ToggleMenuFlyoutItem>().Single(item => item.Text == widgets.TimerDurationLabel(10));
                await WaitUntilAsync(() => choice.IsLoaded && choice.ActualWidth > 0, token);
                var popup = VisualTreeHelper.GetOpenPopupsForXamlRoot(view.XamlRoot)
                    .Select(item => item.Child).OfType<FrameworkElement>()
                    .Single(root => ReferenceEquals(root, choice) || Descendants(root).Any(child => ReferenceEquals(child, choice)));
                receipt.Observations.Add(new
                {
                    stage = "timer-menu-expanded", timer = id.ToString(), popup = Bounds(popup, popup),
                    choice = Bounds(choice, popup), menuOpen = menu.IsOpen,
                });
                await CaptureAsync(popup, "timer-menu-" + id, receipt, token);
                var choicePeer = FrameworkElementAutomationPeer.CreatePeerForElement(choice)
                    ?? throw new InvalidOperationException("Duration choice has no real automation peer.");
                var inputPattern = "Invoke";
                if (choicePeer.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke) invoke.Invoke();
                else
                {
                    inputPattern = "Toggle";
                    (choicePeer.GetPattern(PatternInterface.Toggle) as IToggleProvider
                        ?? throw new InvalidOperationException("Duration choice has no Invoke or Toggle pattern.")).Toggle();
                }
                var timer = id == NativeWidgetId.FocusTimer ? widgets.FocusTimer : widgets.Countdown;
                await WaitUntilAsync(() => timer.Duration == TimeSpan.FromMinutes(10), token);
                menu.Hide();
                await WaitUntilAsync(() => !menu.IsOpen, token);
                Invoke(startButton);
                await WaitUntilAsync(() => timer.IsRunning, token);
                Invoke(startButton);
                await WaitUntilAsync(() => !timer.IsRunning, token);
                Invoke(durationButton);
                await WaitUntilAsync(() => menu.IsOpen, token);
                var beforeHide = closed;
                Present(false);
                await WaitUntilAsync(() => !menu.IsOpen && closed > beforeHide && !overlay.ObserveModuleOverlay().Visible, token);
                Present(true);
                await WaitUntilAsync(() => overlay.ObserveModuleOverlay() is { Visible: true, WidgetsActive: true }, token);
                Require(!menu.IsOpen, "A retired timer menu reopened with the retained overlay.");
                Invoke(durationButton);
                await WaitUntilAsync(() => menu.IsOpen, token);
                Require(choice.IsChecked, "The duration submenu lost its selected value after hide/reopen.");
                receipt.Observations.Add(new
                {
                    stage = "timer-reopened", timer = id.ToString(), durationMinutes = timer.Duration.TotalMinutes,
                    inputPattern, menuClosedEvents = closed, durationSelected = choice.IsChecked,
                    tile = Bounds(tile, tiles), durationButton = Bounds(durationButton, tile), startButton = Bounds(startButton, tile),
                    overlay = overlay.ObserveModuleOverlay(), scale = view.XamlRoot.RasterizationScale,
                });
                menu.Hide();
                await WaitUntilAsync(() => !menu.IsOpen, token);
            }
            finally { menu.Closed -= OnClosed; menu.Hide(); }
        }
        await CaptureAsync(overlay.DiagnosticSurface, "timers-reopened", receipt, token);
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
        await page.SelectDiagnosticSectionAsync("Space").WaitAsync(token);
        var list = Descendants(page).OfType<ListView>().Single(control => control.Name == "ItemsList");
        var cards = main.Items.Where(card => card.DropBatchId == batch).ToArray();
        Require(cards.Length == 3, "The real repository projection did not contain the synthetic batch.");
        var header = cards.Single(card => card.IsBatchHeader);
        list.ScrollIntoView(header);
        await WaitUntilAsync(() => list.ContainerFromItem(header) is ListViewItem { ActualWidth: > 0 }, token);
        var headerContainer = (ListViewItem)list.ContainerFromItem(header);
        Button Toggle() => Descendants(headerContainer).OfType<Button>().Single(button =>
            ReferenceEquals(button.Tag, header) && button.Content is string text && text == header.BatchToggleLabel);
        Invoke(Toggle());
        await WaitUntilAsync(() => cards.All(card => card.IsBatchMemberVisible) &&
            cards.All(card => list.ContainerFromItem(card) is ListViewItem { ActualHeight: > 0, Visibility: Visibility.Visible }), token);
        foreach (var card in cards) list.SelectedItems.Add(card);
        Require(list.SelectedItems.Count == 3, "The real ListView did not accept the expanded multi-selection.");
        var containers = cards.Select(card => (ListViewItem)list.ContainerFromItem(card)).ToArray();
        var anchor = main.Items.Single(card => card.Id == anchorItem.Id);
        list.ScrollIntoView(anchor);
        await WaitUntilAsync(() => list.ContainerFromItem(anchor) is ListViewItem { ActualHeight: > 0 }, token);
        var anchorContainer = (ListViewItem)list.ContainerFromItem(anchor);
        var scroll = Descendants(list).OfType<ScrollViewer>().First();
        list.UpdateLayout();
        var expandedAnchorY = anchorContainer.TransformToVisual(list).TransformPoint(new Point(0, 0)).Y;
        var expandedScrollOffset = scroll.VerticalOffset;
        var expandedContentY = expandedAnchorY + expandedScrollOffset;
        await CaptureAsync(page, "batch-expanded-selected", receipt, token);
        Stage(receipt, "batch-collapse");
        Invoke(Toggle());
        await WaitUntilAsync(() => cards.Where(card => !card.IsBatchHeader).All(card =>
            list.ContainerFromItem(card) is ListViewItem { Visibility: Visibility.Collapsed }) &&
            list.SelectedItems.Cast<ItemCardViewModel>().All(card => card.IsBatchMemberVisible), token);
        list.UpdateLayout();
        Require(list.SelectedItems.Count == 1 && ReferenceEquals(list.SelectedItems[0], header),
            "Collapsing the real batch did not retain only its selected header.");
        Require(main.SelectedItem is null || main.IsCurrentVisibleItem(main.SelectedItem),
            "Two-way SelectedItem retained a hidden batch member.");
        await WaitUntilAsync(() => anchorContainer.TransformToVisual(list).TransformPoint(new Point(0, 0)).Y + scroll.VerticalOffset < expandedContentY - 1, token);
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
