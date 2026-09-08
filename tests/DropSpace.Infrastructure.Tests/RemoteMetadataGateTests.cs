using DropSpace.Core.Models;
using DropSpace.Infrastructure.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class RemoteMetadataGateTests
{
    [TestMethod]
    public async Task RemoteGateTimesOutWithoutStartingAThirdSynchronousOperation()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("UNC path classification is a Windows-only integration boundary.");
        }

        using var release = new ManualResetEventSlim(false);
        using var started = new CountdownEvent(2);
        var calls = 0;
        var service = new LocalFileReferenceService(
            inspectOverride: path =>
            {
                Interlocked.Increment(ref calls);
                started.Signal();
                release.Wait();
                return new FileCandidate(
                    path,
                    path,
                    FileEntryKind.File,
                    Path.GetFileName(path),
                    ".txt",
                    1,
                    DateTimeOffset.UtcNow,
                    ItemStatus.Available,
                    null);
            });

        var first = service.InspectAsync(@"\\server\share\one.txt");
        var second = service.InspectAsync(@"\\server\share\two.txt");
        Assert.IsTrue(started.Wait(TimeSpan.FromSeconds(5)), "The first two remote operations did not enter the synchronous boundary.");

        await Assert.ThrowsExactlyAsync<IOException>(() => service.InspectAsync(@"\\server\share\three.txt"));
        Assert.AreEqual(2, calls, "A timed-out gate acquisition must not start a third worker.");

        release.Set();
        await Task.WhenAll(first, second);
    }
}
