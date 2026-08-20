namespace DropSpace.Core.Updates;

public enum UpdateChannel
{
    Stable,
    Preview,
}

public enum UpdateState
{
    Idle,
    Checking,
    UpToDate,
    UpdateAvailable,
    Downloading,
    ReadyToInstall,
    Installing,
    Failed,
}

public enum DeploymentMode
{
    Installer,
    Portable,
    Packaged,
}

public sealed record UpdateReleaseAsset(string Name, long Size, Uri DownloadUri);

public sealed record UpdateRelease(
    string TagName,
    bool IsDraft,
    bool IsPrerelease,
    DateTimeOffset? PublishedAt,
    Uri HtmlUri,
    IReadOnlyList<UpdateReleaseAsset> Assets);

public sealed record UpdateManifestAsset(string AssetName, long Size, string Sha256);

public sealed record UpdateManifest(
    int SchemaVersion,
    UpdateChannel Channel,
    ReleaseVersion Version,
    int VersionCode,
    DateTimeOffset PublishedAt,
    int MinimumWindowsBuild,
    bool Mandatory,
    string? Summary,
    UpdateManifestAsset Installer,
    UpdateManifestAsset Portable);

public sealed record UpdateCandidate(
    UpdateRelease Release,
    UpdateManifest Manifest,
    UpdateReleaseAsset SelectedAsset,
    DeploymentMode DeploymentMode);

public sealed record UpdateDownloadProgress(long BytesReceived, long TotalBytes)
{
    public double Percentage => TotalBytes <= 0 ? 0 : Math.Clamp(BytesReceived * 100d / TotalBytes, 0, 100);
}

public sealed record DownloadedUpdate(
    UpdateCandidate Candidate,
    string FilePath,
    long Size,
    string Sha256,
    string InstallLogPath);

public sealed record TrustedUpdateVerification(bool IsTrusted, string Reason);

public sealed record UpdateStatusSnapshot(
    UpdateState State,
    string Message,
    DeploymentMode DeploymentMode,
    DateTimeOffset? LastCheckedAtUtc = null,
    UpdateCandidate? Candidate = null,
    DownloadedUpdate? Download = null,
    UpdateDownloadProgress? Progress = null,
    bool TrustedAutoInstallAvailable = false,
    bool PreviousInstallIncomplete = false)
{
    public static UpdateStatusSnapshot Initial(DeploymentMode mode) =>
        new(UpdateState.Idle, string.Empty, mode);
}
