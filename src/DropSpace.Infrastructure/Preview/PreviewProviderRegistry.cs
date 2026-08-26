using DropSpace.Core.Preview;
using Microsoft.Extensions.Logging;

namespace DropSpace.Infrastructure.Preview;

public sealed class PreviewProviderRegistry(
    IEnumerable<IPreviewProvider> providers,
    IPreviewCache cache,
    ILogger<PreviewProviderRegistry> logger) : IPreviewProviderRegistry
{
    private readonly IReadOnlyList<IPreviewProvider> _providers = providers
        .OrderByDescending(provider => provider.Priority)
        .ThenBy(provider => provider.Id, StringComparer.Ordinal)
        .ToArray();

    public IReadOnlyList<IPreviewProvider> Providers => _providers;

    public async Task<PreviewCapability> ProbeAsync(
        DropItemSnapshot item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        foreach (var provider in _providers)
        {
            try
            {
                var capability = await provider.ProbeAsync(item, cancellationToken).ConfigureAwait(false);
                if (capability.CanPreview)
                {
                    return capability;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                logger.LogDebug(exception, "Preview provider {ProviderId} could not probe the item.", provider.Id);
            }
        }

        return new PreviewCapability(
            false,
            PreviewKind.Unknown,
            "none",
            item.MimeType,
            "Preview is unavailable for this item.",
            item.KnownSize,
            null,
            null,
            null);
    }

    public async Task<PreviewDescriptor> LoadAsync(
        PreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var capability = await ProbeAsync(request.Item, cancellationToken).ConfigureAwait(false);
        if (!capability.CanPreview)
        {
            return UnknownPreviewProvider.CreateFallback(request.Item);
        }

        var cacheHit = await cache.TryGetAsync(
            request.Item.Id,
            request.Item.Revision,
            capability.Kind,
            request.Page,
            request.TargetPixelWidth,
            cancellationToken).ConfigureAwait(false);
        if (cacheHit is not null)
        {
            return cacheHit;
        }

        var provider = _providers.First(provider => string.Equals(provider.Id, capability.ProviderId, StringComparison.Ordinal));
        try
        {
            var descriptor = await provider.LoadAsync(request, cancellationToken).ConfigureAwait(false);
            await cache.PutAsync(request, descriptor, cancellationToken).ConfigureAwait(false);
            return descriptor;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            logger.LogWarning(exception, "Preview provider {ProviderId} failed while loading an item.", provider.Id);
            return UnknownPreviewProvider.CreateFallback(request.Item);
        }
    }
}
