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
            try { return [new(CudaLyricsRuntimePackage.RuntimeId, "NVIDIA CUDA 13", "DlcCudaPurpose",
                package.DownloadBytes, true, "github.com/airanluo-dot/DropSpace")]; }
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
        var inventory = await package.InspectAsync(token).ConfigureAwait(false);
        // Driver initialization can be slow. Probe once per inventory refresh, never in a
        // UI getter/render; the actual inference path performs its own admission checks.
        var compatible = await Task.Run(() => service.IsNvidia, token).ConfigureAwait(false);
        return inventory with { CanDownload = compatible, UnavailableReasonResourceKey = compatible ? null : "DlcCudaUnsupported" };
    }
    public async Task DownloadAsync(string id, bool consent, IProgress<double>? progress, CancellationToken token)
    {
        RequireId(id);
        if (!await Task.Run(() => service.IsNvidia, token).ConfigureAwait(false))
            throw new InvalidOperationException("A compatible NVIDIA driver is required.");
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
