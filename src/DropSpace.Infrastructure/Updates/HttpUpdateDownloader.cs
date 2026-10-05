using DropSpace.Core.Downloads;
using DropSpace.Infrastructure.Downloads;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Updates;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Updates;

public sealed class HttpUpdateDownloader(
    HttpClient client,
    AppStoragePaths paths,
    UpdateStateStore stateStore,
    HttpRangeDownloader downloads) : IUpdateDownloader, IDisposable
{
    private readonly UpdateFileVerifier _fileVerifier = new(paths);

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
        var partialPath = GetContainedChildPath(versionDirectory, string.Concat(descriptor.AssetName, ".download"));
        var logPath = GetContainedChildPath(versionDirectory, "update-install.log");
        if (File.Exists(finalPath))
        {
            var existing = new DownloadedUpdate(candidate, finalPath, descriptor.Size, descriptor.Sha256, logPath);
            if (await _fileVerifier.VerifyIntegrityAsync(existing, cancellationToken).ConfigureAwait(false))
            {
                await stateStore.SaveAsync(existing, "ReadyToInstall", cancellationToken).ConfigureAwait(false);
                return existing;
            }

            File.Delete(finalPath);
        }

        // Every redirect is checked; the initial asset remains bound to the manifest above.
        var policy = new DownloadRequestPolicy(client, uri =>
            uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
            (UpdateManifestParser.IsOfficialDownloadUri(uri, candidate.Release.TagName, descriptor.AssetName) ||
             uri.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com"),
            request => request.Headers.UserAgent.ParseAdd($"DropSpace/{candidate.Manifest.Version}"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));
        try
        {
            await downloads.DownloadAsync(candidate.SelectedAsset.DownloadUri, partialPath, policy,
                progress is null ? null : new TransferProgress(progress, descriptor.Size),
                timeout.Token, descriptor.Size, descriptor.Sha256).ConfigureAwait(false);
            // Legacy length-only partials have no resource validator and safely restart.
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(partialPath, finalPath, true);
            TryDeletePartial(partialPath);
            var downloaded = new DownloadedUpdate(candidate, finalPath, descriptor.Size, descriptor.Sha256, logPath);
            await stateStore.SaveAsync(downloaded, "ReadyToInstall", cancellationToken).ConfigureAwait(false);
            return downloaded;
        }
        catch (InvalidDataException)
        {
            TryDeletePartial(partialPath);
            throw;
        }
        // Transport failure retains version-scoped staging for retry. Installation still
        // re-verifies the assembled final file, independently of the transfer engine.
    }

    private sealed class TransferProgress(IProgress<UpdateDownloadProgress> target, long total) : IProgress<TrackProgress>
    {
        public void Report(TrackProgress value) => target.Report(new(value.DownloadedBytes, total));
    }

    private static void TryDeletePartial(string path)
    {
        try { DownloadStorage.Clean(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Update partial cleanup deferred: {exception.GetType().Name}");
        }
    }

    public void Dispose() => client.Dispose();

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

        return ReparseSafePathPolicy.ResolveOwnedFilePathForDeletion(fullRoot, name);
    }
}
