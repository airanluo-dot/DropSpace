using DropSpace.Infrastructure.Logging;
using DropSpace.Infrastructure.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class LoggingDiagnosticsTests
{
    private string _root = null!;

    [TestInitialize]
    public void Initialize() => _root = Path.Combine(
        Path.GetTempPath(),
        "DropSpace-tests",
        Guid.NewGuid().ToString("N"));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public async Task SaturatedQueueLeavesQueryableEmergencyDiagnostic()
    {
        var paths = new AppStoragePaths(_root);
        await using var provider = new RedactingFileLoggerProvider(paths, queueCapacity: 1, startWriter: false);

        Assert.IsTrue(provider.TryEnqueueForTests("first"));
        Assert.IsFalse(provider.TryEnqueueForTests("second"));
        Assert.AreEqual(1, provider.DroppedMessageCount);

        await provider.DisposeAsync();

        var diagnosticPath = Path.Combine(paths.Logs, "dropspace-log-diagnostics.txt");
        Assert.IsTrue(File.Exists(diagnosticPath));
        var diagnostic = await File.ReadAllTextAsync(diagnosticPath);
        StringAssert.Contains(diagnostic, "droppedMessages=1");
    }

    [TestMethod]
    public async Task EmergencyDiagnosticSinkFailureDoesNotEscapeShutdown()
    {
        var paths = new AppStoragePaths(_root);
        paths.EnsureCreated();
        Directory.CreateDirectory(Path.Combine(paths.Logs, "dropspace-log-diagnostics.txt"));

        await using var provider = new RedactingFileLoggerProvider(paths, queueCapacity: 1, startWriter: false);
        Assert.IsTrue(provider.TryEnqueueForTests("first"));
        Assert.IsFalse(provider.TryEnqueueForTests("second"));

        await provider.DisposeAsync();
    }
}
