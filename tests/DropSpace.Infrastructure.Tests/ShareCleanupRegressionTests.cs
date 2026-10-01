using DropSpace.Infrastructure.Sharing;
using DropSpace.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class ShareCleanupRegressionTests
{
    private AppStoragePaths _paths = null!;

    [TestInitialize]
    public void Initialize()
    {
        _paths = new AppStoragePaths(Path.Combine(Path.GetTempPath(), "DropSpace-share-cleanup-tests", Guid.NewGuid().ToString("N")));
        _paths.EnsureCreated();
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, recursive: true);
    }

    [TestMethod]
    public async Task CompletedShareStillReturnsItsSessionWhenLeaseRecordCleanupNeedsRetry()
    {
        var leases = new StagingLeaseStore(_paths, NullLogger<StagingLeaseStore>.Instance);
        var backend = new CleanupFailureBackend(_paths);
        var client = new InternetShareClient(new ShareCryptoService(), backend, storagePaths: _paths, stagingLeases: leases);

        var result = await client.CreateWithSessionAsync([CreateSource()], TimeSpan.FromHours(1));

        Assert.AreNotEqual(Guid.Empty, result.Descriptor.ShareId);
        Assert.AreEqual(backend.Session, result.Session);
        Assert.AreEqual(2, backend.Uploads);
        Assert.AreEqual(0, backend.Revocations);
        await AssertRetainedLeaseCanBeCompletedAsync(leases);
    }

    [TestMethod]
    public async Task FailedUploadPreservesItsOriginalErrorWhenLeaseRecordCleanupNeedsRetry()
    {
        var leases = new StagingLeaseStore(_paths, NullLogger<StagingLeaseStore>.Instance);
        var backend = new CleanupFailureBackend(_paths) { UploadFailure = new InvalidOperationException("upload failed") };
        var client = new InternetShareClient(new ShareCryptoService(), backend, storagePaths: _paths, stagingLeases: leases);

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => client.CreateWithSessionAsync([CreateSource()], TimeSpan.FromHours(1)));

        Assert.AreSame(backend.UploadFailure, error);
        Assert.AreEqual(1, backend.Revocations, "The failed upload must still revoke its remote share.");
        await AssertRetainedLeaseCanBeCompletedAsync(leases);
    }

    [TestMethod]
    public async Task RecoveryHandlePrecedesUploadsAndSurvivesFailedRevocation()
    {
        var events = new List<string>();
        var backend = new JournalFailureBackend(events);
        var retained = false;
        var client = new InternetShareClient(new ShareCryptoService(), backend, storagePaths: _paths,
            sessionCreated: (_, _, _) => { events.Add("persist"); retained = true; return Task.CompletedTask; },
            sessionRevoked: _ => { retained = false; return Task.CompletedTask; });
        await Assert.ThrowsExactlyAsync<IOException>(() => client.CreateWithSessionAsync([CreateSource()], TimeSpan.FromHours(1)));
        CollectionAssert.AreEqual(new[] { "create", "persist", "upload", "revoke" }, events);
        Assert.IsTrue(retained, "An unsuccessful remote revoke must retain its recovery capability.");
    }

    private sealed class JournalFailureBackend(List<string> events) : IShareBackendClient
    {
        public Task<ShareBackendUploadSession> CreateAsync(Guid id, DateTimeOffset expires, int count, long bytes, CancellationToken token = default)
        {
            events.Add("create");
            return Task.FromResult(new ShareBackendUploadSession(new Uri("https://share.example.invalid/upload/"),
                new Uri("https://share.example.invalid/"), "Bearer fixture-token", new Uri("https://share.example.invalid/revoke/")));
        }
        public Task UploadAsync(ShareBackendUploadSession session, string name, ReadOnlyMemory<byte> bytes, string contentType, CancellationToken token = default)
        { events.Add("upload"); throw new IOException("fixture upload failure"); }
        public Task RevokeAsync(ShareBackendUploadSession session, Guid id, CancellationToken token = default)
        { events.Add("revoke"); throw new IOException("fixture revoke failure"); }
    }

    private async Task AssertRetainedLeaseCanBeCompletedAsync(StagingLeaseStore leases)
    {
        var leasePath = Directory.EnumerateFiles(_paths.StagingLeases, "*.json").Single();
        var lease = JsonSerializer.Deserialize<StagingLease>(await File.ReadAllTextAsync(leasePath), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.IsNotNull(lease);
        Assert.IsTrue(lease.SensitivePlaintext);
        Assert.AreEqual("internet-share", lease.Kind);
        Assert.IsFalse(Directory.Exists(Path.Combine(_paths.Staging, lease.RelativeRoot)), "Uploaded plaintext should already be removed.");
        File.Delete(_paths.Updates);
        Directory.CreateDirectory(_paths.Updates);
        Assert.IsTrue(await leases.CompleteAsync(lease), "A retained lease should remain available for a cleanup retry.");
        Assert.IsFalse(File.Exists(leasePath));
    }

    private static ShareFileSource CreateSource()
    {
        var bytes = Encoding.UTF8.GetBytes("uploaded snapshot");
        return new ShareFileSource("snapshot.txt", "text/plain", bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)),
            _ => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false)));
    }

    private sealed class CleanupFailureBackend(AppStoragePaths paths) : IShareBackendClient
    {
        public ShareBackendUploadSession Session { get; } = new(
            new Uri("https://share.example.invalid/upload/"),
            new Uri("https://share.example.invalid"),
            "Bearer test-token",
            new Uri("https://share.example.invalid/revoke/"));
        public InvalidOperationException? UploadFailure { get; init; }
        public int Uploads { get; private set; }
        public int Revocations { get; private set; }

        public Task<ShareBackendUploadSession> CreateAsync(Guid shareId, DateTimeOffset expiresAtUtc, int itemCount, long totalBytes, CancellationToken cancellationToken = default) =>
            Task.FromResult(Session);

        public Task UploadAsync(ShareBackendUploadSession session, string objectName, ReadOnlyMemory<byte> ciphertext, string contentType, CancellationToken cancellationToken = default)
        {
            Uploads++;
            if (objectName != "manifest.bin")
            {
                Directory.Delete(paths.Updates);
                File.WriteAllText(paths.Updates, "temporary storage path collision");
                if (UploadFailure is not null) throw UploadFailure;
            }
            return Task.CompletedTask;
        }

        public Task RevokeAsync(ShareBackendUploadSession session, Guid shareId, CancellationToken cancellationToken = default)
        {
            Revocations++;
            return Task.CompletedTask;
        }
    }
}
