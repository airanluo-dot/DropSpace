// Ported from NovaClip Beta8, b798c571dad1cae03c16129e4b4c43b85b428d7a.
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using DropSpace.Core.Downloads;

namespace DropSpace.Infrastructure.Downloads;

/// <summary>Independent HTTP range transport for a private, per-task staging path.</summary>
public sealed class ParallelHttpFileDownloader(DownloadRequestPolicy policy, TimeSpan? idleTimeout = null, DownloadConnectionBudget? connectionBudget = null, DownloadBandwidthLimiter? bandwidthLimiter = null)
{
    private const int BufferSize = 128 * 1024;
    private readonly RetryExecutor _retry = new();
    private readonly DownloadBandwidthLimiter _bandwidth = bandwidthLimiter ?? new();
    private readonly DownloadConnectionBudget _connections = connectionBudget ?? new();
    private readonly HttpTransferDeadline _deadline = new(idleTimeout);
    private sealed record Identity(string Url, string ETag, long Length, int Parts);
    private sealed class RangeRejectedException : Exception { }

    // Null means the server cannot safely support parallel ranges; caller uses sequential transport.
    public async Task<long?> TryDownloadAsync(Uri uri, string stagingPath, int connections,
        RetryPolicy retryPolicy, IProgress<TrackProgress>? progress, CancellationToken cancellationToken, long? expectedBytes = null,
        long? maximumBytes = null)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (uri.Scheme is not ("http" or "https")) throw new ArgumentException("HTTP(S) is required.", nameof(uri));
        if (!Path.IsPathRooted(stagingPath)) throw new ArgumentException("An absolute staging path is required.", nameof(stagingPath));
        using var probeLease = await _connections.AcquireAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report(new TrackProgress(TrackType.File, 0, expectedBytes, 0, 1));
        using var head = await _retry.ExecuteAsync(token => _deadline.RunAsync(ct => policy.SendAsync(uri, 0, 0, null, ct), token), retryPolicy, IsTransient, cancellationToken).ConfigureAwait(false);
        if (head.StatusCode != HttpStatusCode.RequestedRangeNotSatisfiable) head.EnsureSuccessStatusCode();
        var range = head.Content.Headers.ContentRange;
        var tag = head.Headers.ETag;
        if (head.StatusCode != HttpStatusCode.PartialContent || range?.Unit != "bytes" ||
            range.From != 0 || range.To != 0 || range.Length is not > 0 ||
            tag is null || tag.IsWeak || HasEncoding(head)) return null;
        if (head.Content.Headers.ContentLength is { } probeLength && probeLength != 1) return null;
        await using (var probeBody = await head.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            var probeBytes = new byte[2];
            var read = await _deadline.RunAsync(ct => probeBody.ReadAsync(probeBytes.AsMemory(0, 1), ct).AsTask(), cancellationToken).ConfigureAwait(false);
            if (read != 1 || await _deadline.RunAsync(ct => probeBody.ReadAsync(probeBytes.AsMemory(1, 1), ct).AsTask(), cancellationToken).ConfigureAwait(false) != 0) return null;
            await _bandwidth.ConsumeAsync(1, cancellationToken).ConfigureAwait(false);
        }
        var length = range.Length.Value;
        if (maximumBytes is { } maximum && length > maximum) throw new InvalidDataException("Download exceeded the allowed size.");
        if (expectedBytes is { } expected && length != expected) throw new InvalidDataException("Download differs from trusted size.");
        head.Dispose();
        probeLease.Dispose();
        var cache = stagingPath + ".ranges";
        var metadataPath = Path.Combine(cache, "identity.json");
        DownloadStorage.Safe(Path.GetDirectoryName(stagingPath)!, metadataPath);
        Identity? previous = null;
        try
        {
            if (File.Exists(metadataPath) && new FileInfo(metadataPath).Length < 16384)
                previous = JsonSerializer.Deserialize<Identity>(await File.ReadAllTextAsync(metadataPath, cancellationToken).ConfigureAwait(false));
        }
        catch (JsonException) { }
        var sameIdentity = previous is { Parts: >= 1 and <= 256 } && previous.Url == DownloadStorage.Identity(uri) &&
            previous.ETag == tag.ToString() && previous.Length == length;
        // Preserve the persisted partition even when the live connection ceiling changes.
        var ranges = HttpByteRangePlanner.Create(length, sameIdentity ? previous!.Parts : connections);
        if (ranges.Count <= 1) return null;
        var identity = new Identity(DownloadStorage.Identity(uri), tag.ToString(), length, ranges.Count);
        if (!sameIdentity)
        {
            // Only known part files in this task's private staging directory are touched.
            for (var index = 0; index < DownloadConnectionBudget.Maximum; index++) File.Delete(PartPath(cache, index));
            await DownloadStorage.WriteAsync(metadataPath, identity, cancellationToken).ConfigureAwait(false);
        }
        var completed = new long[ranges.Count];
        var gate = new object();
        for (var index = 0; index < ranges.Count; index++)
        {
            var file = PartPath(cache, index);
            if (!File.Exists(file)) continue;
            var size = new FileInfo(file).Length;
            if (size > ranges[index].Length) File.Delete(file);
            else completed[index] = size;
        }
        var resumedBytes = completed.Sum();
        // Remaining fragments and the full assembly may coexist. Existing assembly bytes
        // can be reclaimed, but do not assume unrelated files can be deleted for space.
        DownloadStorage.CheckSpace(stagingPath, checked(length * 2 - resumedBytes));
        var transferClock = System.Diagnostics.Stopwatch.StartNew();
        var active = 0;
        progress?.Report(new TrackProgress(TrackType.File, resumedBytes, length));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var rejected = 0;
        long CompletedBytes() { lock (gate) return completed.Sum(); }
        try
        {
            await AdaptiveDownloadScheduler.RunAsync(ranges.Count, connections,
                index => DownloadPartWithRetryAsync(ranges[index], index), CompletedBytes, stop.Token).ConfigureAwait(false);
        }
        catch when (Volatile.Read(ref rejected) != 0 && !cancellationToken.IsCancellationRequested)
        {
            File.Delete(metadataPath);
            return null;
        }
        cancellationToken.ThrowIfCancellationRequested();
        // A continuous sidecar must never describe a partially assembled range output.
        File.Delete(DownloadStorage.Safe(Path.GetDirectoryName(stagingPath)!, stagingPath + ".resume.json"));
        await using (var output = new FileStream(stagingPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
        {
            for (var index = 0; index < ranges.Count; index++)
            {
                await using var input = new FileStream(PartPath(cache, index), FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous);
                if (input.Length != ranges[index].Length) throw new InvalidDataException("Incomplete file part.");
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            if (output.Length != length) throw new InvalidDataException("File assembly length mismatch.");
        }
        return length;

        async Task DownloadPartWithRetryAsync(HttpByteRangePlanner.ByteRange part, int index)
        {
            try
            {
                await _retry.ExecuteAsync(async token =>
                {
                    var file = PartPath(cache, index);
                    var existing = File.Exists(file) ? new FileInfo(file).Length : 0;
                    if (existing == part.Length) return existing;
                    using var lease = await _connections.AcquireAsync(token).ConfigureAwait(false);
                    Interlocked.Increment(ref active);
                    try
                    {
                    using var response = await _deadline.RunAsync(readToken => policy.SendAsync(uri, part.From + existing, part.To, identity.ETag, readToken), token).ConfigureAwait(false);
                    if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.RequestedRangeNotSatisfiable)
                        throw new RangeRejectedException();
                    response.EnsureSuccessStatusCode();
                    var receivedRange = response.Content.Headers.ContentRange;
                    if (response.StatusCode != HttpStatusCode.PartialContent || HasEncoding(response) ||
                        receivedRange?.Unit != "bytes" || receivedRange.From != part.From + existing ||
                        receivedRange.To != part.To || receivedRange.Length != length ||
                        response.Headers.ETag?.ToString() != identity.ETag ||
                        response.Content.Headers.ContentLength is { } declared && declared != part.Length - existing)
                        throw new RangeRejectedException();
                    await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    await using var output = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read, BufferSize, FileOptions.Asynchronous);
                    var buffer = new byte[BufferSize];
                    int count;
                    while ((count = await _deadline.RunAsync(readToken => input.ReadAsync(buffer.AsMemory(), readToken).AsTask(), token).ConfigureAwait(false)) > 0)
                    {
                        if (count > part.Length - existing) throw new RangeRejectedException();
                        await _bandwidth.ConsumeAsync(count, token).ConfigureAwait(false);
                        await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                        existing += count;
                        lock (gate)
                        {
                            completed[index] = existing;
                            var total = completed.Sum();
                            var speed = transferClock.Elapsed.TotalSeconds > 0
                                ? (total - resumedBytes) / transferClock.Elapsed.TotalSeconds : 0;
                            progress?.Report(new TrackProgress(TrackType.File, total, length, speed, Volatile.Read(ref active)));
                        }
                    }
                    await output.FlushAsync(token).ConfigureAwait(false);
                    if (existing != part.Length) throw new IOException("File part ended prematurely.");
                    return existing;
                    }
                    finally
                    {
                        Interlocked.Decrement(ref active);
                        lock (gate)
                        {
                            var total = completed.Sum();
                            progress?.Report(new TrackProgress(TrackType.File, total, length,
                                (total - resumedBytes) / Math.Max(.001, transferClock.Elapsed.TotalSeconds), Volatile.Read(ref active)));
                        }
                    }
                }, retryPolicy, IsTransient, stop.Token).ConfigureAwait(false);
            }
            catch (RangeRejectedException)
            {
                Interlocked.Exchange(ref rejected, 1);
                await stop.CancelAsync().ConfigureAwait(false);
                throw;
            }
            catch
            {
                await stop.CancelAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    private static string PartPath(string directory, int index) => DownloadStorage.Safe(directory, Path.Combine(directory, $"part-{index:D2}.bin"));
    private static bool HasEncoding(HttpResponseMessage response) =>
        response.Content.Headers.ContentEncoding.Any(value => !value.Equals("identity", StringComparison.OrdinalIgnoreCase));
    private static bool IsTransient(Exception exception) => exception is IOException or TimeoutException ||
        exception is HttpRequestException { StatusCode: null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests } ||
        exception is HttpRequestException { StatusCode: >= HttpStatusCode.InternalServerError };
}
