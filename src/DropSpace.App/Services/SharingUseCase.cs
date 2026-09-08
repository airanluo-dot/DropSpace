using DropSpace.Core.Models;
using DropSpace.Core.Transfer;
using DropSpace.Infrastructure.Sharing;

namespace DropSpace.App.Services;

/// <summary>
/// UI-facing share-intent boundary. Item validation and source staging remain
/// owned by <see cref="ItemSharingService"/>.
/// </summary>
public sealed class SharingUseCase(ItemSharingService service)
{
    public bool IsInternetConfigured => service.IsInternetConfigured;

    public bool CanRevokeInternet(Guid shareId) => service.CanRevokeInternet(shareId);

    public Task<ShareDescriptor> CreateNearbyAsync(
        IReadOnlyList<DropItem> items,
        AppSettings settings,
        CancellationToken cancellationToken = default) =>
        service.CreateNearbyAsync(items, settings, cancellationToken);

    public Task<ShareDescriptor> CreateInternetAsync(
        IReadOnlyList<DropItem> items,
        AppSettings settings,
        TimeSpan? lifetime = null,
        CancellationToken cancellationToken = default) =>
        service.CreateInternetAsync(items, settings, lifetime, cancellationToken);

    public Task<bool> RevokeInternetAsync(Guid shareId, CancellationToken cancellationToken = default) =>
        service.RevokeInternetAsync(shareId, cancellationToken);
}
