using DropSpace.Infrastructure.Sharing;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class ShareCapacityRegressionTests
{
    // Capacity reservations on an empty store use only local bookkeeping, with no DPAPI calls.
#pragma warning disable CA1416
    [TestMethod]
    public async Task ConcurrentReservationDisposalRestoresEveryAvailableSlot()
    {
        var paths = new AppStoragePaths(Path.Combine(Path.GetTempPath(), "DropSpace-share-capacity-tests", Guid.NewGuid().ToString("N")));
        var store = new InternetShareRevokeStore(paths);
        var failures = 0;
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
        {
            for (var index = 0; index < 2_000; index++)
            {
                ShareCapacityReservation reservation;
                try
                {
                    reservation = await store.ReserveCapacityAsync();
                }
                catch (InvalidOperationException)
                {
                    Interlocked.Increment(ref failures);
                    break;
                }
                using (reservation)
                {
                    await Task.Yield();
                }
            }
        })));

        var available = new List<ShareCapacityReservation>();
        try
        {
            for (var index = 0; index < 128; index++) available.Add(await store.ReserveCapacityAsync());
            Assert.AreEqual(0, failures, "At most 32 reservations were active, so the 128-slot capacity must remain available throughout.");
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => store.ReserveCapacityAsync());
        }
        finally
        {
            foreach (var reservation in available) reservation.Dispose();
            if (Directory.Exists(paths.Root)) Directory.Delete(paths.Root, recursive: true);
        }
    }
#pragma warning restore CA1416
}
