using DropSpace.App.Services.Media;
using DropSpace.Core.Abstractions;
using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.App.Services.Dlc;

/// <summary>Stateless adapter; DLC alone owns download/inspection UI state.</summary>
public sealed class CudaRuntimeDlcProvider(CudaLyricsRuntimePackage package, AiLyricsService service) : IDlcPackageProvider
{
    public IReadOnlyList<DlcPackageDescriptor> Packages
    {
        get
        {
            // Builds without the exact release trust anchor do not advertise a placeholder.
            try { return [new(CudaLyricsRuntimePackage.RuntimeId, "NVIDIA CUDA 12", "DlcCudaPurpose",
                package.DownloadBytes, service.IsNvidia, "github.com/airanluo-dot/DropSpace")]; }
            catch (Exception error) when (error is IOException or InvalidDataException or System.Text.Json.JsonException)
            { return []; }
        }
    }
    public event EventHandler? PackagesChanged;
    private static void RequireId(string id)
    {
        if (id != CudaLyricsRuntimePackage.RuntimeId) throw new ArgumentException("Unknown CUDA package.", nameof(id));
    }
    public async Task<DlcPackageInspection> InspectAsync(string id, CancellationToken token)
    {
        RequireId(id);
        var bytes = package.GetOwnedArtifactBytes();
        var installed = false;
        if (package.HasInstalledFiles)
            try { await package.EnsureWorkerAsync(token).ConfigureAwait(false); installed = true; }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { }
        return new(installed, bytes > 0, bytes, service.IsNvidia, service.IsNvidia ? null : "DlcCudaUnsupported");
    }
    public async Task DownloadAsync(string id, bool consent, IProgress<double>? progress, CancellationToken token)
    {
        RequireId(id);
        if (!service.IsNvidia) throw new InvalidOperationException("A compatible NVIDIA driver is required.");
        await service.DownloadCudaComponentsAsync(consent, progress, token).ConfigureAwait(false);
        PackagesChanged?.Invoke(this, EventArgs.Empty);
    }
    public async Task DeleteAsync(string id, CancellationToken token)
    {
        RequireId(id);
        await service.DeleteCudaComponentsAsync(token).ConfigureAwait(false);
        PackagesChanged?.Invoke(this, EventArgs.Empty);
    }
}
