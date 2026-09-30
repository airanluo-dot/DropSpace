using DropSpace.App.Services.NeteaseEnhancement;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class FullAuditNeteaseProbeRegressionTests
{
    [TestMethod]
    public void MalformedExecutableDoesNotHideLaterValidCandidate()
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-tests", Guid.NewGuid().ToString("N"));
        var badDirectory = Path.Combine(root, "bad");
        var goodDirectory = Path.Combine(root, "good");
        Directory.CreateDirectory(badDirectory);
        Directory.CreateDirectory(goodDirectory);
        try
        {
            var bad = Path.Combine(badDirectory, "cloudmusic.exe");
            var good = Path.Combine(goodDirectory, "cloudmusic.exe");
            File.WriteAllText(bad, "not a PE image");
            // The versioned MSTest managed PE is a self-contained fixture for the probe's
            // existing filename, architecture, and version contract, without installing a player.
            File.Copy(typeof(Assert).Assembly.Location, good);

            var result = NeteaseInstallationProbe.FindCandidates([bad, good]);

            Assert.IsNotNull(result);
            Assert.AreEqual(good, result.ExecutablePath);
            Assert.IsTrue(result.Version.Major >= 2);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    public void CandidateScanPreservesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsExactly<OperationCanceledException>(() =>
            NeteaseInstallationProbe.FindCandidates(["cloudmusic.exe"], cancellation.Token));
    }
}
