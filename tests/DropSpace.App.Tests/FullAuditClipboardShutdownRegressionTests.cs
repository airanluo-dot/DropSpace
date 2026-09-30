using System.Reflection;
using DropSpace.App.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class FullAuditClipboardShutdownRegressionTests
{
    [TestMethod]
    public async Task QueuedCopyObservesShutdownWithoutStrandingItsGate()
    {
        // No platform operation is reached: hold the clipboard-write gate until
        // Dispose has completed, then let the queued operation observe shutdown.
        var capture = new ClipboardCaptureService(null!, null!, null!, null!, null!, null!, null!, null!,
            NullLogger<ClipboardCaptureService>.Instance);
        var gate = (SemaphoreSlim)typeof(ClipboardCaptureService)
            .GetField("_clipboardWriteGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(capture)!;
        await gate.WaitAsync();
        var copy = capture.CopyTextAsync("queued shutdown copy");
        await capture.DisposeAsync();
        gate.Release();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => copy.WaitAsync(TimeSpan.FromSeconds(2)));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => capture.PauseAsync());
    }
}
