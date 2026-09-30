using DropSpace.Core.Preview;
using Microsoft.Extensions.Logging;

namespace DropSpace.Infrastructure.Preview;

public sealed class PreviewProviderRegistry(
    IEnumerable<IPreviewProvider> providers,
    IPreviewCache cache,
    ILogger<PreviewProviderRegistry> logger) : IPreviewProviderRegistry
{
    private readonly SemaphoreSlim _operationGate = new(2, 2);
    private readonly IReadOnlyList<IPreviewProvider> _providers = providers
        .OrderByDescending(provider => provider.Priority)
        .ThenBy(provider => provider.Id, StringComparer.Ordinal)
        .ToArray();

    public IReadOnlyList<IPreviewProvider> Providers => _providers;

    public Task<PreviewCapability> ProbeAsync(
        DropItemSnapshot item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return RunBoundedAsync(() => ProbeCoreAsync(item, cancellationToken), cancellationToken);
    }

    private async Task<PreviewCapability> ProbeCoreAsync(
        DropItemSnapshot item,
        CancellationToken cancellationToken)
    {
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

    public Task<PreviewDescriptor> LoadAsync(
        PreviewRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunBoundedAsync(() => LoadCoreAsync(request, cancellationToken), cancellationToken);
    }

    private async Task<PreviewDescriptor> LoadCoreAsync(
        PreviewRequest request,
        CancellationToken cancellationToken)
    {
        var generation = cache.Generation;
        var capability = await ProbeCoreAsync(request.Item, cancellationToken).ConfigureAwait(false);
        if (!capability.CanPreview)
        {
            return UnknownPreviewProvider.CreateFallback(request.Item);
        }

        if (!request.Item.HasExternalSource)
        {
            try
            {
                var cacheHit = await cache.TryGetAsync(
                    request.Item.Id, request.Item.Revision, capability.Kind,
                    request.Page, request.TargetPixelWidth, cancellationToken).ConfigureAwait(false);
                if (cacheHit is not null) return cacheHit with { CacheGeneration = generation };
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(exception, "Preview cache unavailable; loading the source.");
            }
        }

        var provider = _providers.First(provider => string.Equals(provider.Id, capability.ProviderId, StringComparison.Ordinal));
        try
        {
            var descriptor = (await provider.LoadAsync(request, cancellationToken).ConfigureAwait(false))
                with { CacheGeneration = generation };
            // PDF is cached only after successful platform rendering in the App.
            if (capability.Kind != PreviewKind.Pdf && !request.Item.HasExternalSource)
            {
                try
                {
                    await cache.PutAsync(request, descriptor, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    logger.LogDebug(exception, "Preview cache write failed; the generated preview remains usable.");
                }
            }
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

    private async Task<T> RunBoundedAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var work = Task.Run(operation, CancellationToken.None);
        try
        {
            return await work.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // A cancelled caller cannot interrupt synchronous filesystem/provider IO.
            // Keep its capacity occupied until that work actually exits.
            if (work.IsCompleted) _operationGate.Release();
            else _ = ReleaseGateAfterAsync(work);
        }
    }

    private async Task ReleaseGateAfterAsync(Task work)
    {
        try { await work.ConfigureAwait(false); }
        catch (Exception) { }
        finally { _operationGate.Release(); }
    }
}
