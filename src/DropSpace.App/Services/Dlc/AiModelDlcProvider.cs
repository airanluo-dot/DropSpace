using DropSpace.App.Services.Media;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Lyrics;

namespace DropSpace.App.Services.Dlc;

/// <summary>Adapts the production model services; no second downloader or installation receipt.</summary>
public sealed class AiModelDlcProvider(AiLyricsService service) : IDlcPackageProvider
{
    public IReadOnlyList<DlcPackageDescriptor> Packages { get; } = AiLyricsModelCatalog.All
        .Concat(AiLyricsModelCatalog.Legacy).Select(model => new DlcPackageDescriptor(
            model.Id, model.Name,
            model.Id == AiLyricsModelCatalog.ExperimentalLargePlain.Id ? "DlcLargeModelPurpose" :
            AiLyricsModelCatalog.FindSelectable(model.Id) is not null ? "DlcModelPurpose" : "DlcLegacyModelPurpose",
            model.Bytes, AiLyricsModelCatalog.FindSelectable(model.Id) is not null,
            model.DownloadUri.Host + "/" + string.Join('/', model.DownloadUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Take(2))))
        .ToArray();

    public event EventHandler? PackagesChanged
    {
        add => service.ModelDownloaded += value;
        remove => service.ModelDownloaded -= value;
    }

    public Task<DlcPackageInspection> InspectAsync(string packageId, CancellationToken token) =>
        service.InspectModelAsync(packageId, token);

    public Task DownloadAsync(string packageId, bool consent, IProgress<double>? progress, CancellationToken token) =>
        service.DownloadAsync(packageId, consent, progress, token);

    public async Task DeleteAsync(string packageId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // Availability is separate from user consent. Maintenance cancels and drains the
        // worker; a missing model cannot be resolved. Failure/cancellation must not change AI preference.
        await service.DeleteModelAsync(packageId, token);
    }
}
