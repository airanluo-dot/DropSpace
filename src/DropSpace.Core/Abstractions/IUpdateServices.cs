using DropSpace.Core.Models;
using DropSpace.Core.Updates;

namespace DropSpace.Core.Abstractions;

public interface IUpdateSource
{
    Task<IReadOnlyList<UpdateRelease>> GetReleasesAsync(CancellationToken cancellationToken = default);

    Task<ReadOnlyMemory<byte>> GetManifestAsync(
        UpdateRelease release,
        CancellationToken cancellationToken = default);
}

public interface IUpdateDownloader
{
    Task<DownloadedUpdate> DownloadAsync(
        UpdateCandidate candidate,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IUpdateVerifier
{
    Task<bool> VerifyIntegrityAsync(
        DownloadedUpdate update,
        CancellationToken cancellationToken = default);
}

public interface ITrustedUpdateVerifier
{
    Task<TrustedUpdateVerification> VerifyPublisherAsync(
        string filePath,
        CancellationToken cancellationToken = default);
}

public interface IUpdateInstallerLauncher
{
    Task<bool> LaunchAsync(
        DownloadedUpdate update,
        CancellationToken cancellationToken = default);
}

public interface IDeploymentModeService
{
    DeploymentMode Current { get; }
}

public interface IUpdateService
{
    ReleaseVersion CurrentVersion { get; }

    UpdateStatusSnapshot Status { get; }

    event EventHandler<UpdateStatusSnapshot>? StatusChanged;

    Task<UpdateStatusSnapshot> RecoverPendingAsync(CancellationToken cancellationToken = default);

    Task<UpdateStatusSnapshot> CheckAtStartupAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default);

    Task<UpdateStatusSnapshot> CheckManuallyAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default);

    Task<UpdateStatusSnapshot> DownloadAsync(CancellationToken cancellationToken = default);

    Task<UpdateStatusSnapshot> InstallAsync(
        bool unattended,
        CancellationToken cancellationToken = default);

    Task MarkUpdatedLaunchAsync(
        ReleaseVersion updatedVersion,
        CancellationToken cancellationToken = default);
}
