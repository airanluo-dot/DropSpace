using System.Reflection;
using DropSpace.App.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class PlacementObserverLifetimeTests
{
    [TestMethod]
    public async Task DelayedObserverStopCannotEraseNewPlacementSuppression()
    {
        await using var detector = new DragSessionDetector(null!, NullLogger<DragSessionDetector>.Instance);
        var processor = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        typeof(DragSessionDetector).GetField("_processor", flags)!.SetValue(detector, processor.Task);
        var stop = (Task)typeof(DragSessionDetector).GetMethod("StopCoreAsync", flags)!.Invoke(detector, null)!;
        try
        {
            Assert.IsFalse(stop.IsCompleted);
            detector.SetPlacementEditing(true);
        }
        finally { processor.TrySetResult(); }
        await stop;
        Assert.IsTrue(detector.CandidateCreationSuppressed);
        detector.SetPlacementEditing(false);
        Assert.IsFalse(detector.CandidateCreationSuppressed);
    }
}
