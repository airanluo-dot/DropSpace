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

    internal AiLyricsRuntimePackage(Func<string, Stream?> openResource, string cacheRoot, bool? useAvx2 = null)
    {
        ArgumentNullException.ThrowIfNull(openResource);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        _openResource = openResource;
        _root = Path.GetFullPath(cacheRoot);
        _useAvx2 = useAvx2 ?? (Avx2.IsSupported && Fma.IsSupported && X86Base.IsSupported &&
            (X86Base.CpuId(1, 0).Ecx & (1 << 29)) != 0);
    }

    public async Task<string> EnsureExecutableAsync(CancellationToken token)
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
            if (_useAvx2 && root.TryGetProperty("avx2", out var optimized))
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
                Path.Combine(RuntimeId, hash, ExecutableName));
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
