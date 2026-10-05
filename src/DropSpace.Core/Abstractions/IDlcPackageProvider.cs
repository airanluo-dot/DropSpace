namespace DropSpace.Core.Abstractions;

/// <summary>One owned package. Unknown sizes stay null; bundled components are not downloads.</summary>
public sealed record DlcPackageDescriptor(
    string Id, string Name, string PurposeResourceKey, long? DownloadBytes,
    bool CanDownload = true, string? Source = null);

/// <summary>Verified installation and actual app-owned bytes, including resumable artifacts.</summary>
public sealed record DlcPackageInspection(
    bool IsInstalled, bool HasArtifacts, long? InstalledBytes,
    bool CanDownload = true, string? UnavailableReasonResourceKey = null);

/// <summary>
/// Adapts an existing model/runtime package service. IDs must be globally unique.
/// Inspect is local-only. Download requires explicit consent and never activates a package.
/// Delete must cancel inference and retain maintenance ownership until native resources exit;
/// it removes only this provider's app-owned package files, never lyrics/cache/user files.
/// Providers own persistent receipts/manifests; DLC owns only transient operation state.
/// </summary>
public interface IDlcPackageProvider
{
    IReadOnlyList<DlcPackageDescriptor> Packages { get; }
    event EventHandler? PackagesChanged;
    Task<DlcPackageInspection> InspectAsync(string packageId, CancellationToken token);
    Task DownloadAsync(string packageId, bool consent, IProgress<double>? progress, CancellationToken token);
    Task DeleteAsync(string packageId, CancellationToken token);
}
