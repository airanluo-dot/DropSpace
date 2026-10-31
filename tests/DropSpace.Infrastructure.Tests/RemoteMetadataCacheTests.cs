using DropSpace.Core.Models;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class RemoteMetadataCacheTests
{
    [TestMethod]
    public async Task RemoteAvailabilityCacheDoesNotReuseDifferentExpectedEntryKinds()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("UNC path classification is a Windows-only integration boundary.");
        }

        var calls = 0;
        var service = new LocalFileReferenceService(
            availabilityOverride: reference =>
            {
                Interlocked.Increment(ref calls);
                return reference.EntryKind == FileEntryKind.Folder
                    ? new FileAvailabilityCheck(ItemStatus.Unavailable, "Entry type changed")
                    : new FileAvailabilityCheck(ItemStatus.Available, null);
            });
        const string path = @"\\server\share\entry";
        var file = new FileReference(path, path, FileEntryKind.File, null, null, null, DateTimeOffset.UtcNow, null);
        Assert.AreEqual(ItemStatus.Available, (await service.CheckAvailabilityAsync(file)).Status);
        Assert.AreEqual(ItemStatus.Available, (await service.CheckAvailabilityAsync(file)).Status);
        Assert.AreEqual(1, calls, "The same expected kind should reuse the bounded cache.");

        var folder = file with { EntryKind = FileEntryKind.Folder };
        Assert.AreEqual(ItemStatus.Unavailable, (await service.CheckAvailabilityAsync(folder)).Status);
        Assert.AreEqual(ItemStatus.Unavailable, (await service.CheckAvailabilityAsync(folder)).Status);
        Assert.AreEqual(2, calls, "A file result cannot authorize a folder reference at the same path.");
        Assert.AreEqual(ItemStatus.Available, (await service.CheckAvailabilityAsync(file)).Status);
        Assert.AreEqual(3, calls);
        Assert.AreEqual(1, service.AvailabilityCacheCount);
    }

    [TestMethod]
    public async Task RemoteAvailabilityCacheEvictsOldEntriesAtItsHardCap()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("UNC path classification is a Windows-only integration boundary.");
        }

        var calls = 0;
        var service = new LocalFileReferenceService(
            availabilityOverride: reference =>
            {
                Interlocked.Increment(ref calls);
                return new FileAvailabilityCheck(ItemStatus.Available, null);
            });

        for (var index = 0; index < 1_025; index++)
        {
            var path = $"\\\\server\\share\\cache-{index:D4}.txt";
            var reference = new FileReference(
                path,
                path,
                FileEntryKind.File,
                ".txt",
                1,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                null);
            await service.CheckAvailabilityAsync(reference);
        }

        Assert.AreEqual(1_025, calls);
        Assert.AreEqual(1_024, service.AvailabilityCacheCount);
    }
}
