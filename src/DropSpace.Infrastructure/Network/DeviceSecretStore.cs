using System.Security.Cryptography;
using System.Runtime.Versioning;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Network;

[SupportedOSPlatform("windows")]
public sealed class DeviceSecretStore(AppStoragePaths paths)
{
    private const int MinimumSecretBytes = 16;
    private const int MaximumSecretBytes = 1024;
    private const int MaximumProtectedSecretBytes = 16 * 1024;

    public async Task SaveAsync(Guid peerId, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken = default)
    {
        if (peerId == Guid.Empty || secret.Length is < MinimumSecretBytes or > MaximumSecretBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(secret));
        }

        paths.EnsureCreated();
        var directory = Path.Combine(paths.Data, "secrets");
        Directory.CreateDirectory(directory);
        var path = GetPath(directory, peerId);
        var temporary = string.Concat(path, ".", Guid.NewGuid().ToString("N"), ".tmp");
        var plaintext = secret.ToArray();
        byte[]? protectedBytes = null;
        try
        {
            protectedBytes = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
            await File.WriteAllBytesAsync(temporary, protectedBytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (protectedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(protectedBytes);
            }

            TryDeleteTemporary(temporary);
        }
    }

    public async Task<byte[]?> GetAsync(Guid peerId, CancellationToken cancellationToken = default)
    {
        var path = GetPath(Path.Combine(paths.Data, "secrets"), peerId);
        if (!File.Exists(path)) return null;

        var info = new FileInfo(path);
        if (info.Length is <= 0 or > MaximumProtectedSecretBytes)
        {
            throw new InvalidDataException("The protected device secret exceeds the bounded storage policy.");
        }

        var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            if (protectedBytes.Length is <= 0 or > MaximumProtectedSecretBytes)
            {
                throw new InvalidDataException("The protected device secret exceeds the bounded storage policy.");
            }

            var plaintext = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            if (plaintext.Length is < MinimumSecretBytes or > MaximumSecretBytes)
            {
                CryptographicOperations.ZeroMemory(plaintext);
                throw new InvalidDataException("The unprotected device secret length is invalid.");
            }

            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
        }
    }

    public Task DeleteAsync(Guid peerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetPath(Path.Combine(paths.Data, "secrets"), peerId);
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (FileNotFoundException)
        {
            // Deletion is idempotent if another cleanup already removed the secret.
        }

        return Task.CompletedTask;
    }

    private static void TryDeleteTemporary(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // A failed cleanup must not mask the primary secret-write failure.
        }
        catch (UnauthorizedAccessException)
        {
            // A failed cleanup must not mask the primary secret-write failure.
        }
    }

    private static string GetPath(string directory, Guid peerId) => Path.Combine(directory, string.Concat(peerId.ToString("N"), ".bin"));
}
