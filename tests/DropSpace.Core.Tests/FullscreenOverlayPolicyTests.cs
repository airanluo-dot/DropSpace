using System.Text.Json;
using DropSpace.Core.Models;
using DropSpace.Core.Island;
using DropSpace.Core.Overlay;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class FullscreenOverlayPolicyTests
{
    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"IslandAppearance\":{}}")]
    [DataRow("{\"IslandAppearance\":null}")]
    [DataRow("{\"Version\":13,\"IslandAppearance\":{\"AutoHide\":false}}")]
    public void ExistingSettingsNeverOptIntoFullscreenOverride(string json)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(json)!.Validate();
        Assert.IsFalse(settings.IslandAppearance.ForceShowOverFullscreen);
    }

    [TestMethod]
    public void FreshSettingsDefaultOff()
    {
        Assert.IsFalse(new AppSettings().IslandAppearance.ForceShowOverFullscreen);
        Assert.IsTrue(new AppSettings().SystemActivities.SuppressOverFullscreen);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void SavedOverrideRoundTripsWithoutChangingOtherChoices(bool suppressFullscreen)
    {
        var original = new AppSettings
        {
            IslandAppearance = new() { ForceShowOverFullscreen = true, AutoHide = true },
            IslandActivity = new() { EnableMediaActivity = false },
            SystemActivities = new() { SuppressOverFullscreen = suppressFullscreen },
            Lyrics = new() { Enabled = false, AiTranslationEnabled = false },
            PrivacyChoicesCompleted = false,
        };
        var loaded = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(original))!.Validate();
        Assert.IsTrue(loaded.IslandAppearance.ForceShowOverFullscreen);
        Assert.IsTrue(loaded.IslandAppearance.AutoHide);
        Assert.AreEqual(suppressFullscreen, loaded.SystemActivities.SuppressOverFullscreen);
        Assert.IsFalse(loaded.IslandActivity.EnableMediaActivity);
        Assert.IsFalse(loaded.SystemActivities.ShowWindowsNotifications);
        Assert.IsFalse(loaded.SystemActivities.ShowVolumeChanges);
        Assert.IsFalse(loaded.Lyrics.Enabled);
        Assert.IsFalse(loaded.Lyrics.AiTranslationEnabled);
        Assert.IsFalse(loaded.PrivacyChoicesCompleted);
    }

    [TestMethod]
    public void DisabledOverrideExactlyPreservesPreviousSuppressionMatrix()
    {
        foreach (var state in Enum.GetValues<OverlayState>())
        foreach (var suppress in new[] { false, true })
        foreach (var fullscreen in new[] { false, true })
        {
            var actual = FullscreenOverlayPolicy.Resolve(state, false, suppress, fullscreen);
            var expected = suppress && fullscreen && state is not (OverlayState.DragApproaching or OverlayState.DragReady);
            Assert.AreEqual(expected, actual.Suppress, $"{state}/{suppress}/{fullscreen}");
            Assert.AreEqual(state, actual.State);
            Assert.IsFalse(actual.KeepTopmost);
            Assert.AreEqual(state == OverlayState.Expanded && !expected, actual.AllowActivation);
        }
    }

    [TestMethod]
    public void EnabledOverrideNeverSuppressesOrActivatesOverFullscreen()
    {
        foreach (var state in Enum.GetValues<OverlayState>())
        foreach (var suppress in new[] { false, true })
        {
            var actual = FullscreenOverlayPolicy.Resolve(state, true, suppress, true);
            Assert.IsFalse(actual.Suppress);
            Assert.IsTrue(actual.KeepTopmost);
            Assert.IsFalse(actual.AllowActivation);
            Assert.AreEqual(state is OverlayState.Hidden or OverlayState.Dismissing ? OverlayState.Compact : state,
                actual.State);
        }
    }

    [TestMethod]
    public void EnabledOverrideDoesNotForcePresenceOrPreventActivationOnOrdinaryDesktop()
    {
        foreach (var state in Enum.GetValues<OverlayState>())
        {
            var actual = FullscreenOverlayPolicy.Resolve(state, true, true, false);
            Assert.AreEqual(state, actual.State);
            Assert.IsFalse(actual.KeepTopmost);
            Assert.IsFalse(actual.Suppress);
            Assert.AreEqual(state == OverlayState.Expanded, actual.AllowActivation);
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void RepeatedToggleAndFullscreenExitRestoreSavedPolicy(bool savedSuppression)
    {
        foreach (var state in Enum.GetValues<OverlayState>())
        {
            var original = FullscreenOverlayPolicy.Resolve(state, false, savedSuppression, true);
            for (var iteration = 0; iteration < 5; iteration++)
            {
                var forced = FullscreenOverlayPolicy.Resolve(state, true, savedSuppression, true);
                Assert.IsFalse(forced.Suppress);
                Assert.IsTrue(forced.KeepTopmost);
                Assert.AreEqual(original, FullscreenOverlayPolicy.Resolve(state, false, savedSuppression, true));
                var desktop = FullscreenOverlayPolicy.Resolve(state, true, savedSuppression, false);
                Assert.AreEqual(state, desktop.State);
                Assert.IsFalse(desktop.KeepTopmost);
            }
        }
    }

    [TestMethod]
    public void AutomaticMonitorFollowsFullscreenThenRestoresPreviousMonitor()
    {
        Assert.AreEqual("game", SelectMonitor(OverlayMonitorPreference.Automatic, true, "game", false));
        Assert.AreEqual("previous", SelectMonitor(OverlayMonitorPreference.Automatic, true, null, false));
        Assert.AreEqual("previous", SelectMonitor(OverlayMonitorPreference.Automatic, false, "game", false));
    }

    [TestMethod]
    public void PrimaryPreferenceAndDragOwnershipTakePriorityOverFullscreenMonitor()
    {
        Assert.AreEqual("previous", SelectMonitor(OverlayMonitorPreference.Primary, true, "game", false));
        Assert.AreEqual("previous", SelectMonitor(OverlayMonitorPreference.Automatic, true, "game", true));
        Assert.AreEqual("previous", SelectMonitor(OverlayMonitorPreference.Primary, true, "game", true));
    }

    [TestMethod]
    public void ForceOnFullMonitorDoesNotSuppressOtherMonitorOrTreatDesktopAsFullscreen()
    {
        var sameMonitor = FullscreenOverlayPolicy.Resolve(OverlayState.Hidden, true, true, true);
        var otherMonitor = FullscreenOverlayPolicy.Resolve(OverlayState.Hidden, true, true, false);
        Assert.AreEqual(OverlayState.Compact, sameMonitor.State);
        Assert.AreEqual(OverlayState.Hidden, otherMonitor.State);
        Assert.IsFalse(otherMonitor.KeepTopmost);
    }

    [TestMethod]
    public void ProjectedIdleSurfaceCanOpenAndCollapseWithoutRetainingIdlePresence()
    {
        var coordinator = new IslandExperienceCoordinator();
        Assert.AreEqual(OverlayState.Hidden, coordinator.Current.State);
        var presented = FullscreenOverlayPolicy.Resolve(coordinator.Current.State, true, true, true);
        Assert.AreEqual(OverlayState.Compact, presented.State);

        // OnCompactClicked opens the ordinary manual experience when the file
        // state machine has no items to expand. No fake file/media state is needed.
        coordinator.Open(IslandPage.Files);
        presented = FullscreenOverlayPolicy.Resolve(coordinator.Current.State, true, true, true);
        Assert.AreEqual(OverlayState.Expanded, presented.State);
        Assert.IsFalse(presented.AllowActivation);
        Assert.IsFalse(coordinator.Current.MediaPresent);
        coordinator.Collapse();
        Assert.AreEqual(OverlayState.Hidden, coordinator.Current.State);
        Assert.AreEqual(OverlayState.Compact,
            FullscreenOverlayPolicy.Resolve(coordinator.Current.State, true, true, true).State);
        Assert.AreEqual(OverlayState.Hidden,
            FullscreenOverlayPolicy.Resolve(coordinator.Current.State, false, true, true).State);
    }

    [TestMethod]
    public void DragAndCollapseRoundTripDoNotDestroyFullscreenIdleProjection()
    {
        var coordinator = new IslandExperienceCoordinator();
        coordinator.UpdateFiles(new(OverlayState.DragApproaching, 0, false, 1));
        Assert.AreEqual(OverlayState.DragApproaching,
            FullscreenOverlayPolicy.Resolve(coordinator.Current.State, true, true, true).State);
        coordinator.UpdateFiles(new(OverlayState.Hidden, 0, false, 2));
        Assert.AreEqual(OverlayState.Compact,
            FullscreenOverlayPolicy.Resolve(coordinator.Current.State, true, true, true).State);
        Assert.AreEqual(OverlayState.Hidden,
            FullscreenOverlayPolicy.Resolve(coordinator.Current.State, true, true, false).State);
    }

    private static string? SelectMonitor(OverlayMonitorPreference preference, bool force, string? fullscreen, bool dragging) =>
        FullscreenOverlayPolicy.ResolveMonitorId("previous", preference, force, fullscreen, dragging);
}
