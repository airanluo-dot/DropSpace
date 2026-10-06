using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Downloads;

internal static class DownloadStorage
{
    public static string NormalizeDirectory(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    public static bool SameDirectory(string left, string right) => string.Equals(
        NormalizeDirectory(left), NormalizeDirectory(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    public static string Identity(Uri uri) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)));
    public static string Safe(string root, string path)
    {
        root = NormalizeDirectory(root);
        return ReparseSafePathPolicy.PrepareContainedFileDestination(root, Path.GetRelativePath(root, path));
    }
    public static IEnumerable<string> Artifacts(string staging)
    {
        var root = Path.GetDirectoryName(staging)!;
        foreach (var suffix in new[] { "", ".resume.json", ".resume.json.tmp", ".request.json", ".request.json.tmp" })
        {
            var path = ReparseSafePathPolicy.ResolveOwnedFilePathForDeletion(root, Path.GetFileName(staging) + suffix);
            if (File.Exists(path)) yield return ReparseSafePathPolicy.ResolveExistingContainedPath(root, path);
        }
        var ranges = staging + ".ranges";
        if (!Directory.Exists(ranges)) yield break;
        ReparseSafePathPolicy.ResolveExistingContainedPath(root, ranges);
        foreach (var name in new[] { "identity.json", "identity.json.tmp" }.Concat(Enumerable.Range(0, 256).Select(i => $"part-{i:D2}.bin")))
        {
            var path = Path.Combine(ranges, name);
            if (File.Exists(path)) yield return ReparseSafePathPolicy.ResolveExistingContainedPath(root, path);
        }
    }
    public static void Clean(string staging)
    {
        foreach (var path in Artifacts(staging).ToArray()) File.Delete(path);
        var ranges = staging + ".ranges";
        if (Directory.Exists(ranges) && !Directory.EnumerateFileSystemEntries(ranges).Any()) Directory.Delete(ranges);
    }
    public static void CheckSpace(string path, long bytes)
    {
        var drive = new DriveInfo(Path.GetPathRoot(path)!);
        if (drive.IsReady) CheckAvailableSpace(bytes, drive.AvailableFreeSpace);
    }
    internal static void CheckAvailableSpace(long bytes, long availableBytes)
    {
        if (bytes < 0 || availableBytes < 0 || availableBytes < checked(bytes + 64L * 1024 * 1024))
            throw new IOException("Insufficient download disk space.");
    }
    public static async Task WriteAsync<T>(string path, T value, CancellationToken token)
    {
        Safe(Path.GetDirectoryName(path)!, path);
        Safe(Path.GetDirectoryName(path)!, path + ".tmp");
        await using (var file = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(file, value, cancellationToken: token).ConfigureAwait(false);
            await file.FlushAsync(token).ConfigureAwait(false);
            file.Flush(true);
        }
        File.Move(path + ".tmp", path, true);
    }
}
