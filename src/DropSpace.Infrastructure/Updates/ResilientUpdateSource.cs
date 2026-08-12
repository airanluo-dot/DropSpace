using System.Text.Json;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Updates;
using Microsoft.Extensions.Logging;

namespace DropSpace.Infrastructure.Updates;

public sealed class ResilientUpdateSource(
    IEnumerable<IUpdateSource> sources,
    ILogger<ResilientUpdateSource> logger,
    bool mergeReleaseMetadata = false) : IUpdateSource
{
    private readonly IUpdateSource[] _sources = sources.ToArray();

    public Task<IReadOnlyList<UpdateRelease>> GetReleasesAsync(CancellationToken cancellationToken = default) =>
        mergeReleaseMetadata
            ? MergeReleaseMetadataAsync(cancellationToken)
            : ExecuteAsync(
                static (source, token) => source.GetReleasesAsync(token),
                static releases => releases.Count > 0,
                "release metadata",
                cancellationToken);

    public Task<ReadOnlyMemory<byte>> GetManifestAsync(
        UpdateRelease release,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            (source, token) => source.GetManifestAsync(release, token),
            static manifest => !manifest.IsEmpty,
            "update manifest",
            cancellationToken);

    private async Task<T> ExecuteAsync<T>(
        Func<IUpdateSource, CancellationToken, Task<T>> operation,
        Func<T, bool> accept,
        string operationName,
        CancellationToken cancellationToken)
    {
        if (_sources.Length == 0) throw new InvalidOperationException("No update metadata sources are configured.");
        var failures = new List<Exception>();
        foreach (var source in _sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await operation(source, cancellationToken).ConfigureAwait(false);
                if (accept(result))
                {
                    logger.LogInformation(
                        "Update {OperationName} loaded from {SourceType}.",
                        operationName,
                        source.GetType().Name);
                    return result;
                }

                failures.Add(new InvalidDataException($"{source.GetType().Name} returned no {operationName}."));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or JsonException or TaskCanceledException)
            {
                failures.Add(exception);
                logger.LogWarning(
                    "Update {OperationName} source {SourceType} failed with {FailureType}; trying the next official source.",
                    operationName,
                    source.GetType().Name,
                    exception.GetType().Name);
            }
        }

        throw new HttpRequestException(
            $"All official DropSpace sources failed to provide {operationName}.",
            new AggregateException(failures));
    }

    private async Task<IReadOnlyList<UpdateRelease>> MergeReleaseMetadataAsync(
        CancellationToken cancellationToken)
    {
        if (_sources.Length == 0) throw new InvalidOperationException("No update metadata sources are configured.");
        var releases = new Dictionary<string, UpdateRelease>(StringComparer.Ordinal);
        var failures = new List<Exception>();
        foreach (var source in _sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var sourceReleases = await source.GetReleasesAsync(cancellationToken).ConfigureAwait(false);
                foreach (var release in sourceReleases)
                {
                    releases.TryAdd(release.TagName, release);
                }

                logger.LogInformation(
                    "Update release metadata replica {SourceType} returned {ReleaseCount} releases.",
                    source.GetType().Name,
                    sourceReleases.Count);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or InvalidDataException or JsonException or TaskCanceledException)
            {
                failures.Add(exception);
                logger.LogWarning(
                    "Update release metadata replica {SourceType} failed with {FailureType}; other official replicas remain eligible.",
                    source.GetType().Name,
                    exception.GetType().Name);
            }
        }

        if (releases.Count > 0)
        {
            return releases.Values.ToArray();
        }

        throw new HttpRequestException(
            "All official DropSpace website replicas failed to provide release metadata.",
            new AggregateException(failures));
    }
}
