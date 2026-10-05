namespace DropSpace.Core.Abstractions;

/// <summary>One owned package. Unknown sizes stay null; bundled components are not downloads.</summary>
public sealed record DlcPackageDescriptor(
    string Id, string Name, string PurposeResourceKey, long? DownloadBytes,
    bool CanDownload = true, string? Source = null,
    string? DownloadConfirmationResourceKey = null, string? DeleteConfirmationResourceKey = null);

/// <summary>Local installation inventory and actual app-owned bytes, including resumable artifacts.
/// IsInstalled means the expected final files and sizes are present, not permission to load them.
/// Execution and installation must perform their own full integrity verification.</summary>
public sealed record DlcPackageInspection(
    bool IsInstalled, bool HasArtifacts, long? InstalledBytes,
    bool CanDownload = true, string? UnavailableReasonResourceKey = null, bool CanDelete = true);

/// <summary>
/// Adapts an existing model/runtime package service. IDs must be globally unique.
/// Inspect is read-only metadata inventory; it must not hash large payloads or prepare a runtime.
/// Download requires explicit consent and never activates a package.
/// All HTTP package payloads must use the application-owned shared download engine;
/// providers supply fixed staging paths, trusted request policies and integrity metadata.
/// Never create a per-provider connection/transfer/bandwidth budget or single-stream fallback.
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
