using DropSpace.Infrastructure.Sharing;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class ShareCryptoTests
{
    [TestMethod]
    public void ChunkAndManifestEncryptionRoundTripWithDeterministicProtocolInputs()
    {
        var crypto = new ShareCryptoService();
        var master = crypto.CreateMasterKey();
        var shareId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var prefix = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var plaintext = System.Text.Encoding.UTF8.GetBytes("DropSpace encrypted share test");
        var chunk = crypto.EncryptChunk(master, shareId, fileId, 0, plaintext, prefix);
        var restored = crypto.DecryptChunk(master, shareId, fileId, 0, plaintext.Length, chunk.Ciphertext, chunk.Tag, prefix);
        CollectionAssert.AreEqual(plaintext, restored);

        var manifest = crypto.EncryptManifest(master, shareId, [new EncryptedShareManifestItem(fileId, "test.txt", "text/plain", plaintext.Length, 1, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(plaintext)).ToLowerInvariant(), prefix)]);
        var items = crypto.DecryptManifest(master, shareId, manifest.Nonce, manifest.Ciphertext, manifest.Tag);
        Assert.AreEqual(1, items.Count);
        Assert.AreEqual(fileId, items[0].FileId);

        var tampered = chunk.Ciphertext.ToArray();
        tampered[0] ^= 0x01;
        Assert.ThrowsExactly<System.Security.Cryptography.CryptographicException>(() =>
            crypto.DecryptChunk(master, shareId, fileId, 0, plaintext.Length, tampered, chunk.Tag, prefix));
    }
}
