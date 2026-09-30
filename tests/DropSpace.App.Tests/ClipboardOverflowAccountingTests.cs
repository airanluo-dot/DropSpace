using System.Reflection;
using DropSpace.App.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class ClipboardOverflowAccountingTests
{
    [TestMethod]
    public async Task ABlockedReaderCountsEvictedSignalsAndRetainsTheLatestSequence()
    {
        // No native listener/dispatcher is needed to exercise notification admission.
        await using var service = new ClipboardCaptureService(null!, null!, null!, null!, null!, null!, null!, null!,
            NullLogger<ClipboardCaptureService>.Instance);
        var callback = typeof(ClipboardCaptureService).GetMethod("OnClipboardChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        for (uint sequence = 1; sequence <= 200; sequence++)
            callback.Invoke(service, [null, new ClipboardNotification(sequence, DateTimeOffset.UtcNow)]);

        var dropped = (long)typeof(ClipboardCaptureService).GetField("_droppedEvents", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        Assert.AreEqual(72L, dropped, "DropOldest writes succeed but still evict an older notification.");
        var channel = typeof(ClipboardCaptureService).GetField("_signals", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        var reader = channel.GetType().GetProperty("Reader")!.GetValue(channel)!;
        var read = reader.GetType().GetMethod("TryRead")!;
        uint latest = 0;
        var retained = 0;
        object?[] args = [null];
        while ((bool)read.Invoke(reader, args)!)
        {
            retained++;
            latest = (uint)args[0]!.GetType().GetProperty("ClipboardSequenceNumber")!.GetValue(args[0])!;
        }
        Assert.AreEqual(128, retained);
        Assert.AreEqual(200u, latest);
    }
}
