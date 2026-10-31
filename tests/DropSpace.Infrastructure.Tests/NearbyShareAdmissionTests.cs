using DropSpace.Infrastructure.Sharing;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class NearbyShareAdmissionTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(11)]
    public async Task InvalidLifetimeDoesNotResolveNetworkOrAllocateListener(int minutes)
    {
        var resolutions = 0;
        await using var server = new NearbyShareServer(null, () =>
        {
            Interlocked.Increment(ref resolutions);
            throw new InvalidOperationException("An invalid request reached network startup.");
        });
        var sourceOpens = 0;
        var item = new NearbyShareItem(Guid.NewGuid(), "fixture.txt", "text/plain", 1, _ =>
        {
            Interlocked.Increment(ref sourceOpens);
            return Task.FromResult<Stream>(new MemoryStream([1], writable: false));
        });

        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() =>
            server.CreateShareAsync([item], TimeSpan.FromMinutes(minutes)));

        Assert.AreEqual(0, resolutions, "Rejected input must not start network ownership.");
        Assert.AreEqual(0, sourceOpens);
        Assert.IsNull(server.BaseUri);
        Assert.IsEmpty(server.GetSnapshot().Items);
    }
}
