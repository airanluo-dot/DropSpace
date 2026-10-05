using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Lyrics;

/// <summary>Independent, explicitly injected CUDA experiment. Shipping resources never resolve it.</summary>
public sealed class CudaLyricsRuntimePackage
{
    public const string RuntimeId = "llama-cpp-v0.5.0-cuda12-win-x64-experiment-v1";
    public const string ResourcePrefix = "DropSpace.CudaLyricsRuntime.";
    public const string ManifestResourceName = ResourcePrefix + "cuda-runtime-manifest.json";
    public const string ExecutableName = "plain-lyrics-worker-cuda.exe";
    private static readonly string[] Names = [ExecutableName, "cublas64_12.dll", "cublasLt64_12.dll"];
    private static readonly SemaphoreSlim ExtractionGate = new(1, 1);
    private readonly Func<string, Stream?> _openResource;
    private readonly string _root;

    public CudaLyricsRuntimePackage(Assembly assembly, string cacheRoot)
        : this((assembly ?? throw new ArgumentNullException(nameof(assembly))).GetManifestResourceStream, cacheRoot) { }

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
                    !IsHash(file.GetProperty("sha256").GetString()) || bytes is <= 0 or > 536_870_912)
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
