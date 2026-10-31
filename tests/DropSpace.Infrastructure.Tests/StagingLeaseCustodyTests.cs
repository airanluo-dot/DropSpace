using DropSpace.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class StagingLeaseCustodyTests
{
    private readonly AppStoragePaths _paths = new(Path.Combine(Path.GetTempPath(), "DropSpace-lease-custody", Guid.NewGuid().ToString("N")));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true);
    }

    [TestMethod]
    public async Task CompletedLeaseCannotDeleteAReusedRoot()
    {
        var store = new StagingLeaseStore(_paths, NullLogger<StagingLeaseStore>.Instance);
        var completed = await store.AcquireAsync("test", "shares/reused", false, allowExistingRoot: false);
        Assert.IsTrue(await store.CompleteAsync(completed));
        var current = await store.AcquireAsync("test", "shares/reused", false, allowExistingRoot: false);
        var sentinel = Path.Combine(current.RootPath, "new-owner.txt");
        await File.WriteAllTextAsync(sentinel, "new custody");

        Assert.IsTrue(await store.CompleteAsync(completed), "Repeated completion is idempotent.");
        Assert.AreEqual("new custody", await File.ReadAllTextAsync(sentinel));
        Assert.IsTrue(File.Exists(Path.Combine(_paths.StagingLeases, current.LeaseId + ".json")));
        Assert.IsTrue(await store.CompleteAsync(current));
    }

    [TestMethod]
    public async Task CompletedLeaseCannotRenewAndRecreateItsRoot()
    {
        var store = new StagingLeaseStore(_paths, NullLogger<StagingLeaseStore>.Instance);
        var completed = await store.AcquireAsync("test", "shares/completed", false);
        Assert.IsTrue(await store.CompleteAsync(completed));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.RenewAsync(completed));
        Assert.IsFalse(Directory.Exists(completed.RootPath));
        Assert.IsFalse(File.Exists(Path.Combine(_paths.StagingLeases, completed.LeaseId + ".json")));
    }

    [TestMethod]
    public async Task RenewalPreservesCustodyForTheOriginalHandle()
    {
        var store = new StagingLeaseStore(_paths, NullLogger<StagingLeaseStore>.Instance);
        var original = await store.AcquireAsync("test", "shares/renewed", false);
        var renewed = await store.RenewAsync(original, TimeSpan.FromHours(3));

        Assert.IsTrue(renewed.ExpiresAtUtc > original.ExpiresAtUtc);
        Assert.IsTrue(await store.CompleteAsync(original));
        Assert.IsFalse(Directory.Exists(renewed.RootPath));
        Assert.IsFalse(File.Exists(Path.Combine(_paths.StagingLeases, renewed.LeaseId + ".json")));
    }

    [TestMethod]
    public async Task ModifiedLeaseCannotRedirectCleanupToAnotherRoot()
    {
        var store = new StagingLeaseStore(_paths, NullLogger<StagingLeaseStore>.Instance);
        var original = await store.AcquireAsync("test", "shares/original", false);
        var other = await store.AcquireAsync("test", "shares/other", false);
        var sentinel = Path.Combine(other.RootPath, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "keep");

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            store.CompleteAsync(original with { RelativeRoot = other.RelativeRoot }));
        Assert.AreEqual("keep", await File.ReadAllTextAsync(sentinel));
        Assert.IsTrue(Directory.Exists(original.RootPath));
        Assert.IsTrue(await store.CompleteAsync(original));
        Assert.IsTrue(await store.CompleteAsync(other));
    }
}
