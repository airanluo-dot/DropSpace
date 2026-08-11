using System.Text.Json;
using DropSpace.Core.Updates;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Updates;

public sealed class UpdateStateStore(AppStoragePaths paths)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public async Task SaveAsync(
        DownloadedUpdate update,
        string state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentException.ThrowIfNullOrWhiteSpace(state);
        var descriptor = new PersistedUpdateState(
            1,
            state,
            update.Candidate.Manifest.Version.ToString(),
            update.Candidate.Manifest.Channel.ToString().ToLowerInvariant(),
            update.Candidate.Manifest.VersionCode,
            update.Candidate.Manifest.PublishedAt,
            update.Candidate.Manifest.MinimumWindowsBuild,
            update.Candidate.Manifest.Mandatory,
            update.Candidate.Manifest.Summary,
            update.Candidate.Manifest.Installer,
            update.Candidate.Manifest.Portable,
            update.Candidate.Release.TagName,
            update.Candidate.Release.IsPrerelease,
            update.Candidate.Release.HtmlUri.AbsoluteUri,
            update.Candidate.SelectedAsset.Name,
            update.Candidate.SelectedAsset.Size,
            update.Candidate.SelectedAsset.DownloadUri.AbsoluteUri,
            update.Candidate.DeploymentMode.ToString(),
            Path.GetFileName(update.FilePath),
            Path.GetFileName(update.InstallLogPath),
            DateTimeOffset.UtcNow);
        var statePath = Path.Combine(Path.GetDirectoryName(update.FilePath)!, "update-state.json");
        var temporaryPath = string.Concat(statePath, ".tmp");
        await using (var stream = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         16 * 1024,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, descriptor, Options, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, statePath, true);
    }

    public async Task MarkUpdatedLaunchAsync(
        ReleaseVersion version,
        CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        var markerPath = Path.Combine(paths.Updates, "last-update.json");
        var temporaryPath = string.Concat(markerPath, ".tmp");
        var marker = new UpdatedLaunchState(1, version.ToString(), DateTimeOffset.UtcNow);
        await using (var stream = new FileStream(
                         temporaryPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         4096,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, marker, Options, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, markerPath, true);
    }

    public async Task<(DownloadedUpdate Update, string State)?> LoadHighestAsync(
        ReleaseVersion currentVersion,
        DeploymentMode currentMode,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(paths.Updates)) return null;
        var candidates = new List<(DownloadedUpdate Update, string State)>();
        foreach (var statePath in Directory.EnumerateFiles(paths.Updates, "update-state.json", SearchOption.AllDirectories).Take(20))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (new FileInfo(statePath).Length > 64 * 1024) continue;
                await using var stream = File.OpenRead(statePath);
                var state = await JsonSerializer.DeserializeAsync<PersistedUpdateState>(stream, Options, cancellationToken)
                    .ConfigureAwait(false);
                if (state is null || state.SchemaVersion != 1 ||
                    !ReleaseVersion.TryParse(state.Version, out var version) || version <= currentVersion ||
                    !Enum.TryParse<UpdateChannel>(state.Channel, true, out var channel) ||
                    !Enum.TryParse<DeploymentMode>(state.DeploymentMode, out var mode) || mode != currentMode ||
                    state.VersionCode != version.ToVersionCode() ||
                    state.Installer is null || state.Portable is null ||
                    !Uri.TryCreate(state.ReleaseUrl, UriKind.Absolute, out var releaseUrl) ||
                    !Uri.TryCreate(state.DownloadUrl, UriKind.Absolute, out var downloadUrl) ||
                    state.State is not ("ReadyToInstall" or "Installing") ||
                    releaseUrl.Scheme != Uri.UriSchemeHttps ||
                    !string.Equals(releaseUrl.Host, "github.com", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var manifest = new UpdateManifest(
                    1,
                    channel,
                    version,
                    state.VersionCode,
                    state.PublishedAt,
                    state.MinimumWindowsBuild,
                    state.Mandatory,
                    state.Summary,
                    state.Installer,
                    state.Portable);
                var selected = new UpdateReleaseAsset(state.SelectedAssetName, state.SelectedAssetSize, downloadUrl);
                var release = new UpdateRelease(
                    state.TagName,
                    false,
                    state.IsPrerelease,
                    state.PublishedAt,
                    releaseUrl,
                    [selected]);
                var candidate = new UpdateCandidate(release, manifest, selected, mode);
                var directory = Path.GetDirectoryName(statePath)!;
                var filePath = GetContainedPath(directory, state.FileName);
                var logPath = GetContainedPath(directory, state.LogFileName);
                var expected = mode == DeploymentMode.Installer ? manifest.Installer : manifest.Portable;
                if (!string.Equals(expected.AssetName, state.SelectedAssetName, StringComparison.Ordinal) ||
                    expected.Size != state.SelectedAssetSize || expected.Size <= 0 ||
                    expected.Sha256.Length != 64 ||
                    !expected.Sha256.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f') ||
                    !UpdateManifestParser.IsOfficialDownloadUri(downloadUrl, state.TagName, expected.AssetName) ||
                    !string.Equals(state.FileName, expected.AssetName, StringComparison.Ordinal) ||
                    !string.Equals(state.LogFileName, "update-install.log", StringComparison.Ordinal))
                {
                    continue;
                }
                candidates.Add((new DownloadedUpdate(candidate, filePath, expected.Size, expected.Sha256, logPath), state.State));
            }
            catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
            {
                // A malformed cache entry is ignored. It is never promoted to executable state.
            }
        }

        return candidates.Count == 0
            ? null
            : candidates.OrderByDescending(candidate => candidate.Update.Candidate.Manifest.Version).First();
    }

    private static string GetContainedPath(string root, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new InvalidDataException("Invalid persisted update cache name.");
        }

        var fullRoot = Path.GetFullPath(root);
        var result = Path.GetFullPath(Path.Combine(fullRoot, name));
        if (Path.GetRelativePath(fullRoot, result).StartsWith("..", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Persisted update cache path escaped its version directory.");
        }

        return result;
    }

    private sealed record PersistedUpdateState(
        int SchemaVersion,
        string State,
        string Version,
        string Channel,
        int VersionCode,
        DateTimeOffset PublishedAt,
        int MinimumWindowsBuild,
        bool Mandatory,
        string? Summary,
        UpdateManifestAsset Installer,
        UpdateManifestAsset Portable,
        string TagName,
        bool IsPrerelease,
        string ReleaseUrl,
        string SelectedAssetName,
        long SelectedAssetSize,
        string DownloadUrl,
        string DeploymentMode,
        string FileName,
        string LogFileName,
        DateTimeOffset UpdatedAtUtc);

    private sealed record UpdatedLaunchState(int SchemaVersion, string Version, DateTimeOffset CompletedAtUtc);
}
