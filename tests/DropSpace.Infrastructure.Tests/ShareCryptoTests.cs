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
        var rejected = false;
        try
        {
            crypto.DecryptChunk(master, shareId, fileId, 0, plaintext.Length, tampered, chunk.Tag, prefix);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            rejected = true;
        }

        Assert.IsTrue(rejected, "Tampered ciphertext must be rejected as a cryptographic failure.");
    }

    [TestMethod]
    public void WirePackingUsesNonceCiphertextTagOrder()
    {
        var nonce = Enumerable.Range(1, ShareCryptoService.ManifestNonceBytes).Select(value => (byte)value).ToArray();
        var ciphertext = new byte[] { 21, 22, 23, 24 };
        var tag = Enumerable.Range(101, ShareCryptoService.AuthenticationTagBytes).Select(value => (byte)value).ToArray();

        var manifestWire = ShareCryptoService.PackManifestWire(nonce, ciphertext, tag);
        CollectionAssert.AreEqual(nonce, manifestWire[..nonce.Length]);
        CollectionAssert.AreEqual(ciphertext, manifestWire[nonce.Length..^tag.Length]);
        CollectionAssert.AreEqual(tag, manifestWire[^tag.Length..]);
        var unpacked = ShareCryptoService.UnpackManifestWire(manifestWire);
        CollectionAssert.AreEqual(nonce, unpacked.Nonce);
        CollectionAssert.AreEqual(ciphertext, unpacked.Ciphertext);
        CollectionAssert.AreEqual(tag, unpacked.Tag);

        var chunkWire = ShareCryptoService.PackChunkWire(ciphertext, tag);
        CollectionAssert.AreEqual(ciphertext.Concat(tag).ToArray(), chunkWire);
    }

    [TestMethod]
    public void SharedVectorLocksGuidOrderHkdfAadAndAesGcm()
    {
        var crypto = new ShareCryptoService();
        var master = Convert.FromHexString("0102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f20");
        var shareId = Guid.Parse("10203040-5060-7080-90a0-b0c0d0e0f000");
        var fileId = Guid.Parse("11223344-5566-7788-99aa-bbccddeeff00");
        var plaintext = System.Text.Encoding.UTF8.GetBytes("hello world");
        var prefix = Convert.FromHexString("0102030405060708");
        var item = new EncryptedShareManifestItem(
            fileId,
            "vector.txt",
            "text/plain",
            plaintext.Length,
            1,
            "b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9",
            prefix);

        CollectionAssert.AreEqual(Convert.FromHexString("403020106050807090a0b0c0d0e0f000"), ShareCryptoService.GuidWireBytes(shareId));
        var manifest = crypto.EncryptManifest(master, shareId, [item], Convert.FromHexString("0c0d0e0f1011121314151617"));
        CollectionAssert.AreEqual(Convert.FromHexString("b9cd59c2da1f819a418395017449eea2f0e76302f7d148c42731d4b25369fcbb992310ff4855b54e9d888c2c7bad10768b31f5d2dcd8a48bbcfa15e022b70d6a760f52304e0fb3bf0c48dcb658f904266f55a8fc909c1930ef47d73d26d7f1cbb3ad06fd0803cd5828a6b21f2092c39caca0b72006778f4c26aa21150e363c0ba8308555a3ca62914d22309bfc53ffdce83e12b8f50277387313fb0f0fad7fac847cfc4dcdea3cb398a6ecb30298d3d88df71de7d65c6b7eee8651c5e982ae96202b829e8f5d378e958d4a5b0a95feded8c07e91f32cb7c4e0c68f6bf4496bea796bf5cacdb0938029982fefa0cf34c08e5608afc3691ecb3a8e82119382a224171a2f85fa99151ff1e9045f3afa70324251d0ea84187e99cfb4c9ad7a37a17581f2a3948c01cd06d87a7f4d7e836ce5b311754e83e29e33edf8"), manifest.Ciphertext.Concat(manifest.Tag).ToArray());

        var chunk = crypto.EncryptChunk(master, shareId, fileId, 0, plaintext, prefix);
        CollectionAssert.AreEqual(Convert.FromHexString("19da70f0d8652efeb3ffa6"), chunk.Ciphertext);
        CollectionAssert.AreEqual(Convert.FromHexString("1b425aa01055361c8deca3bcfb08e0a1"), chunk.Tag);
        CollectionAssert.AreEqual(master, ShareCryptoService.FromUrlFragment("AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA"));
    }
}
