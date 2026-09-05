using System.Runtime.Versioning;
using DropSpace.Infrastructure.Sharing;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class InternetShareRevokeStoreTests
{
    [TestMethod]
    public async Task EncryptedRevokeHandleSurvivesRestartRoundTrip()
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-share-revoke", Guid.NewGuid().ToString("N"));
        var paths = new AppStoragePaths(root);
        var shareId = Guid.NewGuid();
        var session = new ShareBackendUploadSession(
            new Uri("https://share.example.invalid/v1/shares/objects/"),
            new Uri("https://share.example.invalid/"),
            "Bearer test-secret-that-must-not-be-stored-in-plaintext",
            new Uri("https://share.example.invalid/v1/shares/" + shareId.ToString("N")));

        try
        {
            var store = new InternetShareRevokeStore(paths);
            await store.SaveAsync(shareId, session, DateTimeOffset.UtcNow.AddHours(1));

            var persistedPath = Path.Combine(paths.Data, "share-revokes", shareId.ToString("N") + ".bin");
            Assert.IsTrue(File.Exists(persistedPath));
            Assert.IsFalse(
                Convert.ToBase64String(await File.ReadAllBytesAsync(persistedPath))
                    .Contains("Bearer test-secret-that-must-not-be-stored-in-plaintext", StringComparison.Ordinal));

            var restored = await new InternetShareRevokeStore(paths).LoadAllAsync();
            Assert.AreEqual(1, restored.Count);
            Assert.AreEqual(shareId, restored[0].ShareId);
            Assert.AreEqual(session.UploadAuthorization, restored[0].Session.UploadAuthorization);
            Assert.AreEqual(session.RevokeUrl, restored[0].Session.RevokeUrl);

            await store.DeleteAsync(shareId);
            Assert.IsFalse(File.Exists(persistedPath));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task SavePrunesToBoundedCapacityAndKeepsNewestHandle()
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-share-revoke", Guid.NewGuid().ToString("N"));
        var paths = new AppStoragePaths(root);
        var ids = Enumerable.Range(0, 130).Select(_ => Guid.NewGuid()).ToArray();
        try
        {
            var store = new InternetShareRevokeStore(paths);
            foreach (var id in ids)
            {
                await store.SaveAsync(
                    id,
                    new ShareBackendUploadSession(
                        new Uri("https://share.example.invalid/v1/shares/objects/"),
                        new Uri("https://share.example.invalid/"),
                        "Bearer bounded-retention-test-token",
                        new Uri("https://share.example.invalid/v1/shares/" + id.ToString("N"))),
                    DateTimeOffset.UtcNow.AddHours(1));
            }

            var restored = await store.LoadAllAsync();
            Assert.AreEqual(128, restored.Count);
            Assert.IsTrue(restored.Any(item => item.ShareId == ids[^1]));
            Assert.IsTrue(File.Exists(Path.Combine(paths.Data, "share-revokes", ids[^1].ToString("N") + ".bin")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
