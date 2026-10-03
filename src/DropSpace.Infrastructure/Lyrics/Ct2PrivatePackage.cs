using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DropSpace.Infrastructure.Lyrics;

/// <summary>The manifest hash must come from a separately reviewed catalog, never from the package itself.</summary>
public sealed record Ct2PackageReference(string PrivateRoot, string ManifestSha256);

/// <summary>Verified, read-locked payload; retain until confirmed child exit. No downloads or discovery.</summary>
internal sealed class Ct2PrivatePackage : IDisposable
{
    private const int MaximumManifestBytes = 1024 * 1024;
    private const long MaximumPackageBytes = 4L * 1024 * 1024 * 1024;
    private readonly List<FileStream> _leases = [];
    internal string Executable { get; private set; } = "";
    internal string ModelDirectory { get; private set; } = "";
    internal string SourceTokenizer { get; private set; } = "";
    internal string TargetTokenizer { get; private set; } = "";
    internal string? TargetPrefix { get; private set; }
    internal Ct2PackageIdentity Identity { get; private set; } = null!;

    internal static async Task<Ct2PrivatePackage> OpenAsync(Ct2PackageReference reference,
        string source, string target, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ValidateHash(reference.ManifestSha256);
        var package = new Ct2PrivatePackage();
        try
        {
            var root = Path.GetFullPath(reference.PrivateRoot);
            EnsureNoLinks(root);
            var manifest = package.OpenFile(Path.Combine(root, "manifest.json"), MaximumManifestBytes);
            var bytes = new byte[checked((int)manifest.Length)];
            await manifest.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            if (!Hash(bytes).Equals(reference.ManifestSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("CT2 manifest does not match the reviewed catalog.");
            using var document = Ct2Protocol.Parse(bytes);
            var data = document.RootElement;
            Ct2Protocol.Keys(data, "schemaVersion", "sourceLanguage", "targetLanguage", "tokenizerProtocol", "decoderProtocol",
                "engineExecutable", "sourceTokenizer", "targetTokenizer", "targetPrefix", "engineFiles", "modelFiles");
            if (Ct2Protocol.Int(data, "schemaVersion") != 1 || Ct2Protocol.Text(data, "sourceLanguage") != source ||
                Ct2Protocol.Text(data, "targetLanguage") != target || !Ct2RoutePlanner.IsSupported(source, target))
                throw new InvalidDataException("CT2 package route/version mismatch.");
            var tokenizerName = Ct2Protocol.Text(data, "tokenizerProtocol");
            var decoderName = Ct2Protocol.Text(data, "decoderProtocol");
            if (!Enum.TryParse<Ct2TokenizerProtocol>(tokenizerName, out var tokenizer) || !Enum.IsDefined(tokenizer) || tokenizer.ToString() != tokenizerName ||
                !Enum.TryParse<Ct2DecoderProtocol>(decoderName, out var decoder) || !Enum.IsDefined(decoder) || decoder.ToString() != decoderName ||
                (tokenizer == Ct2TokenizerProtocol.ArgosSentencePiece) != (decoder == Ct2DecoderProtocol.Argos))
                throw new InvalidDataException("Unsupported CT2 tokenizer/decoder pair.");
            var prefix = data.GetProperty("targetPrefix");
            package.TargetPrefix = prefix.ValueKind == JsonValueKind.Null ? null : Ct2Protocol.Text(data, "targetPrefix");
            if (package.TargetPrefix is { } p && (p.Length is < 5 or > 64 || !p.StartsWith(">>", StringComparison.Ordinal) ||
                !p.EndsWith("<<", StringComparison.Ordinal) || !char.IsAsciiLetter(p[2]) ||
                p[2..^2].Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) || decoder == Ct2DecoderProtocol.Argos))
                throw new InvalidDataException("Invalid explicit target-language prefix.");
            var engineRoot = Path.Combine(root, "engine");
            package.ModelDirectory = Path.Combine(root, "model");
            long total = 0;
            var engine = await VerifyFilesAsync(engineRoot, data.GetProperty("engineFiles")).ConfigureAwait(false);
            var model = await VerifyFilesAsync(package.ModelDirectory, data.GetProperty("modelFiles")).ConfigureAwait(false);
            package.Executable = ResolveListed(engineRoot, Ct2Protocol.Text(data, "engineExecutable"), engine);
            if (!package.Executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("CT2 requires its packaged single-process executable.");
            package.SourceTokenizer = ResolveListed(package.ModelDirectory, Ct2Protocol.Text(data, "sourceTokenizer"), model);
            package.TargetTokenizer = ResolveListed(package.ModelDirectory, Ct2Protocol.Text(data, "targetTokenizer"), model);
            if (!model.ContainsKey("model.bin") || !model.ContainsKey("config.json"))
                throw new InvalidDataException("Incomplete CT2 model package.");
            package.Identity = new Ct2PackageIdentity(InventoryHash(engine), InventoryHash(model),
                Hash(Encoding.UTF8.GetBytes(string.Join('\n', package.SourceTokenizer[(package.ModelDirectory.Length + 1)..],
                    model[Ct2Protocol.Text(data, "sourceTokenizer")], package.TargetTokenizer[(package.ModelDirectory.Length + 1)..],
                    model[Ct2Protocol.Text(data, "targetTokenizer")], package.TargetPrefix ?? ""))), tokenizer, decoder,
                reference.ManifestSha256.ToLowerInvariant());
            return package;

            async Task<Dictionary<string, string>> VerifyFilesAsync(string directory, JsonElement files)
            {
                EnsureNoLinks(directory);
                if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() is < 1 or > 4096)
                    throw new InvalidDataException("Invalid CT2 inventory size.");
                var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in files.EnumerateArray())
                {
                    token.ThrowIfCancellationRequested();
                    Ct2Protocol.Keys(file, "path", "bytes", "sha256");
                    var path = Ct2Protocol.Text(file, "path");
                    var hash = Ct2Protocol.Text(file, "sha256");
                    ValidateHash(hash);
                    var count = file.GetProperty("bytes");
                    if (count.ValueKind != JsonValueKind.Number || !count.TryGetInt64(out var size) || size is < 1 or > MaximumPackageBytes ||
                        (total += size) > MaximumPackageBytes || !result.TryAdd(path, hash.ToLowerInvariant()))
                        throw new InvalidDataException("Invalid CT2 file size or duplicate path.");
                    var stream = package.OpenFile(Resolve(directory, path), size);
                    if (stream.Length != size || !Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).Equals(hash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("CT2 payload size/hash mismatch.");
                }
                // Unlisted DLLs, Python modules and tokenizer alternatives cannot enter native loading.
                foreach (var path in EnumerateSafeFiles(directory, token))
                    if (!result.ContainsKey(Path.GetRelativePath(directory, path).Replace('\\', '/')))
                        throw new InvalidDataException("CT2 package contains an unlisted file.");
                return result;
            }
        }
        catch { package.Dispose(); throw; }
    }

    private FileStream OpenFile(string path, long maximumBytes)
    {
        EnsureNoLinks(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        _leases.Add(stream);
        if (stream.Length is < 1 || stream.Length > maximumBytes) throw new InvalidDataException("CT2 file exceeds its byte budget.");
        return stream;
    }

    private static IEnumerable<string> EnumerateSafeFiles(string directory, CancellationToken token)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((directory, 0));
        var entries = 0;
        while (pending.TryPop(out var current))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(current.Path))
            {
                token.ThrowIfCancellationRequested();
                if (++entries > 8192 || current.Depth >= 8) throw new InvalidDataException("CT2 directory exceeds its inventory bounds.");
                EnsureNoLinks(entry);
                if (Directory.Exists(entry)) pending.Push((entry, current.Depth + 1));
                else yield return entry;
            }
        }
    }

    private static string ResolveListed(string root, string path, Dictionary<string, string> files)
    {
        if (!files.ContainsKey(path)) throw new InvalidDataException("CT2 metadata names an unlisted file.");
        return Resolve(root, path);
    }

    private static string Resolve(string root, string relative)
    {
        var segments = relative.Split('/');
        if (relative.Length is < 1 or > 240 || segments.Length > 8 || segments.Any(segment =>
                segment.Length is < 1 or > 100 || segment is "." or ".." || segment.EndsWith('.') ||
                segment.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '+')) ||
                IsReserved(segment.Split('.')[0])))
            throw new InvalidDataException("Invalid private CT2 path.");
        return Path.Combine(root, Path.Combine(segments));
    }

    private static bool IsReserved(string name) => name.ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL" or
        "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9" or
        "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9";

    private static void EnsureNoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("CT2 private paths must not traverse reparse points.");
    }

    internal static void ValidateHash(string? hash)
    {
        if (hash is null || hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new InvalidDataException("Invalid CT2 package hash.");
    }
    private static string InventoryHash(Dictionary<string, string> files) => Hash(Encoding.UTF8.GetBytes(
        string.Join('\n', files.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key}:{x.Value}"))));
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public void Dispose() { foreach (var lease in _leases) lease.Dispose(); _leases.Clear(); }
}

internal static class Ct2Protocol
{
    internal static JsonDocument Parse(ReadOnlyMemory<byte> bytes)
    {
        try { return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (JsonException error) { throw new InvalidDataException("Invalid CT2 JSON.", error); }
    }
    internal static void Keys(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid CT2 object.");
        string[] actual;
        try { actual = value.EnumerateObject().Select(x => x.Name).ToArray(); }
        catch (InvalidOperationException error) { throw new InvalidDataException("Invalid CT2 property encoding.", error); }
        if (actual.Length != expected.Length || actual.Distinct(StringComparer.Ordinal).Count() != actual.Length ||
            actual.Except(expected, StringComparer.Ordinal).Any()) throw new InvalidDataException("Invalid CT2 object properties.");
    }
    internal static string Text(JsonElement value, string name)
    {
        var property = value.GetProperty(name);
        if (property.ValueKind != JsonValueKind.String) throw new InvalidDataException("Invalid CT2 string.");
        try { return property.GetString()!; }
        catch (InvalidOperationException error) { throw new InvalidDataException("Invalid CT2 string encoding.", error); }
    }
    internal static int Int(JsonElement value, string name)
    {
        var property = value.GetProperty(name);
        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt32(out var result)) throw new InvalidDataException("Invalid CT2 integer.");
        return result;
    }
}
