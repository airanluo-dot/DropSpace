using DropSpace.Core.Models;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class SettingsUpdateCheckMergeRegressionTests
{
    [TestMethod]
    public void QueuedCheckMetadataPreservesPreferencesChangedBeforeDispatcherAppliesIt()
    {
        var checkedAt = new DateTimeOffset(2026, 9, 30, 8, 0, 0, TimeSpan.FromHours(8));
        var current = new AppSettings
        {
            Theme = ThemePreference.Dark,
            ClipboardPaused = true,
            EnableNearbySharing = true,
            QuickPanelHotkey = "Ctrl+Shift+Space"
        };

        var applied = SettingsChangePolicy.ApplyLastUpdateCheck(current, checkedAt);

        Assert.AreEqual(current with { LastUpdateCheckUtc = checkedAt.ToUniversalTime() }, applied);
        Assert.AreEqual(TimeSpan.Zero, applied.LastUpdateCheckUtc!.Value.Offset);
    }

    [TestMethod]
    public void DelayedOlderCheckCannotRegressNewerTimestampOrPreferences()
    {
        var older = new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero);
        var newer = older.AddDays(1);
        var current = new AppSettings { EnableInternetSharing = true, LastUpdateCheckUtc = newer };

        Assert.AreSame(current, SettingsChangePolicy.ApplyLastUpdateCheck(current, older));
        Assert.AreSame(current, SettingsChangePolicy.ApplyLastUpdateCheck(current, newer));
    }
}
