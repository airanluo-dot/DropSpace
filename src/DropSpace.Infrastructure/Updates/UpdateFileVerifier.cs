using System.Security.Cryptography;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Updates;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Updates;

public sealed class UpdateFileVerifier(AppStoragePaths paths) : IUpdateVerifier
{
    public async Task<bool> VerifyIntegrityAsync(
        DownloadedUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.Size <= 0 || update.Sha256.Length != 64 ||
            !update.Sha256.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            return false;
        }

        var updatesRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.Updates));
        var filePath = Path.GetFullPath(update.FilePath);
        var relative = Path.GetRelativePath(updatesRoot, filePath);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ||
            !File.Exists(filePath) || new FileInfo(filePath).Length != update.Size)
        {
            return false;
        }

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actual = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(update.Sha256));
    }
}
