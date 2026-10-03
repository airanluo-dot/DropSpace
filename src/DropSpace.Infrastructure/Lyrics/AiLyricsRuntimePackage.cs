using System.Reflection;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Text.Json;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Lyrics;

/// <summary>
/// Materializes only the runtime embedded in the installed application. The manifest is read from
/// that same assembly, never from user-editable settings, download URLs, or beside an executable.
/// </summary>
public sealed class AiLyricsRuntimePackage
{
    public const string RuntimeId = "llama-cpp-v0.5.0-cpu-win-x64";
    public const string SourceCommit = "7fe450e19305b828c199d602c23a8337aaa1f03b";
    public const string ManifestResourceName = "DropSpace.AiLyricsRuntime.runtime-manifest.json";
    public const string ExecutableResourceName = "DropSpace.AiLyricsRuntime.llama-completion.exe";
    public const string Avx2ExecutableResourceName = "DropSpace.AiLyricsRuntime.llama-completion-avx2.exe";
    private const string ExecutableName = "llama-completion.exe";
    private static readonly SemaphoreSlim ExtractionGate = new(1, 1);
    private readonly Func<string, Stream?> _openResource;
    private readonly string _root;
    private readonly bool _useAvx2;

    public AiLyricsRuntimePackage(Assembly assembly, string cacheRoot)
        : this((assembly ?? throw new ArgumentNullException(nameof(assembly))).GetManifestResourceStream, cacheRoot) { }

    public AiLyricsRuntimePackage(Assembly assembly, string cacheRoot, bool useAvx2)
        : this((assembly ?? throw new ArgumentNullException(nameof(assembly))).GetManifestResourceStream, cacheRoot, useAvx2) { }

    internal AiLyricsRuntimePackage(Func<string, Stream?> openResource, string cacheRoot, bool? useAvx2 = null)
    {
        ArgumentNullException.ThrowIfNull(openResource);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        _openResource = openResource;
        _root = Path.GetFullPath(cacheRoot);
        _useAvx2 = useAvx2 ?? (Avx2.IsSupported && Fma.IsSupported && X86Base.IsSupported &&
            (X86Base.CpuId(1, 0).Ecx & (1 << 29)) != 0);
    }

    /// <summary>Versions cached translations by the exact trusted embedded runtime manifest without
    /// extracting executables or rehashing model weights. Missing resources cannot validate a cache.</summary>
    public string GetManifestCacheIdentity()
    {
        using var resource = _openResource(ManifestResourceName)
            ?? throw new FileNotFoundException("This build does not include the verified local AI runtime.");
        if (resource.Length is <= 0 or > 16_384) throw new InvalidDataException("Invalid embedded runtime manifest size.");
        return Convert.ToHexStringLower(SHA256.HashData(resource));
    }

    public Task<string> EnsureExecutableAsync(CancellationToken token) => EnsureComponentAsync(token, tokenizer: false);

