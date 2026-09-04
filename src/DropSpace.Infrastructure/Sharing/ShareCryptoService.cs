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
    public const int ManifestNonceBytes = 12;
    public const int AuthenticationTagBytes = 16;

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
        ValidateMasterKey(masterKey);
        if (shareId == Guid.Empty || fileId == Guid.Empty || noncePrefix.Length != 8 || index < 0) throw new ArgumentOutOfRangeException(nameof(index));
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[AuthenticationTagBytes];
        var fileKey = DeriveFileKey(masterKey, shareId, fileId);
        try
        {
            using var aes = new AesGcm(fileKey, tagSizeInBytes: AuthenticationTagBytes);
            var nonce = CreateNonce(noncePrefix, index);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, CreateAad(shareId, fileId, index, plaintext.Length));
            return new EncryptedShareChunk(index, plaintext.Length, ciphertext, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fileKey);
        }
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
        ValidateMasterKey(masterKey);
        if (shareId == Guid.Empty || fileId == Guid.Empty || noncePrefix.Length != 8 || index < 0 || plainLength < 0 ||
            ciphertext.Length != plainLength || tag.Length != AuthenticationTagBytes)
        {
            throw new InvalidDataException("The encrypted share chunk metadata is invalid.");
        }
        var plaintext = new byte[ciphertext.Length];
        var fileKey = DeriveFileKey(masterKey, shareId, fileId);
        try
        {
            using var aes = new AesGcm(fileKey, tagSizeInBytes: AuthenticationTagBytes);
            aes.Decrypt(CreateNonce(noncePrefix, index), ciphertext, tag, plaintext, CreateAad(shareId, fileId, index, plainLength));
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fileKey);
        }
    }

    public (byte[] Nonce, byte[] Ciphertext, byte[] Tag) EncryptManifest(
        ReadOnlySpan<byte> masterKey,
        Guid shareId,
        IReadOnlyList<EncryptedShareManifestItem> items)
        => EncryptManifest(masterKey, shareId, items, RandomNumberGenerator.GetBytes(ManifestNonceBytes));

    public (byte[] Nonce, byte[] Ciphertext, byte[] Tag) EncryptManifest(
        ReadOnlySpan<byte> masterKey,
        Guid shareId,
        IReadOnlyList<EncryptedShareManifestItem> items,
        ReadOnlySpan<byte> nonce)
    {
        ValidateMasterKey(masterKey);
        if (shareId == Guid.Empty) throw new ArgumentOutOfRangeException(nameof(shareId));
        if (nonce.Length != ManifestNonceBytes) throw new ArgumentOutOfRangeException(nameof(nonce));
        ArgumentNullException.ThrowIfNull(items);
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new ManifestPlaintext(shareId, items), JsonOptions);
        var key = DeriveManifestKey(masterKey, shareId);
        var ciphertext = new byte[manifest.Length];
        var tag = new byte[AuthenticationTagBytes];
        try
        {
            using var aes = new AesGcm(key, tagSizeInBytes: AuthenticationTagBytes);
            aes.Encrypt(nonce, manifest, ciphertext, tag, Encoding.UTF8.GetBytes(string.Concat("DropSpaceShare:v1\n", shareId.ToString("N"))));
            return (nonce.ToArray(), ciphertext, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(manifest);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public IReadOnlyList<EncryptedShareManifestItem> DecryptManifest(
        ReadOnlySpan<byte> masterKey,
        Guid shareId,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag)
    {
        ValidateMasterKey(masterKey);
        if (shareId == Guid.Empty || nonce.Length != ManifestNonceBytes || tag.Length != AuthenticationTagBytes)
        {
            throw new InvalidDataException("The encrypted share manifest metadata is invalid.");
        }
        var plaintext = new byte[ciphertext.Length];
        var key = DeriveManifestKey(masterKey, shareId);
        try
        {
            using var aes = new AesGcm(key, tagSizeInBytes: AuthenticationTagBytes);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes(string.Concat("DropSpaceShare:v1\n", shareId.ToString("N"))));
            var manifest = JsonSerializer.Deserialize<ManifestPlaintext>(plaintext, JsonOptions)
                ?? throw new InvalidDataException("The encrypted share manifest is empty.");
            if (manifest.ShareId != shareId) throw new InvalidDataException("The encrypted share manifest belongs to another share.");
            return manifest.Items;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static string ToUrlFragment(ReadOnlySpan<byte> masterKey) => Base64Url(masterKey);

    public static byte[] FromUrlFragment(string fragment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fragment);
        var normalized = fragment.StartsWith("#k=", StringComparison.Ordinal) ? fragment[3..] : fragment;
        byte[] key;
        try
        {
            key = FromBase64Url(normalized);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The secure share URL key is invalid.", exception);
        }
        if (key.Length != 32) throw new InvalidDataException("The secure share URL key is invalid.");
        return key;
    }

    /// <summary>
    /// Canonical manifest wire format shared with share-worker/src/index.js:
    /// 12-byte nonce | ciphertext | 16-byte authentication tag.
    /// </summary>
    public static byte[] PackManifestWire(ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag)
    {
        if (nonce.Length != ManifestNonceBytes || tag.Length != AuthenticationTagBytes) throw new InvalidDataException("The manifest wire fields are invalid.");
        var output = new byte[checked(nonce.Length + ciphertext.Length + tag.Length)];
        nonce.CopyTo(output);
        ciphertext.CopyTo(output.AsSpan(nonce.Length));
        tag.CopyTo(output.AsSpan(nonce.Length + ciphertext.Length));
        return output;
    }

    public static (byte[] Nonce, byte[] Ciphertext, byte[] Tag) UnpackManifestWire(ReadOnlySpan<byte> wire)
    {
        if (wire.Length < ManifestNonceBytes + AuthenticationTagBytes) throw new InvalidDataException("The encrypted manifest wire payload is truncated.");
        return (
            wire[..ManifestNonceBytes].ToArray(),
            wire[ManifestNonceBytes..^AuthenticationTagBytes].ToArray(),
            wire[^AuthenticationTagBytes..].ToArray());
    }

    /// <summary>Canonical chunk wire format: ciphertext | 16-byte authentication tag.</summary>
    public static byte[] PackChunkWire(ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag)
    {
        if (tag.Length != AuthenticationTagBytes) throw new InvalidDataException("The encrypted chunk tag is invalid.");
        var output = new byte[checked(ciphertext.Length + tag.Length)];
        ciphertext.CopyTo(output);
        tag.CopyTo(output.AsSpan(ciphertext.Length));
        return output;
    }

    private static byte[] DeriveFileKey(ReadOnlySpan<byte> masterKey, Guid shareId, Guid fileId) =>
        DeriveKey(masterKey, GuidWireBytes(shareId), Encoding.UTF8.GetBytes(string.Concat("file:", fileId.ToString("N"))));

    private static byte[] DeriveManifestKey(ReadOnlySpan<byte> masterKey, Guid shareId) =>
        DeriveKey(masterKey, GuidWireBytes(shareId), Encoding.UTF8.GetBytes("manifest"));

    private static byte[] DeriveKey(
        ReadOnlySpan<byte> masterKey,
        ReadOnlySpan<byte> salt,
        ReadOnlySpan<byte> info)
    {
        var keyMaterial = masterKey.ToArray();
        try
        {
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, keyMaterial, 32, salt, info);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyMaterial);
        }
    }

    /// <summary>
    /// The v1 worker uses the .NET Guid byte order for HKDF salt (the first three Guid
    /// fields are little-endian). Keep this explicit so browser and Windows derivations
    /// cannot silently drift to RFC 4122 network order.
    /// </summary>
    public static byte[] GuidWireBytes(Guid value) => value.ToByteArray();

    private static void ValidateMasterKey(ReadOnlySpan<byte> masterKey)
    {
        if (masterKey.Length != 32) throw new InvalidDataException("The secure share master key is invalid.");
    }

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
