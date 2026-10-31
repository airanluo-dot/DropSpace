using System.Security.Cryptography;
using DropSpace.Core.Transfer;
using DropSpace.Infrastructure.Sharing;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class InternetShareByteBudgetTests
{
    [TestMethod]
    [DataRow(1L, false)]
    [DataRow(2L, true)]
    public async Task CallerByteBudgetIsCheckedBeforeSourceOrBackendAdmission(long maximumBytes, bool accepted)
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-share-byte-budget", Guid.NewGuid().ToString("N"));
        var paths = new AppStoragePaths(root);
        var backend = new CaptureBackend();
        var client = new InternetShareClient(new ShareCryptoService(), backend,
            new TransferLimits { MaxTotalBytes = maximumBytes }, paths);
        byte[] bytes = [1, 2];
        var opens = 0;
        var source = new ShareFileSource("fixture.bin", "application/octet-stream", bytes.Length,
            Convert.ToHexString(SHA256.HashData(bytes)), _ =>
            {
                opens++;
                return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
            });

        try
        {
            if (!accepted)
            {
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                    client.CreateWithSessionAsync([source], TimeSpan.FromHours(1)));
                Assert.AreEqual(0, opens, "An over-budget share must not open or stage the source.");
                Assert.AreEqual(0, backend.Creates);
                Assert.AreEqual(0, backend.Uploads);
                Assert.IsFalse(Directory.Exists(root));
                return;
            }

            var result = await client.CreateWithSessionAsync([source], TimeSpan.FromHours(1));
            Assert.AreEqual(2L, result.Descriptor.TotalBytes);
            Assert.AreEqual(1, opens, "Upload must keep using its one app-owned snapshot.");
            Assert.AreEqual(1, backend.Creates);
            Assert.AreEqual(2, backend.Uploads);
            Assert.IsFalse(Directory.EnumerateFiles(paths.Staging, "*", SearchOption.AllDirectories).Any());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class CaptureBackend : IShareBackendClient
    {
        public int Creates { get; private set; }
        public int Uploads { get; private set; }

        public Task<ShareBackendUploadSession> CreateAsync(Guid shareId, DateTimeOffset expiresAtUtc,
            int itemCount, long totalBytes, CancellationToken cancellationToken = default)
        {
            Creates++;
            return Task.FromResult(new ShareBackendUploadSession(
                new Uri("https://share.example.invalid/upload/"), new Uri("https://share.example.invalid/"),
                "Bearer fixture-token", new Uri("https://share.example.invalid/revoke/")));
        }

        public Task UploadAsync(ShareBackendUploadSession session, string objectName,
            ReadOnlyMemory<byte> ciphertext, string contentType, CancellationToken cancellationToken = default)
        {
            Uploads++;
            return Task.CompletedTask;
        }

        public Task RevokeAsync(ShareBackendUploadSession session, Guid shareId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
