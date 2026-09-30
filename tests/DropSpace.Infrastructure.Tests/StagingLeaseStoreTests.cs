using DropSpace.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class StagingLeaseStoreTests
{
    private AppStoragePaths _paths = null!;

    [TestInitialize]
    public void Initialize()
    {
        _paths = new AppStoragePaths(Path.Combine(Path.GetTempPath(), "DropSpace-lease-tests", Guid.NewGuid().ToString("N")));
        _paths.EnsureCreated();
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, recursive: true);
    }

    [TestMethod]
    public async Task CompleteDeletesRootBeforeLeaseRecord()
    {
        var store = CreateStore();
        var lease = await store.AcquireAsync("test", "shares/complete", sensitivePlaintext: true);
        await File.WriteAllTextAsync(Path.Combine(lease.RootPath, "plain.txt"), "sensitive");

        Assert.AreEqual(1, Directory.GetFiles(_paths.StagingLeases, "*.json").Length);
        Assert.IsTrue(await store.CompleteAsync(lease));
        Assert.IsFalse(Directory.Exists(lease.RootPath));
        Assert.AreEqual(0, Directory.GetFiles(_paths.StagingLeases, "*.json").Length);
    }

    [TestMethod]
    public async Task ALeaseFromAnAbandonedOwnerIsRecoveredWithoutTouchingExternalSentinel()
    {
        var abandonedStore = CreateStore();
        var lease = await abandonedStore.AcquireAsync("test", "transfers/abandoned", sensitivePlaintext: false);
        await File.WriteAllTextAsync(Path.Combine(lease.RootPath, "part.bin"), "partial");
        var sentinel = Path.Combine(_paths.Root, "external-sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "keep");

        var restartingStore = CreateStore();
        Assert.AreEqual(1, await restartingStore.RecoverAbandonedAsync());
        Assert.IsFalse(Directory.Exists(lease.RootPath));
        Assert.IsTrue(File.Exists(sentinel));
    }

    [TestMethod]
    public async Task MalformedLeaseIsQuarantinedWithoutGuessingItsRoot()
    {
        var unknownRoot = Path.Combine(_paths.Staging, "shares", "unknown");
        Directory.CreateDirectory(unknownRoot);
        await File.WriteAllTextAsync(Path.Combine(unknownRoot, "keep.txt"), "keep");
        await File.WriteAllTextAsync(Path.Combine(_paths.StagingLeases, "bad.json"), "{not-json");

        Assert.AreEqual(0, await CreateStore().RecoverAbandonedAsync());
        Assert.IsTrue(File.Exists(Path.Combine(unknownRoot, "keep.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(_paths.StagingLeases, "bad.json")));
        Assert.IsTrue(Directory.Exists(Path.Combine(_paths.Quarantine, "staging-leases")));
    }

    [TestMethod]
    [DataRow("leases")]
    [DataRow("leases/nested")]
    [DataRow("LEASES")]
    public async Task LeaseCannotOwnItsBookkeepingDirectory(string relativeRoot)
    {
        var store = CreateStore();
        var retained = await store.AcquireAsync("test", "shares/retained", sensitivePlaintext: true);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            store.AcquireAsync("test", relativeRoot, sensitivePlaintext: false));

        Assert.AreEqual(1, Directory.GetFiles(_paths.StagingLeases, "*.json").Length);
        Assert.IsTrue(Directory.Exists(retained.RootPath));
        Assert.IsTrue(await store.CompleteAsync(retained));
    }

    [TestMethod]
    public async Task RecoveryQuarantinesBookkeepingRootWithoutDeletingActiveLeases()
    {
        var store = CreateStore();
        var active = await store.AcquireAsync("test", "shares/active", sensitivePlaintext: true);
        var invalid = active with
        {
            LeaseId = Guid.NewGuid().ToString("N"),
            OwnerId = "abandoned-owner",
            RelativeRoot = "leases",
        };
        var invalidPath = Path.Combine(_paths.StagingLeases, invalid.LeaseId + ".json");
        await File.WriteAllTextAsync(invalidPath, JsonSerializer.Serialize(invalid,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

        Assert.AreEqual(0, await store.RecoverAbandonedAsync());

        Assert.IsFalse(File.Exists(invalidPath));
        Assert.IsTrue(File.Exists(Path.Combine(_paths.StagingLeases, active.LeaseId + ".json")));
        Assert.IsTrue(Directory.Exists(active.RootPath));
        Assert.IsTrue(await store.CompleteAsync(active));
    }

    private StagingLeaseStore CreateStore() =>
        new(_paths, NullLogger<StagingLeaseStore>.Instance);
}
