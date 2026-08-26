using System.Security.Cryptography;
using System.Runtime.Versioning;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Network;

[SupportedOSPlatform("windows")]
public sealed class DeviceSecretStore(AppStoragePaths paths)
{
    public async Task SaveAsync(Guid peerId, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken = default)
    {
        if (peerId == Guid.Empty || secret.Length is < 16 or > 1024) throw new ArgumentOutOfRangeException(nameof(secret));
        paths.EnsureCreated();
        var directory = Path.Combine(paths.Data, "secrets");
        Directory.CreateDirectory(directory);
        var path = GetPath(directory, peerId);
        var protectedBytes = ProtectedData.Protect(secret.ToArray(), null, DataProtectionScope.CurrentUser);
        var temporary = string.Concat(path, ".", Guid.NewGuid().ToString("N"), ".tmp");
        await File.WriteAllBytesAsync(temporary, protectedBytes, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, true);
    }

    public async Task<byte[]?> GetAsync(Guid peerId, CancellationToken cancellationToken = default)
    {
        var path = GetPath(Path.Combine(paths.Data, "secrets"), peerId);
        if (!File.Exists(path)) return null;
        var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
    }

    public Task DeleteAsync(Guid peerId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetPath(Path.Combine(paths.Data, "secrets"), peerId);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private static string GetPath(string directory, Guid peerId) => Path.Combine(directory, string.Concat(peerId.ToString("N"), ".bin"));
}
