using DropSpace.Core.Downloads;
using DropSpace.Infrastructure.Downloads;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Abstractions;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Lyrics;

public sealed class AiModelPackageService : IDisposable
{
    private readonly HttpClient _client;
    private readonly HttpRangeDownloader _downloads;
    private readonly bool _ownsDownloads;
    private readonly string _root;
    private readonly Func<string, AiLyricsModelDescriptor?> _resolve;
    private readonly Func<string, long> _availableFreeSpace;
    private readonly Func<AiLyricsModelDescriptor, IReadOnlyList<AiModelDeliveryManifest.Part>?> _delivery;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    public AiModelPackageService(string root, HttpRangeDownloader? downloads = null) : this(root,
        new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }, AiLyricsModelCatalog.Find, downloads: downloads) { }

    internal AiModelPackageService(string root, HttpMessageHandler handler, Func<string, AiLyricsModelDescriptor?> resolve,
        Func<string, long>? availableFreeSpace = null, HttpRangeDownloader? downloads = null,
        Func<AiLyricsModelDescriptor, IReadOnlyList<AiModelDeliveryManifest.Part>?>? delivery = null)
    {
        _root = Path.GetFullPath(root);
        _downloads = downloads ?? new();
        _ownsDownloads = downloads is null;
        _resolve = resolve;
        _delivery = delivery ?? AiModelDeliveryManifest.Find;
        _availableFreeSpace = availableFreeSpace ?? (path => new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace);
        _client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<string?> GetInstalledPathAsync(string modelId, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var model = _resolve(modelId);
        if (model is null || !Directory.Exists(_root)) return null;
        var path = Path.Combine(_root, model.Sha256 + ".gguf");
        if (!File.Exists(path)) return null;
        ReparseSafePathPolicy.ResolveExistingContainedPath(_root, path);
        return await VerifyAsync(path, model, token).ConfigureAwait(false) ? path : null;
    }

    public bool HasArtifacts(string modelId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var model = _resolve(modelId);
        if (model is null || !Directory.Exists(_root)) return false;
        return File.Exists(ReparseSafePathPolicy.ResolveOwnedFilePathForDeletion(_root, model.Sha256 + ".gguf")) ||
            OwnedArtifacts(model).Any();
    }

    public Task<DlcPackageInspection> InspectAsync(string modelId, CancellationToken token) =>
        Task.Run(() => InspectInventoryAsync(modelId, token), token);

    private async Task<DlcPackageInspection> InspectInventoryAsync(string modelId, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var model = _resolve(modelId) ?? throw new ArgumentException("Unknown model.", nameof(modelId));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await _gate.WaitAsync(stop.Token).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(_root)) return new(false, false, 0);
            var final = ReparseSafePathPolicy.ResolveOwnedFilePathForDeletion(_root, model.Sha256 + ".gguf");
            var installed = File.Exists(final) && new FileInfo(ReparseSafePathPolicy.ResolveExistingContainedPath(_root, final)).Length == model.Bytes;
            var artifacts = OwnedArtifacts(model).ToArray();
            var bytes = artifacts.Sum(path => new FileInfo(path).Length) + (File.Exists(final) ? new FileInfo(final).Length : 0);
            // Inventory is not execution trust: actual use still verifies the complete model.
            return new(installed, artifacts.Length > 0 || File.Exists(final), bytes);
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(string modelId, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var model = _resolve(modelId) ?? throw new ArgumentException("Unknown model.", nameof(modelId));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await _gate.WaitAsync(stop.Token).ConfigureAwait(false);
        try
        {
            stop.Token.ThrowIfCancellationRequested();
            if (!Directory.Exists(_root)) return;
            CleanArtifacts(model);
            foreach (var suffix in new[] { ".gguf", ".partial" })
            {
                var path = ReparseSafePathPolicy.ResolveOwnedFilePathForDeletion(_root, model.Sha256 + suffix);
                File.Delete(path);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<string> DownloadAsync(string modelId, bool consent, IProgress<double>? progress, CancellationToken token, long additionalDiskBytes = 0, IProgress<TrackProgress>? transferProgress = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!consent) throw new InvalidOperationException("Explicit model download consent is required.");
        if (additionalDiskBytes is < 0 or > 536_870_912) throw new ArgumentOutOfRangeException(nameof(additionalDiskBytes));
        var model = _resolve(modelId) ?? throw new ArgumentException("Unknown model.", nameof(modelId));
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        transferProgress?.Report(new(TrackType.File, 0, model.Bytes, Stage: DownloadStage.Checking));
        await _gate.WaitAsync(budget.Token).ConfigureAwait(false);
        try
        {
            budget.Token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            budget.CancelAfter(DownloadBudget(model));
            token = budget.Token;
            var final = ReparseSafePathPolicy.PrepareContainedFileDestination(_root, model.Sha256 + ".gguf");
            var partial = ReparseSafePathPolicy.PrepareContainedFileDestination(_root, model.Sha256 + ".partial");
            if (await VerifyAsync(final, model, token).ConfigureAwait(false)) return final;
            var offset = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            if (offset >= model.Bytes)
            {
                if (await VerifyAsync(partial, model, token).ConfigureAwait(false))
                {
                    File.Move(partial, final, true);
                    DownloadStorage.Clean(partial);
                    return final;
                }
                File.Delete(partial);
                offset = 0;
            }
            if (_delivery(model) is { } parts)
                return await DownloadMirrorAsync(model, parts, final, partial, progress, transferProgress, token, additionalDiskBytes).ConfigureAwait(false);
            // Legacy continuous partials without a strong-validator sidecar are restarted by
            // the engine. A complete legacy partial was already accepted only after SHA256 above.
            var retained = DownloadStorage.Artifacts(partial).Sum(path => new FileInfo(path).Length);
            if (_availableFreeSpace(_root) < checked(Math.Max(model.Bytes, model.Bytes * 2 - retained) + additionalDiskBytes + 64L * 1024 * 1024))
                throw new IOException("Insufficient space for model fragments and assembly.");
            var policy = new DownloadRequestPolicy(_client, uri => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
                (uri.Host == "huggingface.co" || uri.Host.EndsWith(".huggingface.co", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".hf.co", StringComparison.OrdinalIgnoreCase)));
            await _downloads.DownloadAsync(model.DownloadUri, partial, policy,
                new DownloadManager.InlineProgress(value => { progress?.Report((double)value.DownloadedBytes / model.Bytes); transferProgress?.Report(value); }),
                token, model.Bytes, model.Sha256).ConfigureAwait(false);
            ReparseSafePathPolicy.RevalidatePreparedDestination(_root, final);
            File.Move(partial, final, true);
            DownloadStorage.Clean(partial);
            return final;
        }
        finally { _gate.Release(); }
    }

    private string PartPath(AiLyricsModelDescriptor model, AiModelDeliveryManifest.Part part) =>
        Path.Combine(_root, $"{model.Sha256}.{AiModelDeliveryManifest.Identity[..16]}.part{part.Order:D3}");

    private IEnumerable<string> OwnedArtifacts(AiLyricsModelDescriptor model)
    {
        foreach (var path in DownloadStorage.Artifacts(Path.Combine(_root, model.Sha256 + ".partial"))) yield return path;
        foreach (var part in _delivery(model) ?? [])
            foreach (var path in DownloadStorage.Artifacts(PartPath(model, part))) yield return path;
    }

    private void CleanArtifacts(AiLyricsModelDescriptor model)
    {
        DownloadStorage.Clean(Path.Combine(_root, model.Sha256 + ".partial"));
        foreach (var part in _delivery(model) ?? []) DownloadStorage.Clean(PartPath(model, part));
    }

    private async Task<string> DownloadMirrorAsync(AiLyricsModelDescriptor model, IReadOnlyList<AiModelDeliveryManifest.Part> parts,
        string final, string assembly, IProgress<double>? ratio, IProgress<TrackProgress>? progress, CancellationToken token, long extra)
    {
        var run = Guid.NewGuid();
        long completed = 0;
        void Report(TrackProgress value)
        {
            progress?.Report(value with { RunId = run });
            ratio?.Report((double)value.DownloadedBytes / model.Bytes);
        }
        var policy = new DownloadRequestPolicy(_client, AiModelDeliveryManifest.Trusted,
            request => request.Headers.UserAgent.ParseAdd("DropSpace-model-download/1"));
        // This legacy assembly is neither a mirror part nor a range cache. It can only be
        // reused as a whole verified model (checked above); reclaim it before space checks.
        DownloadStorage.Clean(assembly);
        var verified = new HashSet<int>();
        foreach (var part in parts)
        {
            var path = DownloadStorage.Safe(_root, PartPath(model, part));
            Report(new(TrackType.File, completed, model.Bytes, Stage: DownloadStage.Checking));
            if (await VerifyFileAsync(path, part.Bytes, part.Sha256, token).ConfigureAwait(false))
            { verified.Add(part.Order); completed += part.Bytes; }
            else if (File.Exists(path) && new FileInfo(path).Length >= part.Bytes)
                DownloadStorage.Clean(path); // Corrupt complete part only; other verified parts survive.
        }
        // All owned part data is either reused or removed before replacement; credit only
        // those files, never arbitrary files on the volume. Metadata is not model payload.
        var retained = parts.SelectMany(part => DownloadStorage.Artifacts(PartPath(model, part)))
            .Where(path => !path.EndsWith(".json", StringComparison.Ordinal) && !path.EndsWith(".tmp", StringComparison.Ordinal))
            .Sum(path => new FileInfo(path).Length);
        var needed = Math.Max(0, checked(model.Bytes * 2 - retained));
        if (_availableFreeSpace(_root) < checked(needed + extra + 64L * 1024 * 1024))
            throw new IOException("Insufficient space for model parts and assembly.");
        foreach (var part in parts)
        {
            var path = PartPath(model, part);
            if (!verified.Contains(part.Order))
            {
                var baseBytes = completed;
                await _downloads.DownloadAsync(part.Url, path, policy, new DownloadManager.InlineProgress(value =>
                    Report(value with { DownloadedBytes = baseBytes + value.DownloadedBytes, TotalBytes = model.Bytes,
                        Stage = value.Stage == DownloadStage.Completed ? DownloadStage.Verifying : value.Stage })),
                    token, part.Bytes, part.Sha256).ConfigureAwait(false);
                // The shared transfer has checked this part's exact length and SHA256.
                completed += part.Bytes;
            }
            // Retain the verified raw part but discard its redundant HTTP fragments.
            foreach (var artifact in DownloadStorage.Artifacts(path).Where(candidate => candidate != path).ToArray()) File.Delete(artifact);
        }
        Report(new(TrackType.File, completed, model.Bytes, Stage: DownloadStage.Merging));
        if (parts.Count == 1)
        {
            // Same full SHA as the model: the engine/resume check already verified it.
            File.Move(PartPath(model, parts[0]), assembly, true);
        }
        else
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var output = new FileStream(assembly, FileMode.Create, FileAccess.Write, FileShare.None, 131072, true))
            {
                var buffer = new byte[131072];
                foreach (var part in parts)
                {
                    await using var input = ReparseSafeFileOpen.OpenRead(PartPath(model, part));
                    int count;
                    while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                    { hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false); }
                }
                await output.FlushAsync(token).ConfigureAwait(false);
                Report(new(TrackType.File, completed, model.Bytes, Stage: DownloadStage.Verifying));
                if (output.Length != model.Bytes || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(model.Sha256)))
                    throw new InvalidDataException("Assembled model SHA256 mismatch.");
            }
        }
        token.ThrowIfCancellationRequested();
        Report(new(TrackType.File, completed, model.Bytes, Stage: DownloadStage.Publishing));
        ReparseSafePathPolicy.RevalidatePreparedDestination(_root, final);
        File.Move(assembly, final, true);
        // Publication succeeded. Cleanup is independent and retried by owned inventory/delete.
        try { CleanArtifacts(model); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { System.Diagnostics.Trace.TraceWarning("Model cache cleanup deferred: {0}", error.GetType().Name); }
        Report(new(TrackType.File, completed, model.Bytes, Stage: DownloadStage.Completed));
        return final;
    }

    private static async Task<bool> VerifyFileAsync(string path, long bytes, string sha256, CancellationToken token)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != bytes) return false;
        await using var input = ReparseSafeFileOpen.OpenRead(path);
        return CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(input, token).ConfigureAwait(false), Convert.FromHexString(sha256));
    }

    // The optional 7B payload is over four times larger. Keep its total transfer and
    // hash-verification budget bounded without changing the 1.8B or per-read budgets.
    internal static TimeSpan DownloadBudget(AiLyricsModelDescriptor model) =>
        model.Id == AiLyricsModelCatalog.ExperimentalLargePlain.Id ? TimeSpan.FromHours(2) : TimeSpan.FromMinutes(30);

    internal static long ValidateDownloadResponse(HttpResponseMessage response, long offset, long expectedBytes)
    {
        response.EnsureSuccessStatusCode();
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange;
            if (range is null || range.Unit != "bytes" || range.From != offset || range.To != expectedBytes - 1 || range.Length != expectedBytes)
                throw new InvalidDataException("Invalid model download range.");
        }
        else if (response.StatusCode == HttpStatusCode.OK) offset = 0;
        else throw new InvalidDataException("Unexpected model download status.");
        if (response.Content.Headers.ContentLength is long length && length != expectedBytes - offset)
            throw new InvalidDataException("Model response size does not match the catalog.");
        return offset;
    }

    private static async Task<bool> VerifyAsync(string path, AiLyricsModelDescriptor model, CancellationToken token)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != model.Bytes) return false;
        await using var input = ReparseSafeFileOpen.OpenRead(path);
        var hash = await SHA256.HashDataAsync(input, token).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(model.Sha256));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _client.Dispose();
        if (_ownsDownloads) _downloads.Dispose();
    }
}
