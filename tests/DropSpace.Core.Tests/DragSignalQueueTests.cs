using DropSpace.Core.DragDrop;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class DragSignalQueueTests
{
    [TestMethod]
    public void ReliableLaneRetainsEveryLifecycleSignalDuringABurst()
    {
        var queue = new DragSignalQueue<int>(reliable: true);
        for (var value = 0; value < 2_048; value++)
        {
            Assert.IsTrue(queue.TryWrite(value));
        }

        for (var expected = 0; expected < 2_048; expected++)
        {
            Assert.IsTrue(queue.TryRead(out var actual));
            Assert.AreEqual(expected, actual);
        }

        Assert.AreEqual(0, queue.WriteFailureCount);
    }

    [TestMethod]
    public void LossyLaneKeepsTheNewestPointerPosition()
    {
        var queue = new DragSignalQueue<int>(reliable: false);
        Assert.IsTrue(queue.TryWrite(10));
        Assert.IsTrue(queue.TryWrite(20));

        Assert.IsTrue(queue.TryRead(out var actual));
        Assert.AreEqual(20, actual);
        Assert.AreEqual(1, queue.ReplacedWriteCount);
    }

    [TestMethod]
    public void CompletedReliableLaneReportsWriteFailureInsteadOfBlocking()
    {
        var queue = new DragSignalQueue<int>(reliable: true);
        queue.Complete();

        Assert.IsFalse(queue.TryWrite(1));
        Assert.AreEqual(1, queue.WriteFailureCount);
    }
}
