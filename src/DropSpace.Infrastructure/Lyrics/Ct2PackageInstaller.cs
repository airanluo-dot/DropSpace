using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using DropSpace.Core.Policies;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Lyrics;

public sealed record Ct2PackageFile(string Path, long Bytes, string Sha256);

/// <summary>Reviewed metadata supplied by the app catalog, never by the downloaded archive.</summary>
public sealed record Ct2PackageCatalogEntry(string PackageId, string SourceLanguage, string TargetLanguage,
    long ArchiveBytes, string ArchiveSha256,
    string ManifestSha256, IReadOnlyList<Ct2PackageFile> Files);

/// <summary>Installs a reviewed CT2 archive without exposing a generic archive extraction surface.</summary>
public sealed class Ct2PackageInstaller(string root)
{
    private const long MaximumPackageBytes = 4L * 1024 * 1024 * 1024;
    private const int MaximumManifestBytes = 1024 * 1024;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> InstallGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    private readonly SemaphoreSlim _gate = InstallGates.GetOrAdd(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), static _ => new SemaphoreSlim(1, 1));

    public async Task<Ct2PackageReference> InstallAsync(Ct2PackageCatalogEntry catalog, Stream archive,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(archive);
        catalog = ValidateCatalog(catalog);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        string? staging = null;
        string? archivePath = null;
        try
        {
            // Check ancestors before creating anything, including when the root does not exist yet.
            EnsureSafeAncestors(_root);
            Directory.CreateDirectory(_root);
            EnsureSafeAncestors(_root);
            // A new reviewed manifest installs beside an older package, never over it.
            var packageRoot = PrepareDestination(_root, $"{catalog.PackageId}-{catalog.ManifestSha256.ToLowerInvariant()}");
            RequireAbsent(packageRoot);
            var temporaryName = $".{catalog.PackageId}.{Guid.NewGuid():N}";
            var preparedStaging = PrepareDestination(_root, temporaryName + ".staging");
            RequireAbsent(preparedStaging);
            Directory.CreateDirectory(preparedStaging);
            staging = preparedStaging;
            EnsureSafeAncestors(staging);

            var preparedArchive = PrepareDestination(_root, temporaryName + ".archive.tmp");
            // Keep the verified archive on the same volume with bounded memory, never in a MemoryStream.
            await using (var bounded = new FileStream(preparedArchive, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                archivePath = preparedArchive;
                await CopyVerifiedAsync(archive, bounded, catalog.ArchiveBytes, catalog.ArchiveSha256, token).ConfigureAwait(false);
                bounded.Position = 0;
                using var zip = new ZipArchive(bounded, ZipArchiveMode.Read, leaveOpen: true);
                var expected = catalog.Files.ToDictionary(x => x.Path, StringComparer.Ordinal);
                if (zip.Entries.Count != expected.Count)
                    throw new InvalidDataException("CT2 archive inventory does not match the reviewed catalog.");
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in zip.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    var name = ValidatePath(entry.FullName);
                    if (!expected.TryGetValue(name, out var file) || !seen.Add(name))
                        throw new InvalidDataException("CT2 archive contains an unexpected file or collision.");
                    RejectSpecialEntry(entry);
                    if (entry.Length != file.Bytes || entry.CompressedLength > catalog.ArchiveBytes)
                        throw new InvalidDataException("CT2 component size does not match the reviewed catalog.");
                    EnsureSafeAncestors(staging);
                    var destination = ReparseSafePathPolicy.PrepareContainedFileDestination(staging,
                        name.Replace('/', Path.DirectorySeparatorChar));
                    EnsureSafeAncestors(destination);
                    await using var input = entry.Open();
                    await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough);
                    await CopyVerifiedAsync(input, output, file.Bytes, file.Sha256, token).ConfigureAwait(false);
                }
                if (seen.Count != expected.Count)
                    throw new InvalidDataException("CT2 archive is missing a reviewed component.");
            }
            DeleteArchive(archivePath);
            archivePath = null;
            var reference = new Ct2PackageReference(staging, catalog.ManifestSha256);
            using (await Ct2PrivatePackage.OpenAsync(reference, catalog.SourceLanguage, catalog.TargetLanguage, token).ConfigureAwait(false)) { }
            EnsureSafeAncestors(staging);
            EnsureSafeAncestors(packageRoot);
            RequireAbsent(packageRoot);
            token.ThrowIfCancellationRequested();
            Directory.Move(staging, packageRoot); // Sibling promotion is same-volume and atomic; no overwrite.
            staging = null;
            return new(packageRoot, catalog.ManifestSha256);
        }
        finally
        {
            // Even refusal to clean a tampered staging tree must release the install gate and archive.
            try
            {
                try { if (staging is not null) SafeDelete(staging); }
                finally { if (archivePath is not null) DeleteArchive(archivePath); }
            }
            finally { _gate.Release(); }
        }
    }

    private static async Task CopyVerifiedAsync(Stream input, Stream output, long exactBytes,
        string expectedHash, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long total = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            // Read at most one byte beyond the reviewed budget, including on a non-seekable source.
            var count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, exactBytes - total + 1)), token).ConfigureAwait(false);
            if (count == 0) break;
            if (count > exactBytes - total)
                throw new InvalidDataException("CT2 content exceeds its reviewed byte budget.");
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            hash.AppendData(buffer, 0, count);
            total += count;
        }
        if (total != exactBytes || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(expectedHash)))
            throw new InvalidDataException("CT2 content size or digest does not match the reviewed catalog.");
        await output.FlushAsync(token).ConfigureAwait(false);
    }

    private static Ct2PackageCatalogEntry ValidateCatalog(Ct2PackageCatalogEntry value)
    {
        if (value.PackageId is null || value.PackageId.Length is < 1 or > 100 ||
            value.PackageId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) || IsReserved(value.PackageId) ||
            value.ArchiveBytes is < 1 or > MaximumPackageBytes || value.Files is null || value.Files.Count is < 1 or > 4096 ||
            !Ct2RoutePlanner.IsSupported(value.SourceLanguage, value.TargetLanguage))
            throw new InvalidDataException("Invalid CT2 catalog entry.");
        Ct2PrivatePackage.ValidateHash(value.ArchiveSha256);
        Ct2PrivatePackage.ValidateHash(value.ManifestSha256);
        // Snapshot caller-owned inventory before the first await so validation remains authoritative.
        value = value with { Files = value.Files.ToArray() };
        if (value.Files.Count is < 1 or > 4096) throw new InvalidDataException("Invalid CT2 catalog inventory size.");
        long total = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Ct2PackageFile? manifest = null;
        foreach (var file in value.Files)
        {
            if (file is null) throw new InvalidDataException("Invalid CT2 component catalog.");
            Ct2PrivatePackage.ValidateHash(file.Sha256);
            var name = ValidatePath(file.Path);
            if (!names.Add(name) || directories.ContainsKey(name) || file.Bytes is < 1 or > MaximumPackageBytes ||
                file.Bytes > MaximumPackageBytes - total)
                throw new InvalidDataException("Invalid CT2 component catalog.");
            total += file.Bytes;
            for (var slash = name.IndexOf('/'); slash >= 0; slash = name.IndexOf('/', slash + 1))
            {
                var directory = name[..slash];
                if (names.Contains(directory) || (directories.TryGetValue(directory, out var existing) && existing != directory))
                    throw new InvalidDataException("CT2 component paths contain a file/directory or case collision.");
                directories[directory] = directory;
            }
            if (name == "manifest.json") manifest = file;
            else if (!(name.StartsWith("engine/", StringComparison.Ordinal) || name.StartsWith("model/", StringComparison.Ordinal)) ||
                     name[(name.IndexOf('/') + 1)..].Length > 240)
                throw new InvalidDataException("CT2 catalog contains a component outside its private layout.");
        }
        if (manifest is null || manifest.Bytes > MaximumManifestBytes ||
            !manifest.Sha256.Equals(value.ManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("CT2 catalog omits or disagrees with its reviewed manifest.");
        return value;
    }

    private static string ValidatePath(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 247 || name.Contains('\\') || name.StartsWith('/') || name.Contains(':'))
            throw new InvalidDataException("Invalid CT2 ZIP name.");
        var parts = name.Split('/');
        if (parts.Length > 9 || parts.Any(p => p.Length is < 1 or > 100 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') ||
                p.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '+')) || IsReserved(p.Split('.')[0])))
            throw new InvalidDataException("Invalid CT2 ZIP path.");
        return name;
    }

    private static bool IsReserved(string name) => name.ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL" or
        "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9" or
        "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9";

    private static void RejectSpecialEntry(ZipArchiveEntry entry)
    {
        var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        if (unixType is not (0 or 0x8000) || (entry.ExternalAttributes & (int)(FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new InvalidDataException("CT2 archives may contain only regular files, never links or reparse points.");
    }

    private static string PrepareDestination(string root, string relative)
    {
        var path = PayloadPathPolicy.ResolveContainedPath(root, relative);
        EnsureSafeAncestors(path);
        return path;
    }

    private static void RequireAbsent(string path)
    {
        if (TryGetAttributes(path) is not null)
            throw new IOException("The CT2 package or private temporary destination already exists.");
    }

    private static FileAttributes? TryGetAttributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void EnsureSafeAncestors(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (TryGetAttributes(current) is { } attributes && (attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("CT2 private paths must not traverse reparse points.");
    }

    private static void DeleteArchive(string path)
    {
        EnsureSafeAncestors(path);
        File.Delete(path);
    }

    private static void SafeDelete(string path)
    {
        EnsureSafeAncestors(path);
        if (!Directory.Exists(path)) return;
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.TryPop(out var directory))
        {
            EnsureSafeAncestors(directory);
            foreach (var item in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(item);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Refusing to clean a reparse-point staging tree.");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(item);
            }
        }
        Directory.Delete(path, true);
    }
}
