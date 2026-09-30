using DropSpace.App.Services;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Settings;
using DropSpace.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class SettingsLastCheckRegressionTests
{
    [TestMethod]
    public async Task UpdateCheckReturnsPreferencesSavedAfterItsOriginalSnapshot()
    {
        var paths = new AppStoragePaths(Path.Combine(Path.GetTempPath(), "DropSpace-last-check-tests", Guid.NewGuid().ToString("N")));
        try
        {
            var store = new JsonSettingsService(paths);
            var original = new AppSettings();
            await store.SaveAsync(original);
            using var coordinator = new SettingsApplicationCoordinator(
                store, null!, null!, null!, null!, null!, NullLogger<SettingsApplicationCoordinator>.Instance);
            var latest = original with { EnableNearbySharing = true, Theme = ThemePreference.Dark, ClipboardPaused = true };
            await coordinator.SaveAsync(latest);
            var checkedAt = DateTimeOffset.Parse("2026-09-30T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

            var returned = await coordinator.UpdateLastCheckAsync(original, checkedAt);
            var persisted = await store.LoadAsync();

            Assert.IsTrue(returned.EnableNearbySharing, "An automatic update check must not replace a newer sharing preference with its old snapshot.");
            Assert.AreEqual(ThemePreference.Dark, returned.Theme);
            Assert.IsTrue(returned.ClipboardPaused);
            Assert.AreEqual(checkedAt, returned.LastUpdateCheckUtc);
            Assert.AreEqual(persisted.EnableNearbySharing, returned.EnableNearbySharing);
            Assert.AreEqual(persisted.Theme, returned.Theme);
            Assert.AreEqual(persisted.ClipboardPaused, returned.ClipboardPaused);
        }
        finally
        {
            if (Directory.Exists(paths.Root)) Directory.Delete(paths.Root, true);
        }
    }
}
