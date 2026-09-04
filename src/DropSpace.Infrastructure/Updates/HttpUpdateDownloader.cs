using System.Security.Cryptography;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Updates;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Updates;

public sealed class HttpUpdateDownloader(
    HttpClient client,
    AppStoragePaths paths,
    UpdateStateStore stateStore) : IUpdateDownloader
{
    public async Task<DownloadedUpdate> DownloadAsync(
        UpdateCandidate candidate,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var descriptor = candidate.DeploymentMode switch
        {
            DeploymentMode.Installer => candidate.Manifest.Installer,
            DeploymentMode.Portable => candidate.Manifest.Portable,
            _ => throw new InvalidOperationException("Packaged deployments are updated by Windows and cannot download an Inno payload."),
        };
        if (!string.Equals(descriptor.AssetName, candidate.SelectedAsset.Name, StringComparison.Ordinal) ||
            descriptor.Size != candidate.SelectedAsset.Size ||
            !UpdateManifestParser.IsOfficialDownloadUri(
                candidate.SelectedAsset.DownloadUri,
                candidate.Release.TagName,
                candidate.SelectedAsset.Name))
        {
            throw new InvalidDataException("The selected download does not match the validated update manifest.");
        }

        paths.EnsureCreated();
        var versionDirectory = GetContainedVersionDirectory(candidate.Manifest.Version);
        Directory.CreateDirectory(versionDirectory);
        var finalPath = GetContainedChildPath(versionDirectory, descriptor.AssetName);
        var partialPath = string.Concat(finalPath, ".download");
        var logPath = GetContainedChildPath(versionDirectory, "update-install.log");
        if (File.Exists(finalPath))
        {
            var existing = new DownloadedUpdate(candidate, finalPath, descriptor.Size, descriptor.Sha256, logPath);
            if (await VerifyFileAsync(existing, cancellationToken).ConfigureAwait(false))
            {
                await stateStore.SaveAsync(existing, "ReadyToInstall", cancellationToken).ConfigureAwait(false);
                return existing;
            }

            File.Delete(finalPath);
        }

        if (File.Exists(partialPath))
        {
            File.Delete(partialPath);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, candidate.SelectedAsset.DownloadUri);
            request.Headers.UserAgent.ParseAdd($"DropSpace/{candidate.Manifest.Version}");
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long contentLength && contentLength != descriptor.Size)
            {
                throw new InvalidDataException("The update Content-Length does not match the signed-off manifest size.");
            }

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long total = 0;
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                total = checked(total + read);
                if (total > descriptor.Size)
                {
                    throw new InvalidDataException("The update stream exceeded the manifest size.");
                }

                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                progress?.Report(new UpdateDownloadProgress(total, descriptor.Size));
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (total != descriptor.Size)
            {
                throw new InvalidDataException("The update stream ended before the manifest size was reached.");
            }

            var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualHash),
                    Convert.FromHexString(descriptor.Sha256)))
            {
                throw new InvalidDataException("The downloaded update failed SHA-256 verification.");
            }

            output.Close();
            File.Move(partialPath, finalPath, true);
            var downloaded = new DownloadedUpdate(candidate, finalPath, total, actualHash, logPath);
            await stateStore.SaveAsync(downloaded, "ReadyToInstall", cancellationToken).ConfigureAwait(false);
            return downloaded;
        }
        catch
        {
            TryDeletePartial(partialPath);
            throw;
        }
    }

    private static void TryDeletePartial(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Update partial cleanup deferred: {exception.GetType().Name}");
        }
        catch (UnauthorizedAccessException exception)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Update partial cleanup deferred: {exception.GetType().Name}");
        }
    }

    internal async Task<bool> VerifyFileAsync(DownloadedUpdate update, CancellationToken cancellationToken)
    {
        if (!File.Exists(update.FilePath)) return false;
        var info = new FileInfo(update.FilePath);
        if (info.Length != update.Size) return false;
        await using var stream = new FileStream(
            update.FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(update.Sha256));
    }

    private string GetContainedVersionDirectory(ReleaseVersion version) =>
        GetContainedChildPath(paths.Updates, version.ToString());

    private static string GetContainedChildPath(string root, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new InvalidDataException("Update cache names may not contain path separators.");
        }

        var fullRoot = Path.GetFullPath(root);
        var candidate = Path.GetFullPath(Path.Combine(fullRoot, name));
        var relative = Path.GetRelativePath(fullRoot, candidate);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new InvalidDataException("The update cache path escaped the DropSpace-owned root.");
        }

        return candidate;
    }
}
