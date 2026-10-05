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
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    public AiModelPackageService(string root, HttpRangeDownloader? downloads = null) : this(root,
        new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }, AiLyricsModelCatalog.Find, downloads: downloads) { }

    internal AiModelPackageService(string root, HttpMessageHandler handler, Func<string, AiLyricsModelDescriptor?> resolve,
        Func<string, long>? availableFreeSpace = null, HttpRangeDownloader? downloads = null)
    {
        _root = Path.GetFullPath(root);
        _downloads = downloads ?? new();
        _ownsDownloads = downloads is null;
        _resolve = resolve;
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
            DownloadStorage.Artifacts(Path.Combine(_root, model.Sha256 + ".partial")).Any();
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
            long bytes = 0;
            var hasArtifacts = false;
            var installed = false;
            foreach (var suffix in new[] { ".gguf", ".partial" })
            {
                stop.Token.ThrowIfCancellationRequested();
                var path = ReparseSafePathPolicy.ResolveOwnedFilePathForDeletion(_root, model.Sha256 + suffix);
                if (!File.Exists(path)) continue;
                ReparseSafePathPolicy.ResolveExistingContainedPath(_root, path);
                hasArtifacts = true;
                var length = new FileInfo(path).Length;
                bytes += length;
                // Final files are published only after SHA256 succeeds. Inventory is not a
                // trust cache: GetInstalledPathAsync still rehashes before actual model use.
                if (suffix == ".gguf") installed = length == model.Bytes;
            }
            var fragments = DownloadStorage.Artifacts(Path.Combine(_root, model.Sha256 + ".partial"))
                .Where(path => path != Path.Combine(_root, model.Sha256 + ".partial")).ToArray();
            bytes += fragments.Sum(path => new FileInfo(path).Length);
            return new(installed, hasArtifacts || fragments.Length > 0, bytes);
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
            DownloadStorage.Clean(Path.Combine(_root, model.Sha256 + ".partial"));
            foreach (var suffix in new[] { ".gguf", ".partial" })
            {
                var path = ReparseSafePathPolicy.ResolveOwnedFilePathForDeletion(_root, model.Sha256 + suffix);
                File.Delete(path);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<string> DownloadAsync(string modelId, bool consent, IProgress<double>? progress, CancellationToken token, long additionalDiskBytes = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!consent) throw new InvalidOperationException("Explicit model download consent is required.");
        if (additionalDiskBytes is < 0 or > 536_870_912) throw new ArgumentOutOfRangeException(nameof(additionalDiskBytes));
        var model = _resolve(modelId) ?? throw new ArgumentException("Unknown model.", nameof(modelId));
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
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
            // Legacy continuous partials without a strong-validator sidecar are restarted by
            // the engine. A complete legacy partial was already accepted only after SHA256 above.
            var retained = DownloadStorage.Artifacts(partial).Sum(path => new FileInfo(path).Length);
            if (_availableFreeSpace(_root) < checked(Math.Max(model.Bytes, model.Bytes * 2 - retained) + additionalDiskBytes + 64L * 1024 * 1024))
                throw new IOException("Insufficient space for model fragments and assembly.");
            var policy = new DownloadRequestPolicy(_client, uri => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort &&
                (uri.Host == "huggingface.co" || uri.Host.EndsWith(".huggingface.co", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".hf.co", StringComparison.OrdinalIgnoreCase)));
            await _downloads.DownloadAsync(model.DownloadUri, partial, policy,
                new DownloadManager.InlineProgress(value => progress?.Report((double)value.DownloadedBytes / model.Bytes)),
                token, model.Bytes, model.Sha256).ConfigureAwait(false);
            if (!await VerifyAsync(partial, model, token).ConfigureAwait(false))
            {
                File.Delete(partial);
                throw new InvalidDataException("Model SHA256 verification failed.");
            }
            ReparseSafePathPolicy.RevalidatePreparedDestination(_root, final);
            File.Move(partial, final, true);
            DownloadStorage.Clean(partial);
            return final;
        }
        finally { _gate.Release(); }
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
