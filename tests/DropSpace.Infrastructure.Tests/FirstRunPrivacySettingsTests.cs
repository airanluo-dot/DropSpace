using DropSpace.Core.Models;
using DropSpace.Infrastructure.Settings;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class FirstRunPrivacySettingsTests
{
    private readonly AppStoragePaths _paths = new(Path.Combine(Path.GetTempPath(), "DropSpace-first-run-tests", Guid.NewGuid().ToString("N")));

    [TestCleanup]
    public void Cleanup() { if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true); }

    [TestMethod]
    public async Task FreshAndEmptySettingsRequireChoiceIncludingAfterReset()
    {
        var store = new JsonSettingsService(_paths);
        Assert.IsFalse((await store.LoadAsync()).PrivacyChoicesCompleted);
        await store.ResetUiSettingsAsync();
        Assert.IsFalse((await store.LoadAsync()).PrivacyChoicesCompleted);
        await File.WriteAllTextAsync(_paths.Settings, "{}");
        Assert.IsFalse((await store.LoadAsync()).PrivacyChoicesCompleted);
    }

    [TestMethod]
    public async Task ExplicitLegacyChoicesMigrateWithoutChangingThem()
    {
        _paths.EnsureCreated();
        await File.WriteAllTextAsync(_paths.Settings, "{\"ClipboardPaused\":true,\"StartWithWindows\":false,\"Lyrics\":{\"Enabled\":true}}");
        var store = new JsonSettingsService(_paths);
        // Reset runs before Load in the --safe-mode startup path.
        await store.ResetUiSettingsAsync();
        var settings = await store.LoadAsync();
        Assert.IsTrue(settings.PrivacyChoicesCompleted);
        Assert.IsTrue(settings.ClipboardPaused);
        Assert.IsFalse(settings.StartWithWindows);
        Assert.IsTrue(settings.Lyrics.Enabled);
    }

    [TestMethod]
    public async Task ExplicitUncompletedChoiceCannotBeMistakenForLegacyConsent()
    {
        var store = new JsonSettingsService(_paths);
        await store.SaveAsync(new AppSettings { PrivacyChoicesCompleted = false, ClipboardPaused = false, StartWithWindows = true });
        Assert.IsFalse((await store.LoadAsync()).PrivacyChoicesCompleted);
        await File.WriteAllTextAsync(_paths.Settings, "corrupt");
        Assert.IsFalse((await store.LoadAsync()).PrivacyChoicesCompleted);
    }
}