    /// <summary>Extracts only a manifest-bound resident worker; CPU builds cannot load Vulkan.</summary>
    public async Task<string> EnsureResidentWorkerAsync(bool gpu, CancellationToken token)
    {
        await ExtractionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var resource = _openResource(ManifestResourceName)
                ?? throw new FileNotFoundException("The embedded runtime is missing.");
            if (resource.Length is <= 0 or > 16_384) throw new InvalidDataException("Invalid runtime manifest size.");
            using var document = await JsonDocument.ParseAsync(resource, cancellationToken: token).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 1 || root.GetProperty("runtimeId").GetString() != RuntimeId ||
                root.GetProperty("sourceCommit").GetString() != SourceCommit)
                throw new InvalidDataException("Unrecognized embedded runtime.");
            var resident = root.GetProperty("resident");
            if (resident.GetProperty("protocol").GetInt32() != 1 || resident.GetProperty("profile").GetString() != "hy-q8-plain-resident-v1")
                throw new InvalidDataException("Unrecognized resident runtime protocol.");
            var variant = gpu ? "vulkan" : _useAvx2 ? "avx2" : "cpu";
            var name = gpu ? "plain-lyrics-worker-vulkan.exe" : _useAvx2 ? "plain-lyrics-worker-avx2.exe" : "plain-lyrics-worker.exe";
            var metadata = resident.GetProperty(variant);
            if (metadata.GetProperty("executable").GetString() != name) throw new InvalidDataException("Unexpected resident executable.");
            var hash = metadata.GetProperty("sha256").GetString();
            var bytes = metadata.GetProperty("bytes").GetInt64();
            if (hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit) || bytes is <= 0 or > 536_870_912)
                throw new InvalidDataException("Invalid resident executable integrity metadata.");
            hash = hash.ToLowerInvariant();
            var relative = Path.Combine(RuntimeId, hash);
            var path = ReparseSafePathPolicy.PrepareContainedFileDestination(_root, Path.Combine(relative, name));
            if (await VerifyAsync(path, hash, bytes, token).ConfigureAwait(false)) return path;
            var partial = ReparseSafePathPolicy.PrepareContainedFileDestination(_root, Path.Combine(relative, $"{Guid.NewGuid():N}.partial"));
            try
            {
                using var embedded = _openResource("DropSpace.AiLyricsRuntime." + name)
                    ?? throw new FileNotFoundException("The embedded resident executable is missing.");
                if (embedded.Length != bytes) throw new InvalidDataException("Resident executable size mismatch.");
                await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                    await embedded.CopyToAsync(output, token).ConfigureAwait(false);
                if (!await VerifyAsync(partial, hash, bytes, token).ConfigureAwait(false)) throw new InvalidDataException("Resident executable SHA256 mismatch.");
                ReparseSafePathPolicy.RevalidatePreparedDestination(_root, path);
                File.Move(partial, path, true);
                return path;
            }
            finally { if (File.Exists(partial)) File.Delete(partial); }
        }
        finally { ExtractionGate.Release(); }
    }

    public Task<string> EnsureTokenizerAsync(CancellationToken token) => EnsureComponentAsync(token, tokenizer: true);

    private async Task<string> EnsureComponentAsync(CancellationToken token, bool tokenizer)
    {
        await ExtractionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var resource = _openResource(ManifestResourceName)
                ?? throw new FileNotFoundException("This build does not include the verified local AI runtime.");
            if (resource.Length is <= 0 or > 16_384) throw new InvalidDataException("Invalid embedded runtime manifest size.");
            using var document = await JsonDocument.ParseAsync(resource, cancellationToken: token).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 1 ||
                root.GetProperty("runtimeId").GetString() != RuntimeId ||
                root.GetProperty("sourceCommit").GetString() != SourceCommit ||
                root.GetProperty("executable").GetString() != ExecutableName)
                throw new InvalidDataException("Unrecognized embedded local AI runtime.");
            var executableResource = ExecutableResourceName;
            var selected = root;
            var fileName = ExecutableName;
            if (tokenizer)
            {
                if (!root.TryGetProperty("tokenizer", out var tokenizerMetadata) ||
                    tokenizerMetadata.GetProperty("executable").GetString() != "llama-tokenize.exe")
                    throw new InvalidDataException("The embedded tokenizer metadata is missing or invalid.");
                selected = tokenizerMetadata;
                fileName = "llama-tokenize.exe";
                executableResource = "DropSpace.AiLyricsRuntime.llama-tokenize.exe";
            }
            else if (_useAvx2 && root.TryGetProperty("avx2", out var optimized))
            {
                if (optimized.GetProperty("executable").GetString() != "llama-completion-avx2.exe")
                    throw new InvalidDataException("Invalid optimized runtime executable.");
                selected = optimized;
                executableResource = Avx2ExecutableResourceName;
            }
            var hash = selected.GetProperty("sha256").GetString();
            var bytes = selected.GetProperty("bytes").GetInt64();
            if (hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit) || bytes is <= 0 or > 134_217_728)
                throw new InvalidDataException("Invalid embedded runtime integrity metadata.");
            hash = hash.ToLowerInvariant();
            var path = ReparseSafePathPolicy.PrepareContainedFileDestination(_root,
                Path.Combine(RuntimeId, hash, fileName));
            if (await VerifyAsync(path, hash, bytes, token).ConfigureAwait(false)) return path;
            var partial = ReparseSafePathPolicy.PrepareContainedFileDestination(_root,
                Path.Combine(RuntimeId, hash, $"{Guid.NewGuid():N}.partial"));
            try
            {
                using var executable = _openResource(executableResource)
                    ?? throw new FileNotFoundException("The embedded local AI runtime is missing.");
                if (executable.Length != bytes) throw new InvalidDataException("Embedded runtime size mismatch.");
                await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                {
                    await executable.CopyToAsync(output, token).ConfigureAwait(false);
                    await output.FlushAsync(token).ConfigureAwait(false);
                }
                if (!await VerifyAsync(partial, hash, bytes, token).ConfigureAwait(false))
                    throw new InvalidDataException("Embedded runtime SHA256 verification failed.");
                ReparseSafePathPolicy.RevalidatePreparedDestination(_root, path);
                File.Move(partial, path, true);
                return path;
            }
            finally
            {
                if (File.Exists(partial)) File.Delete(partial);
            }
        }
        finally { ExtractionGate.Release(); }
    }

    private async Task<bool> VerifyAsync(string path, string hash, long bytes, CancellationToken token)
    {
        if (!File.Exists(path)) return false;
        ReparseSafePathPolicy.ResolveExistingContainedPath(_root, path);
        await using var input = ReparseSafeFileOpen.OpenRead(path);
        if (input.Length != bytes) return false;
        var actual = await SHA256.HashDataAsync(input, token).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(hash));
    }
}
