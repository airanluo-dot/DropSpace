using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using DropSpace.Core.Lyrics;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Lyrics;

public sealed class AiModelPackageService : IDisposable
{
    private readonly HttpClient _client;
    private readonly string _root;
    private readonly Func<string, AiLyricsModelDescriptor?> _resolve;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    public AiModelPackageService(string root) : this(root,
        new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }, AiLyricsModelCatalog.Find) { }

    internal AiModelPackageService(string root, HttpMessageHandler handler, Func<string, AiLyricsModelDescriptor?> resolve)
    {
        _root = Path.GetFullPath(root);
        _resolve = resolve;
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

    public async Task<string> DownloadAsync(string modelId, bool consent, IProgress<double>? progress, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!consent) throw new InvalidOperationException("Explicit model download consent is required.");
        var model = _resolve(modelId) ?? throw new ArgumentException("Unknown model.", nameof(modelId));
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await _gate.WaitAsync(budget.Token).ConfigureAwait(false);
        try
        {
            budget.Token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            budget.CancelAfter(TimeSpan.FromMinutes(30));
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
                    return final;
                }
                File.Delete(partial);
                offset = 0;
            }
            var drive = new DriveInfo(Path.GetPathRoot(_root)!);
            if (drive.AvailableFreeSpace < model.Bytes - offset + 64L * 1024 * 1024)
                throw new IOException("Insufficient free space for the model.");
            var download = await RequestWithRangeFallbackAsync(model.DownloadUri, offset, token).ConfigureAwait(false);
            using var response = download.Response;
            offset = download.Offset;
            response.EnsureSuccessStatusCode();
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var range = response.Content.Headers.ContentRange;
                if (range is null || range.Unit != "bytes" || range.From != offset || range.To != model.Bytes - 1 || range.Length != model.Bytes)
                    throw new InvalidDataException("Invalid model download range.");
            }
            else if (response.StatusCode == HttpStatusCode.OK) offset = 0;
            else throw new InvalidDataException("Unexpected model download status.");
            if (response.Content.Headers.ContentLength is long length && length != model.Bytes - offset)
                throw new InvalidDataException("Model response size does not match the catalog.");
            ReparseSafePathPolicy.RevalidatePreparedDestination(_root, partial);
            await using (var output = new FileStream(partial, offset > 0 ? FileMode.Open : FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
            {
                output.SetLength(offset);
                output.Position = offset;
                await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                var buffer = new byte[65536];
                while (true)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
                    idle.CancelAfter(TimeSpan.FromSeconds(45));
                    var count = await input.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    if (offset + count > model.Bytes) throw new InvalidDataException("Model exceeded the catalog size.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    offset += count;
                    progress?.Report((double)offset / model.Bytes);
                }
                await output.FlushAsync(token).ConfigureAwait(false);
            }
            if (offset != model.Bytes) throw new EndOfStreamException("Incomplete model download; retry to resume.");
            if (!await VerifyAsync(partial, model, token).ConfigureAwait(false))
            {
                File.Delete(partial);
                throw new InvalidDataException("Model SHA256 verification failed.");
            }
            ReparseSafePathPolicy.RevalidatePreparedDestination(_root, final);
            File.Move(partial, final, true);
            return final;
        }
        finally { _gate.Release(); }
    }

    private async Task<(HttpResponseMessage Response, long Offset)> RequestWithRangeFallbackAsync(Uri uri, long offset, CancellationToken token)
    {
        var response = await RequestAsync(uri, offset, token).ConfigureAwait(false);
        if (offset > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            response.Dispose();
            return (await RequestAsync(uri, 0, token).ConfigureAwait(false), 0);
        }
        return (response, offset);
    }

    private async Task<HttpResponseMessage> RequestAsync(Uri uri, long offset, CancellationToken token)
    {
        for (var redirect = 0; redirect < 6; redirect++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
                !(uri.Host == "huggingface.co" || uri.Host.EndsWith(".huggingface.co", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".hf.co", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Untrusted model download destination.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (offset > 0) request.Headers.Range = new RangeHeaderValue(offset, null);
            using var headersDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            headersDeadline.CancelAfter(TimeSpan.FromSeconds(45));
            var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headersDeadline.Token).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null) throw new InvalidDataException("Missing model redirect.");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            return response;
        }
        throw new InvalidDataException("Too many model redirects.");
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
    }
}
