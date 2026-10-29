using DropSpace.Core.Island;
using DropSpace.Core.Models;
using DropSpace.Core.Overlay;
using DropSpace.Core.Policies;
using DropSpace.Core.Widgets;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class MusicWindowIntegrationTests
{
    [TestMethod]
    public void RestoreActivationKeepsPendingMusicSuppressionDismissalValid()
    {
        var coordinator = new IslandExperienceCoordinator();
        coordinator.UpdateSettings(new() { Resident = true, HideWhileMusicWindowVisible = true }, true);
        coordinator.UpdateMedia(true, true, true, 3000, "playing-track");
        for (var cycle = 0; cycle < 2; cycle++)
        {
            coordinator.UpdateMusicWindowVisibility(true); // native restore notification starts the animation
            var dismissal = coordinator.HideGeneration;
            coordinator.UpdateMusicWindowVisibility(true, activated: true); // Open's explicit activation
            Assert.IsTrue(coordinator.CompleteDismissal(dismissal));
            Assert.AreEqual(OverlayState.Hidden, coordinator.Current.State);
            coordinator.UpdateMusicWindowVisibility(false);
            Assert.AreEqual(OverlayState.Compact, coordinator.Current.State);
        }
        coordinator.UpdateMusicWindowVisibility(true);
        coordinator.CompleteDismissal(coordinator.HideGeneration);
        coordinator.Open(IslandPage.Widgets);
        coordinator.UpdateMusicWindowVisibility(true, activated: true);
        var manualDismissal = coordinator.HideGeneration;
        coordinator.UpdateMusicWindowVisibility(true, activated: true);
        Assert.IsFalse(coordinator.IsManuallyOpen);
        Assert.IsTrue(coordinator.CompleteDismissal(manualDismissal));
        Assert.AreEqual(OverlayState.Hidden, coordinator.Current.State);
    }

    [TestMethod]
    public void VisibleMusicWindowSuppressesAutomaticReasonsAndRestoresCurrentRules()
    {
        var coordinator = new IslandExperienceCoordinator();
        var settings = new IslandAppearanceSettings { Resident = true, ShowLogoWhenIdle = true,
            ForceShowOverFullscreen = true, HideWhileMusicWindowVisible = true };
        coordinator.UpdateSettings(settings, true);
        coordinator.UpdateMedia(true, true, true, 3000, "track1");
        coordinator.Open(IslandPage.Widgets);
        coordinator.UpdateMusicWindowVisibility(true);
        Assert.AreEqual(OverlayState.Dismissing, coordinator.Current.State);
        Assert.IsNull(coordinator.Current.NextDeadline);
        Assert.IsFalse(coordinator.IsManuallyOpen);
        Assert.IsTrue(coordinator.CompleteDismissal(coordinator.HideGeneration));
        coordinator.Notify(); coordinator.VolumeChanged();
        coordinator.UpdateFiles(new(OverlayState.Compact, 2, false, 1));
        coordinator.UpdateMedia(true, true, true, 3000, "track2");
        coordinator.UpdateSettings(settings, true);
        Assert.AreEqual(OverlayState.Hidden, coordinator.Current.State);
        coordinator.ClearNotifications(); coordinator.ClearVolume();
        coordinator.UpdateMusicWindowVisibility(false); // minimize/close
        Assert.AreEqual(OverlayState.Compact, coordinator.Current.State);
        Assert.IsFalse(coordinator.IsManuallyOpen); // no stale expanded window
        coordinator.UpdateMusicWindowVisibility(true);
        coordinator.CompleteDismissal(coordinator.HideGeneration);
        coordinator.UpdateSettings(settings with { HideWhileMusicWindowVisible = false }, true);
        Assert.AreEqual(OverlayState.Compact, coordinator.Current.State);
        coordinator.UpdateSettings(settings, true);
        coordinator.CompleteDismissal(coordinator.HideGeneration);
        coordinator.UpdateSettings(settings, false); // fullscreen still owns restoration
        coordinator.UpdateMusicWindowVisibility(false);
        Assert.AreEqual(OverlayState.Hidden, coordinator.Current.State);
    }

    [TestMethod]
    public void ManualRetrievalAndDragRemainAvailableWithoutDefeatingSuppression()
    {
        var coordinator = new IslandExperienceCoordinator();
        coordinator.UpdateSettings(new() { HideWhileMusicWindowVisible = true }, true);
        coordinator.UpdateMusicWindowVisibility(true);
        coordinator.Open(IslandPage.Clipboard);
        Assert.AreEqual(OverlayState.Expanded, coordinator.Current.State);
        coordinator.Collapse();
        coordinator.CompleteDismissal(coordinator.HideGeneration);
        coordinator.UpdateFiles(new(OverlayState.DragReady, 0, false, 1));
        Assert.AreEqual(OverlayState.DragReady, coordinator.Current.State);
        coordinator.UpdateFiles(new(OverlayState.Compact, 1, false, 2));
        Assert.AreEqual(OverlayState.Dismissing, coordinator.Current.State);
        coordinator.CompleteDismissal(coordinator.HideGeneration);
        coordinator.Open(IslandPage.Widgets);
        coordinator.UpdateMusicWindowVisibility(true, activated: true); // reopen existing window from widget
        Assert.IsFalse(coordinator.IsManuallyOpen);
        coordinator.CompleteDismissal(coordinator.HideGeneration);
        coordinator.DismissNow();
        coordinator.UpdateMusicWindowVisibility(false);
        Assert.AreEqual(OverlayState.Hidden, coordinator.Current.State); // explicit dismissal survives
    }

    [TestMethod]
    public void TranslationDefaultsUseEffectiveInterfaceUntilUserChoosesTarget()
    {
        foreach (var preference in Enum.GetValues<AppLanguagePreference>())
        {
            var expected = preference is AppLanguagePreference.System or AppLanguagePreference.SimplifiedChinese or AppLanguagePreference.TraditionalChinese
                ? LyricsTranslationTarget.SimplifiedChinese : LyricsTranslationTarget.English;
            Assert.AreEqual(expected, LyricsTranslationTargetPolicy.DefaultForInterface(preference, ["zh-HK"]));
        }
        Assert.AreEqual(LyricsTranslationTarget.English, LyricsTranslationTargetPolicy.DefaultForInterface(AppLanguagePreference.System, ["en-US", "zh-CN"]));
        var initial = new AppSettings { Language = AppLanguagePreference.English };
        var chinese = SettingsChangePolicy.Merge(initial, initial with { Language = AppLanguagePreference.SimplifiedChinese }, initial);
        chinese = LyricsTranslationTargetPolicy.ApplyDefault(chinese, ["en-US"]);
        Assert.AreEqual(LyricsTranslationTarget.SimplifiedChinese, chinese.LyricsTranslationTarget);
        Assert.IsFalse(chinese.LyricsTranslationTargetExplicit);
        var selected = SettingsChangePolicy.Merge(chinese, chinese with { LyricsTranslationTarget = LyricsTranslationTarget.English }, chinese);
        Assert.IsTrue(selected.LyricsTranslationTargetExplicit);
        Assert.AreEqual(LyricsTranslationTarget.English, LyricsTranslationTargetPolicy.ApplyDefault(selected, ["zh-CN"]).LyricsTranslationTarget);
        // A stale unrelated form cannot erase an explicit choice committed in the meantime.
        var merged = SettingsChangePolicy.Merge(chinese, chinese with { IslandAppearance = chinese.IslandAppearance with { HideWhileMusicWindowVisible = true } }, selected);
        Assert.IsTrue(merged.IslandAppearance.HideWhileMusicWindowVisible);
        Assert.IsTrue(merged.LyricsTranslationTargetExplicit);
        Assert.AreEqual(LyricsTranslationTarget.English, merged.LyricsTranslationTarget);
    }

    [TestMethod]
    public void MusicWidgetAddsToVacantCellWithoutMovingExistingWidgets()
    {
        Assert.IsFalse(new IslandAppearanceSettings().HideWhileMusicWindowVisible);
        var original = WidgetLayout.Default;
        Assert.IsTrue(WidgetLayoutPolicy.TryAdd(original, NativeWidgetId.MusicWindow, 0, 0, out var updated));
        CollectionAssert.AreEqual(original.Expanded.ToArray(), updated.Expanded.Take(original.Expanded.Count).ToArray());
        Assert.AreEqual(new WidgetSize(1, 1), WidgetCatalog.Sizes(NativeWidgetId.MusicWindow).Single());
        Assert.AreEqual(updated, WidgetLayoutPolicy.Normalize(updated));
        Assert.IsFalse(WidgetLayoutPolicy.TryAdd(updated, NativeWidgetId.MusicWindow, 0, 0, out _));
    }
}
