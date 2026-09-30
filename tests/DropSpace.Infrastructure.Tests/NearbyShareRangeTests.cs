using DropSpace.Infrastructure.Sharing;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class NearbyShareRangeTests
{
    [TestMethod]
    [DataRow("-3", 10L, 7L, 9L)]
    [DataRow("-20", 10L, 0L, 9L)]
    [DataRow("3-", 10L, 3L, 9L)]
    [DataRow("3-5", 10L, 3L, 5L)]
    [DataRow("3-20", 10L, 3L, 9L)]
    [DataRow("0-0", 1L, 0L, 0L)]
    public void ValidRangesSelectTheExpectedBytes(string range, long total, long expectedStart, long expectedEnd)
    {
        Assert.IsTrue(NearbyShareServer.ParseRange(range, total, out var start, out var end));
        Assert.AreEqual(expectedStart, start);
        Assert.AreEqual(expectedEnd, end);
    }

    [TestMethod]
    [DataRow("abc-3", 10L)]
    [DataRow("3-abc", 10L)]
    [DataRow("3- ", 10L)]
    [DataRow("-0", 10L)]
    [DataRow("-", 10L)]
    [DataRow("10-", 10L)]
    [DataRow("5-3", 10L)]
    [DataRow("0-1,3-4", 10L)]
    [DataRow("0-", 0L)]
    [DataRow("9223372036854775808-", 10L)]
    public void MalformedOrUnsatisfiableRangesAreRejected(string range, long total)
    {
        Assert.IsFalse(NearbyShareServer.ParseRange(range, total, out _, out _));
    }
}
