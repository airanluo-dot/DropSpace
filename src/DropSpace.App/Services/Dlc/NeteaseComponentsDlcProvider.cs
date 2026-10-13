using System.Runtime.InteropServices;
using DropSpace.App.Services.NeteaseEnhancement;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Media;

namespace DropSpace.App.Services.Dlc;

/// <summary>Inventory only on refresh; explicit actions retain the existing transactional installer.</summary>
public sealed class NeteaseComponentsDlcProvider : IDlcPackageProvider, IDisposable
{
    private const string EnhancementId = "netease-music-enhancement";
    private const string RuntimeId = "netease-visual-cpp-runtime";
    // Both official enhancement assets have this installation limit. Do not hash an
    // externally replaced, oversized file as part of otherwise lightweight inventory.
    private const long MaximumInspectionAssetBytes = 32 * 1024 * 1024;
    private readonly NeteaseInstallationProbe _probe;
    private readonly InfLinkDeploymentService _deployment;
    private readonly INeteaseEnhancementService _enhancement;
    private readonly NeteaseRuntimeInstaller _runtime;
    private readonly IAppStringLocalizer _strings;

    public NeteaseComponentsDlcProvider(NeteaseInstallationProbe probe, InfLinkDeploymentService deployment,
        INeteaseEnhancementService enhancement, NeteaseRuntimeInstaller runtime, IAppStringLocalizer strings)
    {
        _probe = probe; _deployment = deployment; _enhancement = enhancement; _runtime = runtime; _strings = strings;
        enhancement.Changed += OnEnhancementChanged;
        runtime.Changed += OnRuntimeChanged;
    }

    public IReadOnlyList<DlcPackageDescriptor> Packages =>
    [
        new(EnhancementId, _strings.Get("NeteaseEnhancementTitle"), "NeteaseEnhancementDescription", null,
            Source: "github.com/apoint123/inflink-rs · github.com/std-microblock/chromatic",
            DownloadConfirmationResourceKey: "NeteaseEnhancementConfirmBody", DeleteConfirmationResourceKey: "DlcNeteaseRemoveNotice"),
        new(RuntimeId, _strings.Get("DlcVisualCppTitle"), "DlcVisualCppPurpose", null,
            Source: "Microsoft", DownloadConfirmationResourceKey: "DlcVisualCppInstallNotice"),
    ];

    public event EventHandler? PackagesChanged;
    private void OnRuntimeChanged(object? sender, EventArgs args) => PackagesChanged?.Invoke(this, EventArgs.Empty);
    private void OnEnhancementChanged(object? sender, NeteaseEnhancementState state)
    {
        if (!state.IsBusy) PackagesChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<DlcPackageInspection> InspectAsync(string packageId, CancellationToken token)
    {
        RequireId(packageId);
        var installation = await _probe.FindAsync(token).ConfigureAwait(false);
        if (packageId == RuntimeId)
        {
            var architecture = installation?.Architecture ?? Architecture.X64;
            var installed = NeteaseRuntimeInstaller.Installed(architecture);
            // This shared Windows prerequisite belongs to the system, not DropSpace.
            return new(installed, false, null, !_enhancement.Current.IsBusy,
                "DlcVisualCppShared", CanDelete: false);
        }
        if (installation is null) return new(false, false, null, false, "NeteaseEnhancementErrorNotFound", CanDelete: false);
        var receipt = await _deployment.GetManagedReceiptAsync(installation, token).ConfigureAwait(false);
        var local = await new BetterNcmProbe().FindAsync(installation, token).ConfigureAwait(false);
        long ownedBytes = 0;
        var boundedFiles = true;
        if (receipt is not null)
            foreach (var file in receipt.Files)
            {
                token.ThrowIfCancellationRequested();
                DeploymentPaths.AssertSafe(file.TargetPath);
                if (!File.Exists(file.TargetPath)) continue;
                var length = new FileInfo(file.TargetPath).Length;
                boundedFiles &= length <= MaximumInspectionAssetBytes;
                if (file.Owned) ownedBytes = checked(ownedBytes + length);
            }
        // The Music card already distinguishes local presence from intact managed
        // bytes. Keep DLC inventory consistent without exercising the player or
        // contacting an update source. Independent installations remain presence-only.
        var enhancementInstalled = receipt is null ? local.LoaderPresent && local.PluginPresent :
            receipt.Committed && boundedFiles &&
                await _deployment.IsManagedInstallationIntactAsync(receipt, token).ConfigureAwait(false);
        var reason = receipt is { Committed: false } ? "NeteaseEnhancementErrorRollback" :
            receipt is not null && !enhancementInstalled ? "NeteaseEnhancementErrorConflict" : null;
        return new(enhancementInstalled,
            receipt is not null, receipt is null ? null : ownedBytes, !_enhancement.Current.IsBusy,
            UnavailableReasonResourceKey: reason,
            CanDelete: receipt is not null && !_enhancement.Current.IsBusy);
    }

    public async Task DownloadAsync(string packageId, bool consent, IProgress<double>? progress, CancellationToken token)
    {
        RequireId(packageId);
        if (!consent || _enhancement.Current.IsBusy) throw new InvalidOperationException("Component installation is unavailable.");
        if (packageId == RuntimeId)
        {
            var installation = await _probe.FindAsync(token).ConfigureAwait(false);
            await _runtime.EnsureAsync(installation?.Architecture ?? Architecture.X64, token).ConfigureAwait(false);
        }
        else
        {
            await _enhancement.EnhanceAsync(reinstall: true, cancellationToken: token).ConfigureAwait(false);
            EnsureOperationSucceeded(token);
        }
        PackagesChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeleteAsync(string packageId, CancellationToken token)
    {
        RequireId(packageId);
        if (packageId == RuntimeId || _enhancement.Current.IsBusy)
            throw new InvalidOperationException("Shared Windows components cannot be removed by DropSpace.");
        await _enhancement.RemoveAsync(token).ConfigureAwait(false);
        EnsureOperationSucceeded(token);
        PackagesChanged?.Invoke(this, EventArgs.Empty);
    }
    private void EnsureOperationSucceeded(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_enhancement.Current.Stage == NeteaseEnhancementStage.Failed)
            throw new EnhancementDeploymentException(_enhancement.Current.ErrorCode ?? "Unexpected");
    }
    private static void RequireId(string id)
    {
        if (id is not (EnhancementId or RuntimeId)) throw new ArgumentException("Unknown component.", nameof(id));
    }
    public void Dispose()
    {
        _enhancement.Changed -= OnEnhancementChanged;
        _runtime.Changed -= OnRuntimeChanged;
    }
}
