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
        if (receipt is not null)
            foreach (var file in receipt.Files.Where(file => file.Owned))
            {
                DeploymentPaths.AssertSafe(file.TargetPath);
                if (File.Exists(file.TargetPath)) ownedBytes = checked(ownedBytes + new FileInfo(file.TargetPath).Length);
            }
        return new(local.LoaderPresent && local.PluginPresent && receipt?.Committed != false,
            receipt is not null, receipt is null ? null : ownedBytes, !_enhancement.Current.IsBusy,
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
