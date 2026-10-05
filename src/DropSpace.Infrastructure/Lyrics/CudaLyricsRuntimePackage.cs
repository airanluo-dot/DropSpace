using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Lyrics;

/// <summary>Optional trusted CUDA component, validated before automatic NVIDIA execution.</summary>
public sealed class CudaLyricsRuntimePackage
{
    public const string RuntimeId = "llama-cpp-v0.5.0-cuda13-win-x64-v1";
    public const string ResourcePrefix = "DropSpace.CudaLyricsRuntime.";
    public const string ManifestResourceName = ResourcePrefix + "cuda-runtime-manifest.json";
    public const string DownloadResourceName = ResourcePrefix + "cuda-runtime-download.json";
    private string? _appTag;
    private string? _appCommit;
    public const string ExecutableName = "plain-lyrics-worker-cuda.exe";
    private static readonly string[] Names = [ExecutableName, "cublas64_13.dll", "cublasLt64_13.dll"];
    private static readonly SemaphoreSlim ExtractionGate = new(1, 1);
    private readonly Func<string, Stream?> _openResource;
    private readonly string _root;

    public CudaLyricsRuntimePackage(Assembly assembly, string cacheRoot)
        : this((assembly ?? throw new ArgumentNullException(nameof(assembly))).GetManifestResourceStream, cacheRoot)
    {
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+');
        _appTag = version is { Length: > 0 } ? "v" + version[0] : null;
        _appCommit = version is { Length: 2 } ? version[1] : null;
    }

    internal CudaLyricsRuntimePackage(Func<string, Stream?> openResource, string cacheRoot)
    {
        ArgumentNullException.ThrowIfNull(openResource);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        _openResource = openResource;
        _root = Path.GetFullPath(cacheRoot);
    }

    // Includes CUDA identity and its cuBLAS dependencies; never aliases shipping Vulkan/CPU caches.
    public string GetManifestCacheIdentity()
    {
        using var manifest = ReadManifest();
        return Convert.ToHexStringLower(SHA256.HashData(manifest));
    }

    private MemoryStream ReadManifest()
    {
        using var resource = _openResource(ManifestResourceName)
            ?? throw new FileNotFoundException("This build does not embed the CUDA experiment.");
        if (resource.Length is <= 0 or > 16_384) throw new InvalidDataException("Invalid CUDA manifest size.");
        var bytes = new byte[(int)resource.Length];
        resource.ReadExactly(bytes);
        return new MemoryStream(bytes, writable: false);
    }

