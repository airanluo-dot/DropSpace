using DropSpace.Core.Models;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class RemoteMetadataCacheTests
{
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
