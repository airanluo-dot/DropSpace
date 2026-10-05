// File-only adaptation of NovaClip Beta8 HttpRangeDownloader. No media/authentication dependencies.
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using DropSpace.Core.Downloads;

namespace DropSpace.Infrastructure.Downloads;

public sealed class HttpRangeDownloader : IDisposable
{
    public DownloadConnectionBudget Connections { get; } = new();
    public DownloadBandwidthLimiter Bandwidth { get; } = new();
    public DownloadTransferBudget Transfers { get; } = new();
    private readonly HttpClient _client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpTransferDeadline _deadline = new();
    private readonly RetryExecutor _retry = new();
    private sealed record ResumeMetadata(string Identity, string? ETag, long? Length);
    public DownloadRequestPolicy OrdinaryPolicy => new(_client, uri => uri.Scheme is "http" or "https");

    /// <summary>Discard only engine-owned staging files after the caller has awaited the transfer.</summary>
    public static void DeleteStagingFiles(string stagingPath) => DownloadStorage.Clean(stagingPath);

    public async Task DownloadAsync(Uri uri, string stagingPath, DownloadRequestPolicy policy,
        IProgress<TrackProgress>? progress, CancellationToken token, long? expectedBytes = null, string? sha256 = null,
        long? maximumBytes = null)
    {
        if (maximumBytes is <= 0 || expectedBytes is < 0 ||
            maximumBytes is { } maximum && expectedBytes > maximum)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        DownloadStorage.Safe(Path.GetDirectoryName(stagingPath)!, stagingPath);
        // Persist managed transfer intent before waiting for a task slot. The owning catalog
        // reconstructs its trust policy on resume; no serialized delegate or sensitive URL.
        await DownloadStorage.WriteAsync(stagingPath + ".request.json", new
        { SourceIdentity = DownloadStorage.Identity(uri), ExpectedBytes = expectedBytes, Sha256 = sha256, MaximumBytes = maximumBytes }, token).ConfigureAwait(false);
        using var transfer = await Transfers.AcquireAsync(token).ConfigureAwait(false);
        DownloadStorage.Safe(Path.GetDirectoryName(stagingPath)!, stagingPath);
        var parallel = new ParallelHttpFileDownloader(policy, _deadline.Timeout, Connections, Bandwidth);
        var length = await parallel.TryDownloadAsync(uri, stagingPath, Connections.Limit, new(), progress, token, expectedBytes, maximumBytes).ConfigureAwait(false);
        if (length is null)
        {
            // All range workers have settled before fallback. Untrusted old continuous partials
            // have no validator metadata and are truncated by the sequential transport.
            await _retry.ExecuteAsync(ct => DownloadOnceAsync(uri, stagingPath, policy, progress, expectedBytes, maximumBytes, ct), new(), IsTransient, token).ConfigureAwait(false);
        }
        if (sha256 is not null)
        {
            await using var input = new FileStream(stagingPath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, true);
            if (!CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(input, token).ConfigureAwait(false), Convert.FromHexString(sha256)))
            {
                await input.DisposeAsync().ConfigureAwait(false);
                DownloadStorage.Clean(stagingPath);
                throw new InvalidDataException("Download hash mismatch.");
            }
        }
    }

    private async Task<long> DownloadOnceAsync(Uri uri, string path, DownloadRequestPolicy policy,
        IProgress<TrackProgress>? progress, long? expected, long? maximumBytes, CancellationToken token)
    {
        var metadataPath = path + ".resume.json";
        ResumeMetadata? metadata = null;
        DownloadStorage.Safe(Path.GetDirectoryName(path)!, metadataPath);
        try
        {
            if (File.Exists(metadataPath) && new FileInfo(metadataPath).Length < 16384)
                metadata = JsonSerializer.Deserialize<ResumeMetadata>(await File.ReadAllTextAsync(metadataPath, token).ConfigureAwait(false));
        }
        catch (JsonException) { }
        var offset = File.Exists(path) && metadata?.Identity == DownloadStorage.Identity(uri) && metadata.ETag is not null ? new FileInfo(path).Length : 0;
        if (metadata?.Length is { } previousLength && offset >= previousLength) offset = 0;
        using var lease = await Connections.AcquireAsync(token).ConfigureAwait(false);
        HttpResponseMessage? response = null;
        try
        {
            response = await _deadline.RunAsync(ct => policy.SendAsync(uri, offset > 0 ? offset : null, null, offset > 0 ? metadata!.ETag : null, ct), token).ConfigureAwait(false);
            if (offset > 0 && (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable ||
                response.StatusCode == HttpStatusCode.PartialContent && !ValidResume(response, metadata!, offset)))
            {
                response.Dispose();
                response = await _deadline.RunAsync(ct => policy.SendAsync(uri, null, null, null, ct), token).ConfigureAwait(false);
                offset = 0;
            }
            response.EnsureSuccessStatusCode();
            if (response.StatusCode == HttpStatusCode.OK) offset = 0;
            else if (offset == 0 || response.StatusCode != HttpStatusCode.PartialContent || !ValidResume(response, metadata!, offset))
                throw new InvalidDataException("Invalid download range response.");
            if (response.Content.Headers.ContentEncoding.Any(value => !value.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Encoded download response is unsupported.");
            var total = offset > 0 ? response.Content.Headers.ContentRange!.Length : response.Content.Headers.ContentLength;
            if (expected is { } size && total is { } declared && size != declared) throw new InvalidDataException("Unexpected download size.");
            total ??= expected;
            if (maximumBytes is { } maximum && (total > maximum || offset > maximum))
                throw new InvalidDataException("Download exceeded the allowed size.");
            if (total is { } budget) DownloadStorage.CheckSpace(path, Math.Max(0, budget - offset));
            var tag = response.Headers.ETag is { IsWeak: false } strong ? strong.ToString() : null;
            // Truncate the old body before installing a new identity: a crash cannot associate
            // bytes from an old representation with a newly fetched validator.
            await using var output = new FileStream(path, offset > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 131072, true);
            await DownloadStorage.WriteAsync(metadataPath, new ResumeMetadata(DownloadStorage.Identity(uri), tag, total), token).ConfigureAwait(false);
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var buffer = new byte[131072];
            var downloaded = offset;
            var clock = Stopwatch.StartNew();
            progress?.Report(new(TrackType.File, downloaded, total, 0, 1));
            int count;
            while ((count = await _deadline.RunAsync(ct => input.ReadAsync(buffer.AsMemory(), ct).AsTask(), token).ConfigureAwait(false)) > 0)
            {
                if (total is { } limit && count > limit - downloaded) throw new InvalidDataException("Download exceeded expected length.");
                if (maximumBytes is { } bound && count > bound - downloaded) throw new InvalidDataException("Download exceeded the allowed size.");
                await Bandwidth.ConsumeAsync(count, token).ConfigureAwait(false);
                await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                downloaded += count;
                progress?.Report(new(TrackType.File, downloaded, total, (downloaded - offset) / Math.Max(.001, clock.Elapsed.TotalSeconds), 1));
            }
            await output.FlushAsync(token).ConfigureAwait(false);
            if (total is { } final && final != downloaded) throw new IOException("Download ended prematurely.");
            progress?.Report(new(TrackType.File, downloaded, total, 0, 0));
            return downloaded;
        }
        finally { response?.Dispose(); }
    }
    private static bool ValidResume(HttpResponseMessage response, ResumeMetadata metadata, long offset)
    {
        var range = response.Content.Headers.ContentRange;
        return response.Headers.ETag is { IsWeak: false } tag && tag.ToString() == metadata.ETag &&
            range?.Unit == "bytes" && range.From == offset && range.Length is { } length && range.To == length - 1 &&
            (metadata.Length is null || metadata.Length == length) &&
            (response.Content.Headers.ContentLength is null || response.Content.Headers.ContentLength == length - offset);
    }
    private static bool IsTransient(Exception exception) => exception is TimeoutException or IOException ||
        exception is HttpRequestException { StatusCode: null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError };
    public void Dispose() => _client.Dispose();
}
