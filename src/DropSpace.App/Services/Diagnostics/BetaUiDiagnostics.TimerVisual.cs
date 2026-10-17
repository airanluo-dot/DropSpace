using System.Runtime.ExceptionServices;
using DropSpace.App.ViewModels;
using DropSpace.App.Views.Island;
using DropSpace.Core.Island;
using DropSpace.Core.Overlay;
using DropSpace.Core.Widgets;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace DropSpace.App.Services.Diagnostics;

internal static partial class BetaUiDiagnostics
{
    private static async Task CheckTimerVisualAsync(OverlayWindow overlay, OverlayViewModel overlayView,
        IslandExperienceCoordinator island, WidgetViewModel widgets, BetaUiDiagnosticReceipt receipt, CancellationToken token)
    {
        void Present(bool visible)
        {
            if (visible) island.Open(IslandPage.Widgets); else island.DismissNow();
            overlay.ApplySnapshot(overlayView.Snapshot with { State = visible ? island.Current.State : OverlayState.Hidden },
                true, true, overlayView.FileDragWakeMode, overlayView.GetOverlayPlacement(overlay.MonitorId));
        }
        Stage(receipt, "timer-visual-cold-present");
        Present(true);
        await WaitAtStageAsync(receipt, "timer-visual-loaded",
            () => overlay.ObserveModuleOverlay() is { Loaded: true, Visible: true, WidgetsActive: true }, token);
        var view = Descendants(overlay.Content).OfType<WidgetsExpandedView>().Single();
        var tiles = Descendants(view).OfType<Grid>().Single(grid => grid.Name == "Tiles");
        await WaitAtStageAsync(receipt, "timer-visual-real-tiles", () => tiles.Children.Count == 2 &&
            tiles.Children.OfType<FrameworkElement>().All(tile => tile.ActualWidth > 0 && tile.ActualHeight > 0), token);
        Require(widgets.Layout.Expanded.All(item => item.ColumnSpan == 2 && item.RowSpan == 1),
            "The visual-only case did not retain the explicit 2 x 1 timer fixture.");
        var placement = widgets.Layout.Expanded.Single(item => item.Id == NativeWidgetId.FocusTimer);
        var tile = tiles.Children.OfType<FrameworkElement>().Single(element =>
            Grid.GetColumn(element) == placement.Column && Grid.GetRow(element) == placement.Row);
        var button = Descendants(tile).OfType<Button>().Single(control => control.Flyout is MenuFlyout);
        var menu = (MenuFlyout)button.Flyout;
        var submenu = menu.Items.OfType<MenuFlyoutSubItem>().Single();
        var choice = submenu.Items.OfType<ToggleMenuFlyoutItem>().First();
        var tracked = new List<TimerVisualPopup>();
        IExpandCollapseProvider? expand = null;
        var opening = 0;
        var opened = 0;
        var closed = 0;
        void OnOpening(object? sender, object args) => opening++;
        void OnOpened(object? sender, object args) => opened++;
        void OnClosed(object? sender, object args) => closed++;
        static bool Contains(DependencyObject root, DependencyObject item) =>
            ReferenceEquals(root, item) || Descendants(root).Any(child => ReferenceEquals(child, item));
        string Classify(Popup popup) => popup.Child is not { } child ? "unattributed-popup" :
            Contains(child, choice) ? "duration-descendant-popup" : Contains(child, submenu) ? "root-menu-popup" : "unattributed-popup";
        Popup[] OpenPopups() => VisualTreeHelper.GetOpenPopupsForXamlRoot(view.XamlRoot).ToArray();
        void TrackPopups()
        {
            foreach (var popup in OpenPopups())
            {
                var kind = Classify(popup);
                if (kind != "unattributed-popup" && tracked.All(item => !ReferenceEquals(item.Popup, popup)))
                    tracked.Add(new(popup, "popup-" + (tracked.Count + 1), kind));
            }
        }
        object PopupState()
        {
            TrackPopups();
            var current = OpenPopups();
            return new
            {
                rootMenu = new { isOpen = menu.IsOpen, openingEvents = opening, openedEvents = opened, closedEvents = closed },
                submenuState = expand?.ExpandCollapseState.ToString(),
                tracked = tracked.Select(item => item.Snapshot(current)).ToArray(),
                unrelatedOpenCount = current.Count(popup => Classify(popup) == "unattributed-popup"),
            };
        }
        bool PopupsRetired()
        {
            TrackPopups();
            var current = OpenPopups();
            return !menu.IsOpen && tracked.All(item => item.HasClosedAndUnloaded && !item.Popup.IsOpen &&
                !current.Any(popup => ReferenceEquals(popup, item.Popup))) &&
                current.All(popup => Classify(popup) == "unattributed-popup");
        }
        void Checkpoint(string stage, bool motionSettled)
        {
            var native = overlay.NativeVisibilityDiagnostics;
            receipt.VisualCheckpoints.Add(new
            {
                stage, captureOrder = "desktop-before-rtb", motionSettled,
                frameActive = overlay.HasActiveFrameSubscription,
                phase = native.Split(',').FirstOrDefault(part => part.StartsWith("phase=", StringComparison.Ordinal))?[6..],
                nativeVisibility = native,
                layers = Descendants(overlay.Content).OfType<FrameworkElement>()
                    .Where(element => element.Name is "Surface" or "ExpandedPanel" or "WidgetsExpanded")
                    .Select(PresentationState).ToArray(),
                popups = PopupState(),
            });
            WriteEvidence(receipt);
        }
        async Task CapturePoint(string name, FrameworkElement? popup = null)
        {
            await WaitForTimerPresentationAsync(overlay, "timer-visual-" + name, receipt, token);
            if (popup is not null)
                await WaitForStablePeerBoundsAsync(popup, "timer-visual-" + name + "-popup-bounds", receipt, token);
            Checkpoint(name, motionSettled: true);
            // Preserve the first desktop read before RTB can touch this surface.
            await CaptureDesktopAsync(overlay, popup, name, receipt, token);
            await CaptureAsync(popup ?? overlay.DiagnosticSurface, name, receipt, token);
        }
        menu.Opening += OnOpening;
        menu.Opened += OnOpened;
        menu.Closed += OnClosed;
        ExceptionDispatchInfo? primaryFailure = null;
        string? primaryStage = null;
        try
        {
            await CapturePoint("timers-initial");
            var closedBeforeOpen = closed;
            Stage(receipt, "timer-visual-open-root-menu-once");
            Invoke(button);
            await WaitAtStageAsync(receipt, "timer-visual-root-menu-realized", () => menu.IsOpen && opened == 1 &&
                submenu.IsLoaded && submenu.ActualWidth > 0 && submenu.ActualHeight > 0 &&
                OpenPopups().Any(popup => Classify(popup) == "root-menu-popup"), token, PopupState);
            // UIA Expand can run before a submenu has a real parent presenter.
            // Require its actual root popup and stable screen bounds first.
            await WaitForStablePeerBoundsAsync(submenu, "timer-visual-root-submenu-bounds", receipt, token);
            TrackPopups();
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(submenu)
                ?? throw new InvalidOperationException("The realized duration submenu has no automation peer.");
            expand = peer.GetPattern(PatternInterface.ExpandCollapse) as IExpandCollapseProvider
                ?? throw new InvalidOperationException("The realized duration submenu has no ExpandCollapse pattern.");
            Stage(receipt, "timer-visual-expand-submenu-once");
            expand.Expand();
            await WaitAtStageAsync(receipt, "timer-visual-descendant-popup-realized", () => choice.IsLoaded &&
                choice.ActualWidth > 0 && OpenPopups().Any(popup => Classify(popup) == "duration-descendant-popup"), token, PopupState);
            TrackPopups();
            var descendant = tracked.Single(item => item.Classification == "duration-descendant-popup");
            await CapturePoint("timer-menu-FocusTimer", descendant.Child);

            try
            {
                Stage(receipt, "timer-visual-hide-with-descendant-open");
                Present(false);
                await WaitAtStageAsync(receipt, "timer-visual-all-popups-retired", () =>
                    !overlay.ObserveModuleOverlay().Visible && closed > closedBeforeOpen && PopupsRetired(), token, PopupState);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                primaryFailure = ExceptionDispatchInfo.Capture(error);
                primaryStage = receipt.Stage;
            }
            try { Checkpoint(primaryFailure is null ? "hidden-popups-retired" : "hidden-popup-retirement-failed", motionSettled: false); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                receipt.Observations.Add(new { stage = "hidden-checkpoint", outcome = "evidence-failed", failure = Describe(error) });
                if (primaryFailure is null) { primaryFailure = ExceptionDispatchInfo.Capture(error); primaryStage = receipt.Stage; }
            }
            // Keep a final picture even when root Closed did not retire the child.
            // Do not explicitly Collapse/close that child and thereby erase evidence.
            try
            {
                Stage(receipt, "timer-visual-reopen-for-final-evidence");
                Present(true);
                await CapturePoint("timers-reopened");
                Stage(receipt, "timer-visual-reopened-popup-state");
                Require(PopupsRetired(), "A tracked root or duration-descendant Popup remained open after hide/reopen.");
                Require(opening == 1 && opened == 1, "The visual-only case did not preserve its single root-menu opening cycle.");
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                receipt.Observations.Add(new { stage = receipt.Stage, outcome = "final-evidence-failed", failure = Describe(error) });
                if (primaryFailure is null) { primaryFailure = ExceptionDispatchInfo.Capture(error); primaryStage = receipt.Stage; }
            }
            if (primaryFailure is not null)
            {
                receipt.Stage = primaryStage!;
                primaryFailure.Throw();
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Early presentation/open/expand failures must also survive a later
            // native retirement error in the finally block.
            primaryFailure ??= ExceptionDispatchInfo.Capture(error);
            primaryStage ??= receipt.Stage;
            throw;
        }
        finally
        {
            menu.Opening -= OnOpening;
            menu.Opened -= OnOpened;
            menu.Closed -= OnClosed;
            foreach (var popup in tracked) popup.Dispose();
            // Normal production retirement runs after evidence; no product opacity or
            // submenu state is forced to manufacture an image or successful assertion.
            try { Present(false); }
            catch (Exception error) when (error is not OutOfMemoryException && primaryFailure is not null)
            {
                receipt.Exceptions.Add("Timer visual retirement after primary failure: " + Describe(error));
                receipt.Observations.Add(new { stage = "timer-visual-retire-after-failure", failure = Describe(error) });
            }
        }
    }

    private static async Task WaitForTimerPresentationAsync(OverlayWindow overlay, string stage,
        BetaUiDiagnosticReceipt receipt, CancellationToken token)
    {
        Stage(receipt, stage + "-motion-settled");
        await overlay.WaitForMotionSettledAsync(token).WaitAsync(TimeSpan.FromSeconds(15), token);
        Require(!overlay.HasActiveFrameSubscription && overlay.ObserveModuleOverlay().Visible,
            "The Overlay did not settle into its visible presentation.");
        overlay.DiagnosticSurface.UpdateLayout();
        Stage(receipt, stage + "-render-and-commit");
        await WaitForTimerRenderingAsync(token);
        await ElementCompositionPreview.GetElementVisual(overlay.DiagnosticSurface).Compositor.RequestCommitAsync()
            .AsTask().WaitAsync(TimeSpan.FromSeconds(5), token);
        await WaitForTimerRenderingAsync(token);
        Require(!overlay.HasActiveFrameSubscription, "Overlay motion restarted before desktop capture.");
    }

    private static async Task WaitForTimerRenderingAsync(CancellationToken token)
    {
        var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnRendering(object? sender, object args) => frame.TrySetResult();
        CompositionTarget.Rendering += OnRendering;
        try { await frame.Task.WaitAsync(TimeSpan.FromSeconds(5), token); }
        finally { CompositionTarget.Rendering -= OnRendering; }
    }

    private static async Task WaitForStablePeerBoundsAsync(FrameworkElement element, string stage,
        BetaUiDiagnosticReceipt receipt, CancellationToken token)
    {
        Stage(receipt, stage);
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(element)
            ?? throw new InvalidOperationException("The realized control has no real automation peer for screen bounds.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        Windows.Foundation.Rect? previous = null;
        var stable = 0;
        while (stable < 2)
        {
            await WaitForTimerRenderingAsync(deadline.Token);
            var bounds = peer.GetBoundingRectangle();
            receipt.Observations.Add(new { stage, peerType = peer.GetType().FullName, rawScreenBounds = RawScreenBounds(bounds) });
            var valid = double.IsFinite(bounds.X) && double.IsFinite(bounds.Y) &&
                double.IsFinite(bounds.Width) && double.IsFinite(bounds.Height) && bounds.Width > 0 && bounds.Height > 0;
            stable = element.IsLoaded && valid && previous == bounds ? stable + 1 : 0;
            previous = bounds;
        }
    }

    private sealed class TimerVisualPopup : IDisposable
    {
        internal Popup Popup { get; }
        internal FrameworkElement Child { get; }
        internal string Classification { get; }
        private readonly string _id;
        private readonly bool _openAtDiscovery;
        private int _opened, _closed;
        internal bool HasClosedAndUnloaded => _closed > 0 && !Child.IsLoaded;
        internal TimerVisualPopup(Popup popup, string id, string classification)
        {
            Popup = popup;
            Child = popup.Child as FrameworkElement ?? throw new InvalidOperationException("The observed real Popup has no FrameworkElement child.");
            _id = id; Classification = classification; _openAtDiscovery = popup.IsOpen;
            popup.Opened += OnOpened; popup.Closed += OnClosed;
        }
        private void OnOpened(object? sender, object args) => _opened++;
        private void OnClosed(object? sender, object args) => _closed++;
        internal object Snapshot(Popup[] current)
        {
            string? peerType = null, error = null;
            object? bounds = null;
            try
            {
                var peer = FrameworkElementAutomationPeer.CreatePeerForElement(Child);
                peerType = peer?.GetType().FullName;
                if (peer is not null) bounds = RawScreenBounds(peer.GetBoundingRectangle());
            }
            catch (Exception failure) when (failure is not OutOfMemoryException) { error = Describe(failure); }
            return new { id = _id, classification = Classification, isOpen = Popup.IsOpen,
                enumeratedOpen = current.Any(popup => ReferenceEquals(popup, Popup)),
                openedAfterTracking = _opened, closedAfterTracking = _closed, openAtDiscovery = _openAtDiscovery,
                childLoaded = Child.IsLoaded, closedAndUnloaded = HasClosedAndUnloaded,
                rawScreenBounds = bounds, peerType, boundsError = error };
        }
        public void Dispose() { Popup.Opened -= OnOpened; Popup.Closed -= OnClosed; }
    }
}