    private JsonDocument ReadDownloadDescriptor()
    {
        using var resource = _openResource(DownloadResourceName)
            ?? throw new FileNotFoundException("This build has no trusted CUDA download descriptor.");
        if (resource.Length is <= 0 or > 16_384) throw new InvalidDataException("Invalid CUDA descriptor size.");
        var bytes = new byte[(int)resource.Length]; resource.ReadExactly(bytes);
        var document = JsonDocument.Parse(bytes);
        try
        {
            var root = document.RootElement;
            using var stream = ReadManifest();
            var hash = Convert.ToHexStringLower(SHA256.HashData(stream)); stream.Position = 0;
            using var manifest = JsonDocument.Parse(stream);
            var component = manifest.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 1 || root.GetProperty("repository").GetString() != "airanluo-dot/DropSpace" ||
                root.GetProperty("runtimeId").GetString() != RuntimeId || root.GetProperty("backend").GetString() != "cuda" ||
                root.GetProperty("platform").GetString() != "win-x64" || root.GetProperty("protocol").GetInt32() != 1 ||
                root.GetProperty("profile").GetString() != PersistentPlainLyricsRunner.ResidentProfileId ||
                root.GetProperty("engineSourceCommit").GetString() != AiLyricsRuntimePackage.SourceCommit ||
                root.GetProperty("workerSourceSha256").GetString() != component.GetProperty("workerSourceSha256").GetString() ||
                root.GetProperty("manifest").GetProperty("name").GetString() != "cuda-runtime-manifest.json" ||
                root.GetProperty("manifest").GetProperty("sha256").GetString() != hash ||
                root.GetProperty("manifest").GetProperty("bytes").GetInt64() != stream.Length ||
                root.GetProperty("appRelease").GetProperty("tag").GetString() != _appTag ||
                _appCommit is not { Length: 40 } || root.GetProperty("appRelease").GetProperty("sourceCommit").GetString() != _appCommit)
                throw new InvalidDataException("CUDA descriptor is not bound to this App and engine.");
            var outer = root.GetProperty("files").EnumerateArray().ToArray();
            var inner = component.GetProperty("files").EnumerateArray().ToArray();
            if (outer.Length != Names.Length || inner.Length != Names.Length) throw new InvalidDataException("CUDA descriptor component count changed.");
            for (var i = 0; i < Names.Length; i++)
                if (outer[i].GetProperty("name").GetString() != Names[i] ||
                    outer[i].GetProperty("sha256").GetString() != inner[i].GetProperty("sha256").GetString() ||
                    outer[i].GetProperty("bytes").GetInt64() != inner[i].GetProperty("bytes").GetInt64())
                    throw new InvalidDataException("CUDA descriptor and component manifest differ.");
            var notices = root.GetProperty("notices").EnumerateArray().ToArray();
            if (notices.Length != 2) throw new InvalidDataException("CUDA notices are missing.");
            for (var i = 0; i < 2; i++)
                if (notices[i].GetProperty("name").GetString() != (i == 0 ? "LICENSE-llama.cpp" : "LICENSE-CUDA.txt") ||
                    !IsHash(notices[i].GetProperty("sha256").GetString()) || notices[i].GetProperty("bytes").GetInt64() is <= 0 or > 16_777_216)
                    throw new InvalidDataException("Invalid CUDA notice identity.");
            return document;
        }
        catch { document.Dispose(); throw; }
    }

    public async Task<string> EnsureWorkerAsync(CancellationToken token)
    {
        await ExtractionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var manifest = ReadManifest();
            var identity = Convert.ToHexStringLower(SHA256.HashData(manifest));
            manifest.Position = 0;
            using var document = await JsonDocument.ParseAsync(manifest, cancellationToken: token).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 1 ||
                root.GetProperty("runtimeId").GetString() != RuntimeId ||
                root.GetProperty("sourceCommit").GetString() != AiLyricsRuntimePackage.SourceCommit ||
                root.GetProperty("sourceRepository").GetString() != "https://github.com/ggml-org/llama.cpp" ||
                root.GetProperty("backend").GetString() != "cuda" || root.GetProperty("protocol").GetInt32() != 1 ||
                root.GetProperty("profile").GetString() != PersistentPlainLyricsRunner.ResidentProfileId ||
                !IsHash(root.GetProperty("workerSourceSha256").GetString()))
                throw new InvalidDataException("Unrecognized CUDA component provenance.");
            var files = root.GetProperty("files").EnumerateArray().ToArray();
            if (files.Length != Names.Length) throw new InvalidDataException("Unexpected CUDA component count.");
            // Validate the whole variant before materializing anything, including its DLLs.
            long total = 0;
            for (var i = 0; i < files.Length; i++)
            {
                var file = files[i];
                var bytes = file.GetProperty("bytes").GetInt64();
                if (file.GetProperty("name").GetString() != Names[i] ||
                    !IsHash(file.GetProperty("sha256").GetString()) || (bytes <= 0 || bytes > (Names[i] == "cublasLt64_13.dll" ? 805_306_368L : 536_870_912L)))
                    throw new InvalidDataException("Invalid CUDA component integrity metadata.");
                total += bytes;
            }
            if (total > 1_073_741_824) throw new InvalidDataException("CUDA payload exceeds budget.");
            var directory = Path.Combine(RuntimeId, identity);
            for (var i = 0; i < files.Length; i++)
            {
                var name = Names[i];
                var hash = files[i].GetProperty("sha256").GetString()!;
                var bytes = files[i].GetProperty("bytes").GetInt64();
                var path = ReparseSafePathPolicy.PrepareContainedFileDestination(_root, Path.Combine(directory, name));
                if (await VerifyAsync(path, hash, bytes, token).ConfigureAwait(false)) continue;
                var partial = ReparseSafePathPolicy.PrepareContainedFileDestination(_root,
                    Path.Combine(directory, Guid.NewGuid().ToString("N") + ".partial"));
                try
                {
                    using var embedded = _openResource(ResourcePrefix + name)
                        ?? throw new FileNotFoundException("Missing embedded CUDA component: " + name);
                    if (embedded.Length != bytes) throw new InvalidDataException("CUDA component size mismatch.");
                    await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                        await embedded.CopyToAsync(output, token).ConfigureAwait(false);
                    if (!await VerifyAsync(partial, hash, bytes, token).ConfigureAwait(false))
                        throw new InvalidDataException("CUDA component SHA256 mismatch.");
                    ReparseSafePathPolicy.RevalidatePreparedDestination(_root, path);
                    File.Move(partial, path, true);
                }
                finally { if (File.Exists(partial)) File.Delete(partial); }
            }
            // No executable is returned until every dependency has been verified.
            token.ThrowIfCancellationRequested();
            return ReparseSafePathPolicy.ResolveExistingContainedPath(_root, Path.Combine(_root, directory, ExecutableName));
        }
        finally { ExtractionGate.Release(); }
    }

    public bool HasInstalledFiles
    {
        get
        {
            try
            {
                var directory = Path.Combine(_root, RuntimeId, GetManifestCacheIdentity());
                return Names.All(name => File.Exists(Path.Combine(directory, name)));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    public long InstalledBytes
    {
        get { using var stream = ReadManifest(); using var json = JsonDocument.Parse(stream); return json.RootElement.GetProperty("files").EnumerateArray().Sum(file => file.GetProperty("bytes").GetInt64()); }
    }

    private IEnumerable<string> OwnedArtifactPaths()
    {
        var runtimeRoot = Path.Combine(_root, RuntimeId);
        if (!Directory.Exists(runtimeRoot)) yield break;
        runtimeRoot = ReparseSafePathPolicy.ResolveExistingContainedPath(_root, runtimeRoot);
        foreach (var directory in Directory.EnumerateDirectories(runtimeRoot))
        {
            if (!IsHash(Path.GetFileName(directory))) continue;
            var safe = ReparseSafePathPolicy.ResolveExistingContainedPath(_root, directory);
            foreach (var file in Directory.EnumerateFiles(safe))
            {
                var name = Path.GetFileName(file);
                var partial = name.EndsWith(".partial", StringComparison.Ordinal) &&
                    Guid.TryParseExact(name.Split('.')[0], "N", out _) &&
                    (name.EndsWith(".zip.partial", StringComparison.Ordinal) ||
                     name.EndsWith(".notice.partial", StringComparison.Ordinal) || name.Length == 40);
                if (Names.Contains(name, StringComparer.Ordinal) || name is "LICENSE-CUDA.txt" or "LICENSE-llama.cpp" || partial)
                    yield return ReparseSafePathPolicy.ResolveExistingContainedPath(_root, file);
            }
        }
    }

    public long GetOwnedArtifactBytes() => OwnedArtifactPaths().Sum(path => new FileInfo(path).Length);

    public async Task RemoveAsync(CancellationToken token)
    {
        await ExtractionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            foreach (var file in OwnedArtifactPaths().ToArray())
            {
                token.ThrowIfCancellationRequested();
                File.Delete(ReparseSafePathPolicy.ResolveOwnedFilePathForDeletion(_root, Path.GetRelativePath(_root, file)));
            }
        }
        finally { ExtractionGate.Release(); }
    }

    public long DownloadBytes
    {
        get { using var json = ReadDownloadDescriptor(); return json.RootElement.GetProperty("download").GetProperty("bytes").GetInt64(); }
    }

    public async Task DownloadAsync(bool consent, IProgress<double>? progress, CancellationToken token)
    {
        if (!consent) throw new InvalidOperationException("Explicit CUDA component download consent is required.");
        await ExtractionGate.WaitAsync(token).ConfigureAwait(false);
        string? archivePath = null;
        var partials = new List<string>();
        try
        {
            using var stream = ReadManifest();
            var identity = Convert.ToHexStringLower(SHA256.HashData(stream));
            stream.Position = 0;
            using var json = JsonDocument.Parse(stream);
            var root = json.RootElement;
            if (root.GetProperty("runtimeId").GetString() != RuntimeId ||
                root.GetProperty("sourceCommit").GetString() != AiLyricsRuntimePackage.SourceCommit ||
                root.GetProperty("backend").GetString() != "cuda")
                throw new InvalidDataException("CUDA component is not bound to this engine version.");
            using var descriptor = ReadDownloadDescriptor();
            var download = descriptor.RootElement.GetProperty("download");
            var uri = new Uri(download.GetProperty("url").GetString()!);
            if (uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" ||
                !uri.AbsolutePath.StartsWith("/airanluo-dot/DropSpace/releases/download/", StringComparison.Ordinal))
                throw new InvalidDataException("Unrecognized CUDA component release source.");
            var assetName = "DropSpace-CUDA-win-x64-" + _appTag + ".zip";
            if (download.GetProperty("name").GetString() != assetName || uri.AbsoluteUri !=
                "https://github.com/airanluo-dot/DropSpace/releases/download/" + _appTag + "/" + assetName)
                throw new InvalidDataException("CUDA asset does not belong to this exact release.");
            var archiveBytes = download.GetProperty("bytes").GetInt64();
            var archiveHash = download.GetProperty("sha256").GetString();
            if (archiveBytes is <= 0 or > 1_073_741_824 || !IsHash(archiveHash))
                throw new InvalidDataException("Invalid CUDA archive fingerprint.");
            var files = root.GetProperty("files").EnumerateArray().ToArray();
            if (files.Length != Names.Length) throw new InvalidDataException("Unexpected CUDA component count.");
            long expanded = 0;
            for (var i = 0; i < Names.Length; i++)
            {
                var bytes = files[i].GetProperty("bytes").GetInt64();
                if (files[i].GetProperty("name").GetString() != Names[i] || !IsHash(files[i].GetProperty("sha256").GetString()) ||
                    bytes <= 0 || bytes > (Names[i] == "cublasLt64_13.dll" ? 805_306_368L : 536_870_912L))
                    throw new InvalidDataException("Invalid CUDA component fingerprint.");
                expanded += bytes;
            }
            if (expanded > 1_073_741_824) throw new InvalidDataException("CUDA payload exceeds budget.");
            var relative = Path.Combine(RuntimeId, identity);
            archivePath = ReparseSafePathPolicy.PrepareContainedFileDestination(_root, Path.Combine(relative, Guid.NewGuid().ToString("N") + ".zip.partial"));
            if (new DriveInfo(Path.GetPathRoot(_root)!).AvailableFreeSpace < archiveBytes + expanded + 64L * 1024 * 1024)
                throw new IOException("Insufficient free space for CUDA components.");
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(TimeSpan.FromMinutes(30));
            token = budget.Token;
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            using var response = await GetDownloadAsync(client, uri, token).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength is { } length && length != archiveBytes)
                throw new InvalidDataException("CUDA download size changed.");
            await using (var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                var buffer = new byte[65536]; long received = 0;
                while (true)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
                    idle.CancelAfter(TimeSpan.FromSeconds(45));
                    var count = await input.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    received += count;
                    if (received > archiveBytes) throw new InvalidDataException("CUDA archive exceeded its trusted size.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    progress?.Report((double)received / archiveBytes * 0.85);
                }
                if (received != archiveBytes) throw new EndOfStreamException("Incomplete CUDA component download.");
            }
            if (!await VerifyAsync(archivePath, archiveHash!, archiveBytes, token).ConfigureAwait(false))
                throw new InvalidDataException("CUDA archive SHA256 mismatch.");
            await using var archiveInput = ReparseSafeFileOpen.OpenRead(archivePath);
            using var zip = new System.IO.Compression.ZipArchive(archiveInput, System.IO.Compression.ZipArchiveMode.Read);
            var allowed = Names.Concat(new[] { "cuda-runtime-manifest.json", "LICENSE-CUDA.txt", "LICENSE-llama.cpp" }).ToHashSet(StringComparer.Ordinal);
            if (zip.Entries.Count != allowed.Count || zip.Entries.Select(entry => entry.FullName).Distinct(StringComparer.Ordinal).Count() != allowed.Count ||
                zip.Entries.Any(entry => !allowed.Contains(entry.FullName)))
                throw new InvalidDataException("Unexpected CUDA archive entry.");
            var embeddedManifest = descriptor.RootElement.GetProperty("manifest");
            var manifestEntry = zip.GetEntry("cuda-runtime-manifest.json")!;
            if (manifestEntry.Length != embeddedManifest.GetProperty("bytes").GetInt64()) throw new InvalidDataException("Archive manifest size changed.");
            using (var input = manifestEntry.Open())
                if (!CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(input, token).ConfigureAwait(false),
                    Convert.FromHexString(embeddedManifest.GetProperty("sha256").GetString()!)))
                    throw new InvalidDataException("Archive manifest differs from the App trust anchor.");
            for (var i = 0; i < Names.Length; i++)
            {
                var entry = zip.GetEntry(Names[i])!;
                var bytes = files[i].GetProperty("bytes").GetInt64();
                if (entry.Length != bytes) throw new InvalidDataException("CUDA component size mismatch.");
                var partial = ReparseSafePathPolicy.PrepareContainedFileDestination(_root, Path.Combine(relative, Guid.NewGuid().ToString("N") + ".partial"));
                partials.Add(partial);
                await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await using (var input = entry.Open())
                {
                    var buffer = new byte[65536]; long written = 0;
                    while (true)
                    {
                        var count = await input.ReadAsync(buffer, token).ConfigureAwait(false);
                        if (count == 0) break;
                        written += count;
                        if (written > bytes) throw new InvalidDataException("CUDA component exceeded its trusted size.");
                        await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    }
                    if (written != bytes) throw new InvalidDataException("Truncated CUDA component.");
                }
                if (!await VerifyAsync(partial, files[i].GetProperty("sha256").GetString()!, bytes, token).ConfigureAwait(false))
                    throw new InvalidDataException("CUDA component SHA256 mismatch.");
                progress?.Report(0.85 + 0.15 * (i + 1) / Names.Length);
            }
            foreach (var name in new[] { "LICENSE-CUDA.txt", "LICENSE-llama.cpp" })
            {
                var entry = zip.GetEntry(name)!;
                var notice = descriptor.RootElement.GetProperty("notices").EnumerateArray().Single(item => item.GetProperty("name").GetString() == name);
                if (entry.Length != notice.GetProperty("bytes").GetInt64()) throw new InvalidDataException("Invalid CUDA notice size.");
                var destination = ReparseSafePathPolicy.PrepareContainedFileDestination(_root, Path.Combine(relative, name));
                var partial = ReparseSafePathPolicy.PrepareContainedFileDestination(_root, Path.Combine(relative, Guid.NewGuid().ToString("N") + ".notice.partial"));
                partials.Add(partial);
                await using (var input = entry.Open())
                await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                    await input.CopyToAsync(output, token).ConfigureAwait(false);
                if (!await VerifyAsync(partial, notice.GetProperty("sha256").GetString()!, notice.GetProperty("bytes").GetInt64(), token).ConfigureAwait(false))
                    throw new InvalidDataException("CUDA notice SHA256 mismatch.");
                ReparseSafePathPolicy.RevalidatePreparedDestination(_root, destination);
                File.Move(partial, destination, true);
            }
            token.ThrowIfCancellationRequested();
            for (var i = 0; i < Names.Length; i++)
            {
                var destination = ReparseSafePathPolicy.PrepareContainedFileDestination(_root, Path.Combine(relative, Names[i]));
                ReparseSafePathPolicy.RevalidatePreparedDestination(_root, destination);
                File.Move(partials[i], destination, true);
            }
        }
        finally
        {
            try
            {
                foreach (var partial in partials) DeletePartial(partial);
                if (archivePath is not null) DeletePartial(archivePath);
            }
            finally { ExtractionGate.Release(); }
        }
    }

    private static void DeletePartial(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { System.Diagnostics.Trace.TraceWarning("CUDA partial cleanup needs retry ({0})", error.GetType().Name); }
    }

    private static async Task<HttpResponseMessage> GetDownloadAsync(HttpClient client, Uri uri, CancellationToken token)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps || uri.Host is not ("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))
                throw new InvalidDataException("Untrusted CUDA download redirect.");
            var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location; response.Dispose();
                if (location is null) throw new InvalidDataException("Missing CUDA download location.");
                uri = new Uri(uri, location); continue;
            }
            if (!response.IsSuccessStatusCode) { response.Dispose(); throw new IOException("CUDA component release is unavailable."); }
            return response;
        }
        throw new IOException("Too many CUDA download redirects.");
    }

    private static bool IsHash(string? hash) => hash is { Length: 64 } && hash.All(Uri.IsHexDigit);

    /// <summary>Rehashes the materialized component under retained read leases. The resident owner
    /// releases these only after actual native exit and reader settlement, including failed startup.</summary>
    internal async Task<CudaLyricsComponentLease> OpenWorkerLeaseAsync(CancellationToken token)
    {
        var executable = await EnsureWorkerAsync(token).ConfigureAwait(false);
        var streams = new List<FileStream>();
        try
        {
            using var manifest = ReadManifest();
            var identity = Convert.ToHexStringLower(SHA256.HashData(manifest));
            if (!StringComparer.Ordinal.Equals(Path.GetFileName(Path.GetDirectoryName(executable)), identity))
                throw new InvalidDataException("CUDA manifest changed before launch.");
            manifest.Position = 0;
            using var document = await JsonDocument.ParseAsync(manifest, cancellationToken: token).ConfigureAwait(false);
            var files = document.RootElement.GetProperty("files").EnumerateArray().ToArray();
            for (var i = 0; i < Names.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                var path = Path.Combine(Path.GetDirectoryName(executable)!, Names[i]);
                ReparseSafePathPolicy.ResolveExistingContainedPath(_root, path);
                var input = ReparseSafeFileOpen.OpenRead(path);
                streams.Add(input);
                if (input.Length != files[i].GetProperty("bytes").GetInt64() ||
                    !CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(input, token).ConfigureAwait(false),
                        Convert.FromHexString(files[i].GetProperty("sha256").GetString()!)))
                    throw new InvalidDataException("CUDA component changed before launch.");
                input.Position = 0;
            }
            token.ThrowIfCancellationRequested();
            return new CudaLyricsComponentLease(executable, streams);
        }
        catch
        {
            foreach (var stream in streams) stream.Dispose();
            throw;
        }
    }

    private async Task<bool> VerifyAsync(string path, string hash, long bytes, CancellationToken token)
    {
        if (!File.Exists(path)) return false;
        ReparseSafePathPolicy.ResolveExistingContainedPath(_root, path);
        await using var input = ReparseSafeFileOpen.OpenRead(path);
        return input.Length == bytes && CryptographicOperations.FixedTimeEquals(
            await SHA256.HashDataAsync(input, token).ConfigureAwait(false), Convert.FromHexString(hash));
    }
}

internal sealed class CudaLyricsComponentLease(string executablePath, List<FileStream> streams) : IDisposable
{
    internal string ExecutablePath { get; } = executablePath;
    public void Dispose()
    {
        foreach (var stream in streams) stream.Dispose();
    }
}
