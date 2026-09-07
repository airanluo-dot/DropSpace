using DropSpace.Core.Models;
using DropSpace.Infrastructure.Settings;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class SettingsConcurrencyTests
{
    private readonly AppStoragePaths _paths = new(Path.Combine(Path.GetTempPath(), "DropSpace-settings-tests", Guid.NewGuid().ToString("N")));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true);
    }

    [TestMethod]
    public async Task IndependentConcurrentUpdatesPreserveEachOther()
    {
        var store = new JsonSettingsService(_paths);
        await store.SaveAsync(new AppSettings());
        var checkedAt = DateTimeOffset.UtcNow;
        await Task.WhenAll(
            store.UpdateAsync(current => current with { ClipboardPaused = true }),
            store.UpdateAsync(current => current with { LastUpdateCheckUtc = checkedAt }),
            store.UpdateAsync(current => current with { StartWithWindows = true }));
        var saved = await store.LoadAsync();
        Assert.IsTrue(saved.ClipboardPaused);
        Assert.IsTrue(saved.StartWithWindows);
        Assert.AreEqual(checkedAt, saved.LastUpdateCheckUtc);
    }

    [TestMethod]
    public async Task FailedMutationLeavesPersistedSettingsUntouchedAndGateReusable()
    {
        var store = new JsonSettingsService(_paths);
        await store.SaveAsync(new AppSettings { ClipboardPaused = true });
        var before = await File.ReadAllBytesAsync(_paths.Settings);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(_ => throw new InvalidOperationException("rejected")));
        CollectionAssert.AreEqual(before, await File.ReadAllBytesAsync(_paths.Settings));
        await store.UpdateAsync(current => current with { StartWithWindows = true });
        Assert.IsTrue((await store.LoadAsync()).ClipboardPaused);
    }
}
