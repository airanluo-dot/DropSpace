using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using DropSpace.App.ViewModels;
using DropSpace.Core.Island;
using DropSpace.Core.Overlay;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace DropSpace.App.Services.Diagnostics;

internal static partial class BetaUiDiagnostics
{
    private static async Task CheckOverlayLifecycleAsync(OverlayWindow overlay, OverlayViewModel overlayView,
        IslandExperienceCoordinator island, Action releaseRunnerReference, BetaUiDiagnosticReceipt receipt,
        CancellationToken token)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(overlay);
        var appWindow = overlay.AppWindow;
        var closingEvents = 0;
        var closedEvents = 0;
        var cancelObserved = false;
        var closeRequestPosted = false;
        var shutdownRequested = false;
        var windowClosed = false;
        var bridgeEnabled = true;
        var closingObserverAttached = false;
        var closedObserverAttached = false;
        var experienceObserverAttached = false;
        var experienceChangedEvents = 0;
        var eventNumber = 0;
        var events = new List<object>();
        ExceptionDispatchInfo? primaryFailure = null;
        string? primaryStage = null;
        void Event(string kind, object detail)
        {
            eventNumber++;
            if (events.Count < 24) events.Add(new { sequence = eventNumber, kind, detail });
        }
        void Failure(Exception error, string stage)
        {
            primaryFailure ??= ExceptionDispatchInfo.Capture(error);
            primaryStage ??= stage;
            receipt.Observations.Add(new { stage, outcome = "failed", failure = Describe(error) });
        }
        void OnExperienceChanged(object? sender, IslandExperienceSnapshot snapshot)
        {
            experienceChangedEvents++;
            Event("experience-changed", new { state = snapshot.State.ToString(), snapshot.Revision, bridgeEnabled });
            if (!bridgeEnabled || windowClosed || shutdownRequested) return;
            // A single-window fixture bridge follows the production service's state
            // projection. It deliberately does not start global hotkeys/drag listeners.
            overlay.ApplySnapshot(overlayView.Snapshot with { State = snapshot.State },
                true, true, overlayView.FileDragWakeMode, overlayView.GetOverlayPlacement(overlay.MonitorId));
        }
        void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            closingEvents++;
            cancelObserved |= args.Cancel;
            // Production subscribed during construction; this observer never writes
            // Cancel or dismisses the experience on behalf of the closing handler.
            Event("app-window-closing", new { args.Cancel, shutdownRequested });
        }
        void DetachClosingObserver()
        {
            if (!closingObserverAttached) return;
            closingObserverAttached = false;
            try { appWindow.Closing -= OnClosing; }
            catch (Exception error) when (error is not OutOfMemoryException)
            { receipt.Exceptions.Add("Lifecycle closing-observer retirement: " + Describe(error)); }
        }
        void DetachExperienceObserver()
        {
            bridgeEnabled = false;
            if (!experienceObserverAttached) return;
            experienceObserverAttached = false;
            island.Changed -= OnExperienceChanged;
        }
        void DetachClosedObserver()
        {
            if (!closedObserverAttached) return;
            closedObserverAttached = false;
            try { overlay.Closed -= OnClosed; }
            catch (Exception error) when (error is not OutOfMemoryException)
            { receipt.Exceptions.Add("Lifecycle closed-observer retirement: " + Describe(error)); }
        }
        void OnClosed(object sender, WindowEventArgs args)
        {
            closedEvents++;
            windowClosed = true;
            bridgeEnabled = false;
            releaseRunnerReference();
            Event("window-closed", new { shutdownRequested });
            DetachExperienceObserver();
            // Observer detachment runs within the Closed callback. Later runner code
            // uses only the saved HWND and event data, never this closed XAML surface.
            DetachClosingObserver();
            DetachClosedObserver();
        }
        OverlayLifecycleNativeState NativeState()
        {
            if (!DesktopNative.IsWindow(hwnd)) return new(false, 0, null, null);
            DesktopNative.GetWindowThreadProcessId(hwnd, out var processId);
            var regionType = LifecycleNative.GetWindowRgnBox(hwnd, out var rectangle);
            return new(true, processId, regionType, new(rectangle.Left, rectangle.Top,
                rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top));
        }
        bool Alive() => !windowClosed && NativeState() is { IsWindow: true } native && native.ProcessId == Environment.ProcessId;
        object LifecycleState() => new
        {
            hwnd = Hwnd(hwnd), processId = Environment.ProcessId, closingEvents, closedEvents, cancelObserved,
            closeRequestPosted, shutdownRequested, experienceChangedEvents, experienceState = island.Current.State.ToString(),
            native = NativeState(), eventSequence = events.ToArray(), eventSequenceTruncated = eventNumber > events.Count,
        };
        void Checkpoint(string stage, object[] layers, string? nativeVisibility = null,
            bool? frameActive = null, bool motionSettled = false)
        {
            receipt.VisualCheckpoints.Add(new
            {
                stage, captureOrder = "frozen-opacity-then-desktop-before-rtb", motionSettled, frameActive,
                phase = nativeVisibility?.Split(',').FirstOrDefault(part => part.StartsWith("phase=", StringComparison.Ordinal))?[6..],
                nativeVisibility, layers, lifecycle = LifecycleState(),
            });
            WriteEvidence(receipt);
        }
        async Task CapturePresentation(string name)
        {
            Require(Alive(), "The owned Overlay HWND was destroyed before presentation evidence could be captured.");
            Stage(receipt, "overlay-lifecycle-" + name + "-motion-settled");
            await overlay.WaitForMotionSettledAsync(token).WaitAsync(TimeSpan.FromSeconds(15), token);
            Require(Alive(), "The owned Overlay HWND was destroyed while waiting for presentation.");
            Require(!overlay.HasActiveFrameSubscription && overlay.ObserveModuleOverlay().Visible,
                "The owned Overlay did not settle into a visible presentation.");
            overlay.DiagnosticSurface.UpdateLayout();
            Stage(receipt, "overlay-lifecycle-" + name + "-render-and-commit");
            await WaitForTimerRenderingAsync(token);
            Require(Alive(), "The owned Overlay closed before composition commit.");
            await ElementCompositionPreview.GetElementVisual(overlay.DiagnosticSurface).Compositor.RequestCommitAsync()
                .AsTask().WaitAsync(TimeSpan.FromSeconds(5), token);
            Require(Alive(), "The owned Overlay closed during composition commit.");
            await WaitForTimerRenderingAsync(token);
            Require(Alive(), "The owned Overlay closed before its first-frame snapshot.");
            Require(!overlay.HasActiveFrameSubscription && overlay.DiagnosticSurface.ActualWidth > 0 &&
                overlay.DiagnosticSurface.ActualHeight > 0, "The owned Overlay did not retain settled, nonzero geometry.");
            var elements = Descendants(overlay.Content).OfType<FrameworkElement>()
                .Where(element => element.Name is "Surface" or "ExpandedPanel" or "WidgetsExpanded").ToArray();
            var expanded = elements.Single(element => element.Name == "ExpandedPanel");
            // Freeze this value before ANY RTB or UIA call. RTB was observed to change
            // later opacity, so a fresh read after capture cannot prove first paint.
            var frozenExpandedOpacity = ElementCompositionPreview.GetElementVisual(expanded).Opacity;
            var frozenLayers = elements.Select(PresentationState).ToArray();
            var frozenGeometryReady = elements.Length == 3 && elements.All(element =>
                element.IsLoaded && element.Visibility == Visibility.Visible &&
                element.ActualWidth > 0 && element.ActualHeight > 0);
            Checkpoint(name, frozenLayers, overlay.NativeVisibilityDiagnostics,
                overlay.HasActiveFrameSubscription, motionSettled: true);
            await CaptureDesktopAsync(overlay, null, name, receipt, token);
            Require(Alive(), "The Overlay was closed during desktop capture; RTB on a destroyed surface was skipped.");
            await CaptureAsync(overlay.DiagnosticSurface, name, receipt, token, Alive);
            Stage(receipt, "overlay-lifecycle-" + name + "-frozen-opacity");
            Require(frozenGeometryReady,
                "The immutable pre-RTB Surface/ExpandedPanel/WidgetsExpanded snapshot was not loaded, visible and nonzero.");
            Require(frozenExpandedOpacity > .99f,
                "The immutable pre-RTB ExpandedPanel composition opacity was not above 0.99: " + frozenExpandedOpacity);
        }

        try
        {
            // Observe after the production constructor installed its close policy.
            appWindow.Closing += OnClosing;
            closingObserverAttached = true;
            overlay.Closed += OnClosed;
            closedObserverAttached = true;
            island.Changed += OnExperienceChanged;
            experienceObserverAttached = true;
            try
            {
                Stage(receipt, "overlay-lifecycle-cold-open");
                Require(Alive(), "The fixture did not own a live Overlay HWND.");
                island.Open(IslandPage.Widgets);
                await CapturePresentation("timers-initial");
            }
            catch (Exception error) when (error is not OutOfMemoryException) { Failure(error, receipt.Stage); }

            if (Alive())
            {
                try
                {
                    Stage(receipt, "overlay-lifecycle-post-system-close");
                    Require(WinRT.Interop.WindowNative.GetWindowHandle(overlay) == hwnd, "The Overlay HWND changed before SC_CLOSE.");
                    // Programmatic native system-close request, not physical Alt+F4.
                    closeRequestPosted = LifecycleNative.PostMessageW(hwnd, 0x0112, 0xF060, 0);
                    if (!closeRequestPosted) throw DesktopError("PostMessageW(WM_SYSCOMMAND/SC_CLOSE)");
                    Event("system-close-posted", new { message = "WM_SYSCOMMAND", command = "SC_CLOSE", ownedHwnd = Hwnd(hwnd) });
                    await WaitAtStageAsync(receipt, "overlay-lifecycle-system-close-observed",
                        () => closingEvents > 0 || windowClosed, token, LifecycleState);
                    Require(!windowClosed, "The ordinary system-close request destroyed the reusable Overlay.");
                    Require(closingEvents == 1 && cancelObserved,
                        "The production AppWindow.Closing handler did not observably cancel the single system-close request.");
                    await WaitAtStageAsync(receipt, "overlay-lifecycle-hidden-retained", () => windowClosed ||
                        island.Current.State == OverlayState.Hidden && !overlay.ObserveModuleOverlay().Visible &&
                        !overlay.HasActiveFrameSubscription && NativeState().RegionType == 1, token, LifecycleState);
                    Require(Alive() && closedEvents == 0, "The hidden Overlay HWND was not retained by the same process.");
                    // Hidden intentionally retains a WS_VISIBLE HWND with NULLREGION.
                    // IsWindowVisible=false is not the product's ordinary hide policy.
                    Checkpoint("system-close-hidden-retained", [], overlay.NativeVisibilityDiagnostics,
                        overlay.HasActiveFrameSubscription, motionSettled: true);
                }
                catch (Exception error) when (error is not OutOfMemoryException) { Failure(error, receipt.Stage); }
            }
            else Failure(new InvalidOperationException("System close was skipped because the owned HWND had already closed."), "overlay-lifecycle-system-close-unavailable");

            if (Alive())
            {
                try
                {
                    Stage(receipt, "overlay-lifecycle-reopen-same-window");
                    island.Open(IslandPage.Widgets);
                    Require(WinRT.Interop.WindowNative.GetWindowHandle(overlay) == hwnd && closedEvents == 0,
                        "Reopening did not retain the original Window object and HWND.");
                    await CapturePresentation("timers-reopened");
                }
                catch (Exception error) when (error is not OutOfMemoryException) { Failure(error, receipt.Stage); }
            }
            else Failure(new InvalidOperationException("Reopen/capture was skipped for the destroyed Overlay instance."), "overlay-lifecycle-reopen-unavailable");

            if (Alive())
            {
                try
                {
                    Stage(receipt, "overlay-lifecycle-explicit-shutdown");
                    DetachExperienceObserver();
                    DetachClosingObserver();
                    shutdownRequested = true;
                    Event("explicit-shutdown-requested", new { ownedHwnd = Hwnd(hwnd) });
                    overlay.CloseForShutdown();
                    await WaitAtStageAsync(receipt, "overlay-lifecycle-hwnd-destroyed",
                        () => windowClosed && !DesktopNative.IsWindow(hwnd), token, LifecycleState);
                    Require(closedEvents == 1, "Explicit shutdown did not produce exactly one Window.Closed event.");
                    Checkpoint("explicit-shutdown-destroyed", []);
                }
                catch (Exception error) when (error is not OutOfMemoryException) { Failure(error, receipt.Stage); }
            }
            else Failure(new InvalidOperationException("Explicit shutdown was unavailable because the Overlay was already destroyed."), "overlay-lifecycle-shutdown-unavailable");
        }
        catch (Exception error) when (error is not OutOfMemoryException) { Failure(error, receipt.Stage); }
        finally
        {
            DetachExperienceObserver();
            if (!windowClosed && DesktopNative.IsWindow(hwnd))
            {
                DetachClosingObserver();
                DetachClosedObserver();
            }
            else if (!windowClosed)
            {
                // Do not hand an already-destroyed native instance to outer cleanup,
                // even if a broken event path failed to deliver Window.Closed.
                releaseRunnerReference();
                receipt.Exceptions.Add("The lifecycle HWND disappeared without an observed Window.Closed event.");
            }
        }
        if (primaryFailure is not null)
        {
            receipt.Stage = primaryStage!;
            primaryFailure.Throw();
        }
    }

    private sealed record OverlayLifecycleNativeState(bool IsWindow, uint ProcessId, int? RegionType, BetaUiDesktopBounds? RegionBounds);
    private static class LifecycleNative
    {
        [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern int GetWindowRgnBox(nint hwnd, out DesktopNative.Rect rectangle);
    }
}
