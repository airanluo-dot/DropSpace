using DropSpace.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class StagingLeaseCleanupBoundaryTests
{
    private readonly AppStoragePaths _paths = new(Path.Combine(Path.GetTempPath(), "DropSpace-lease-cleanup-boundary", Guid.NewGuid().ToString("N")));

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true);
    }

    [TestMethod]
    public async Task LeaseRecordDeletionFailureRetainsRetryAfterPlaintextRemoval()
    {
        var store = new StagingLeaseStore(_paths, NullLogger<StagingLeaseStore>.Instance);
        var lease = await store.AcquireAsync("test", "shares/retry", sensitivePlaintext: true);
        var plaintext = Path.Combine(lease.RootPath, "plaintext.txt");
        await File.WriteAllTextAsync(plaintext, "sensitive");
        var record = Path.Combine(_paths.StagingLeases, lease.LeaseId + ".json");

        if (OperatingSystem.IsWindows())
        {
            var attributes = File.GetAttributes(record);
            try
            {
                File.SetAttributes(record, attributes | FileAttributes.ReadOnly);
                Assert.IsFalse(await store.CompleteAsync(lease));
                Assert.IsFalse(Directory.Exists(lease.RootPath), "Plaintext must be removed before record deletion is retried.");
                Assert.IsTrue(File.Exists(record));
            }
            finally { if (File.Exists(record)) File.SetAttributes(record, attributes); }
        }
        else if (OperatingSystem.IsLinux())
        {
            var mode = File.GetUnixFileMode(_paths.StagingLeases);
            try
            {
                File.SetUnixFileMode(_paths.StagingLeases, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                Assert.IsFalse(await store.CompleteAsync(lease));
                Assert.IsFalse(Directory.Exists(lease.RootPath), "Plaintext must be removed before record deletion is retried.");
                Assert.IsTrue(File.Exists(record));
            }
            finally { File.SetUnixFileMode(_paths.StagingLeases, mode); }
        }
        else
        {
            Assert.Inconclusive("This fixture requires Windows read-only deletion or Linux directory permissions.");
            return;
        }

        Assert.IsTrue(await store.CompleteAsync(lease));
        Assert.IsFalse(File.Exists(record));
    }
}
