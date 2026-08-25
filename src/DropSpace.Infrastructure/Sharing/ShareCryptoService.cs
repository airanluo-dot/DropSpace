using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DropSpace.Core.Transfer;

namespace DropSpace.Infrastructure.Sharing;

public sealed record EncryptedShareChunk(int Index, int PlainLength, byte[] Ciphertext, byte[] Tag);

public sealed record EncryptedShareManifest(
    Guid ShareId,
    IReadOnlyList<EncryptedShareManifestItem> Items,
    byte[] Nonce,
    byte[] Ciphertext,
    byte[] Tag);

public sealed record EncryptedShareManifestItem(
    Guid FileId,
    string DisplayName,
    string MimeType,
    long PlainLength,
    int ChunkCount,
    string Sha256,
    byte[] NoncePrefix);

public sealed class ShareCryptoService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public byte[] CreateMasterKey() => RandomNumberGenerator.GetBytes(32);

    public EncryptedShareChunk EncryptChunk(
        ReadOnlySpan<byte> masterKey,
        Guid shareId,
        Guid fileId,
        int index,
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> noncePrefix)
    {
        if (masterKey.Length != 32 || noncePrefix.Length != 8 || index < 0) throw new ArgumentOutOfRangeException(nameof(index));
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(DeriveFileKey(masterKey, shareId, fileId), tagSizeInBytes: 16);
        var nonce = CreateNonce(noncePrefix, index);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, CreateAad(shareId, fileId, index, plaintext.Length));
        return new EncryptedShareChunk(index, plaintext.Length, ciphertext, tag);
    }

    public byte[] DecryptChunk(
        ReadOnlySpan<byte> masterKey,
        Guid shareId,
        Guid fileId,
        int index,
        int plainLength,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag,
        ReadOnlySpan<byte> noncePrefix)
    {
        if (masterKey.Length != 32 || noncePrefix.Length != 8 || index < 0 || plainLength < 0 ||
            ciphertext.Length != plainLength || tag.Length != 16)
        {
            throw new InvalidDataException("The encrypted share chunk metadata is invalid.");
        }
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(DeriveFileKey(masterKey, shareId, fileId), tagSizeInBytes: 16);
        aes.Decrypt(CreateNonce(noncePrefix, index), ciphertext, tag, plaintext, CreateAad(shareId, fileId, index, plainLength));
        return plaintext;
    }

    public (byte[] Nonce, byte[] Ciphertext, byte[] Tag) EncryptManifest(
        ReadOnlySpan<byte> masterKey,
        Guid shareId,
        IReadOnlyList<EncryptedShareManifestItem> items)
    {
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new ManifestPlaintext(shareId, items), JsonOptions);
        var key = DeriveManifestKey(masterKey, shareId);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[manifest.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, tagSizeInBytes: 16);
        aes.Encrypt(nonce, manifest, ciphertext, tag, Encoding.UTF8.GetBytes(string.Concat("DropSpaceShare:v1\n", shareId.ToString("N"))));
        return (nonce, ciphertext, tag);
    }

    public IReadOnlyList<EncryptedShareManifestItem> DecryptManifest(
        ReadOnlySpan<byte> masterKey,
        Guid shareId,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag)
    {
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(DeriveManifestKey(masterKey, shareId), tagSizeInBytes: 16);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes(string.Concat("DropSpaceShare:v1\n", shareId.ToString("N"))));
        var manifest = JsonSerializer.Deserialize<ManifestPlaintext>(plaintext, JsonOptions)
            ?? throw new InvalidDataException("The encrypted share manifest is empty.");
        if (manifest.ShareId != shareId) throw new InvalidDataException("The encrypted share manifest belongs to another share.");
        return manifest.Items;
    }

    public static string ToUrlFragment(ReadOnlySpan<byte> masterKey) => Base64Url(masterKey);

    public static byte[] FromUrlFragment(string fragment)
    {
        var normalized = fragment.StartsWith("#k=", StringComparison.Ordinal) ? fragment[3..] : fragment;
        return FromBase64Url(normalized);
    }

    private static byte[] DeriveFileKey(ReadOnlySpan<byte> masterKey, Guid shareId, Guid fileId) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, shareId.ToByteArray(), Encoding.UTF8.GetBytes(string.Concat("file:", fileId.ToString("N"))));

    private static byte[] DeriveManifestKey(ReadOnlySpan<byte> masterKey, Guid shareId) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, shareId.ToByteArray(), Encoding.UTF8.GetBytes("manifest"));

    private static byte[] CreateNonce(ReadOnlySpan<byte> noncePrefix, int index)
    {
        var nonce = new byte[12];
        noncePrefix.CopyTo(nonce);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8), checked((uint)index));
        return nonce;
    }

    private static byte[] CreateAad(Guid shareId, Guid fileId, int index, int plainLength) =>
        Encoding.UTF8.GetBytes(string.Concat("DropSpaceShare:v1\n", shareId.ToString("N"), "\n", fileId.ToString("N"), "\n", index, "\n", plainLength));

    private static string Base64Url(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }

    private sealed record ManifestPlaintext(Guid ShareId, IReadOnlyList<EncryptedShareManifestItem> Items);
}
