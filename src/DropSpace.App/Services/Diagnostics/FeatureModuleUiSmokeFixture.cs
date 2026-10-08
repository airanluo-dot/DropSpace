using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DropSpace.Core.Dlc;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Dlc;
using DropSpace.Infrastructure.Settings;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.App.Services.Diagnostics;

/// <summary>Explicit native diagnostic fixture only; never touches the production user profile.</summary>
internal static class FeatureModuleUiSmokeFixture
{
    internal const string RootPrefix = "DropSpace-module-ui-";
    private const string MarkerName = "module-ui-fixture.txt";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    // App's diagnostic hook calls this before service composition or settings load.
    internal static async Task PrepareRootAsync(string root, CancellationToken token = default)
    {
        root = ValidateRoot(root);
        if (Directory.Exists(root) || File.Exists(root)) throw new IOException("The module UI fixture root must be fresh.");
        Directory.CreateDirectory(root);
        EnsureNoReparse(root);
        await File.WriteAllTextAsync(Path.Combine(root, MarkerName), Path.GetFileName(root), token);
        var settings = new JsonSettingsService(new AppStoragePaths(root));
        await settings.SaveAsync(new AppSettings
        {
            PrivacyChoicesCompleted = true, ClipboardPaused = true, StartWithWindows = false,
        }, token);
    }

    // Stage only. The live native harness then calls production runtime.InstallAsync,
    // preserving the real transaction/handshake/registration path in the same UI session.
    internal static async Task<OfficialModulePackage> StagePackageAsync(string root, string archivePath,
        string recordPath, ModulePackageStore store, CancellationToken token = default)
    {
        root = ValidateRoot(root);
        var marker = Path.Combine(root, MarkerName);
        EnsureNoReparse(marker);
        if (new FileInfo(marker).Length > 128 || await File.ReadAllTextAsync(marker, token) != Path.GetFileName(root))
            throw new InvalidDataException("The module UI fixture was not prepared.");
        archivePath = ValidateSourceFile(archivePath, ".zip");
        recordPath = ValidateSourceFile(recordPath, ".json");
        var recordBytes = await ReadRecordAsync(recordPath, token);
        using (var document = JsonDocument.Parse(recordBytes))
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                document.RootElement.EnumerateObject().Any(property => !keys.Add(property.Name)))
                throw new InvalidDataException("The module fixture descriptor has duplicate or invalid fields.");
        }
        var package = JsonSerializer.Deserialize<OfficialModulePackage>(recordBytes, JsonOptions) ??
            throw new InvalidDataException("The module fixture descriptor is invalid.");
        OfficialModuleCatalog.Require(package);
        if (package.Id != "dropspace.sample") throw new InvalidDataException("The native fixture accepts only the independent sample module.");
        var expectedVersion = Path.Combine(root, "Modules", "packages", package.Id, package.Version);
        if (!string.Equals(store.GetVersionDirectory(package.Id, package.Version), expectedVersion, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The module store must belong to the isolated fixture root.");
        await using (var archive = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous))
        {
            if (archive.Length != package.Bytes) throw new InvalidDataException("The module fixture archive size differs from the official record.");
            var digest = await SHA256.HashDataAsync(archive, token);
            if (!CryptographicOperations.FixedTimeEquals(digest, Convert.FromHexString(package.Sha256)))
                throw new InvalidDataException("The module fixture archive hash differs from the official record.");
            archive.Position = 0;
            var modulesRoot = Path.Combine(root, "Modules");
            var staged = ReparseSafePathPolicy.PrepareContainedFileDestination(modulesRoot,
                Path.Combine("staging", package.Id, package.Version, "package.zip"));
            EnsureNoReparse(staged);
            await using var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await archive.CopyToAsync(output, token);
            await output.FlushAsync(token);
            output.Flush(true);
        }
        return package;
    }

    // Alternate startup-seed route, only before the first runtime.InitializeAsync.
    // The normal single-session lifecycle harness uses StagePackageAsync + InstallAsync.
    internal static async Task PrepareModuleAsync(string root, string archivePath, string recordPath,
        ModulePackageStore store, CancellationToken token = default)
    {
        var package = await StagePackageAsync(root, archivePath, recordPath, store, token);
        var manifest = await store.PrepareAsync(package, new Dictionary<string, ModuleInstallation>(), token);
        await store.WriteAsync(new ModuleInstallation(manifest.Id, manifest.Version, true, manifest.Data.Version), token);
    }

    private static string ValidateRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new ArgumentException("A module UI diagnostic requires an absolute isolated test root.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var name = Path.GetFileName(full);
        if (!string.Equals(Path.GetDirectoryName(full), temporary, StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith(RootPrefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[RootPrefix.Length..], "N", out _))
            throw new ArgumentException("A module UI diagnostic requires a fresh GUID child of the temporary directory.");
        EnsureNoReparse(full);
        return full;
    }

    private static string ValidateSourceFile(string file, string extension)
    {
        if (string.IsNullOrWhiteSpace(file) || !Path.IsPathFullyQualified(file) ||
            !Path.GetExtension(file).Equals(extension, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The module fixture requires an absolute archive and record path.");
        file = Path.GetFullPath(file); EnsureNoReparse(file);
        if (!File.Exists(file)) throw new FileNotFoundException("The module fixture source does not exist.", file);
        return file;
    }

    private static void EnsureNoReparse(string file)
    {
        for (var current = Path.GetFullPath(file); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Module UI fixtures do not follow reparse paths.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static async Task<byte[]> ReadRecordAsync(string file, CancellationToken token)
    {
        await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (input.Length is <= 0 or > 16 * 1024) throw new InvalidDataException("The module fixture record exceeds its size bound.");
        using var memory = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        {
            if (memory.Length + count > 16 * 1024) throw new InvalidDataException("The module fixture record exceeds its size bound.");
            memory.Write(buffer, 0, count);
        }
        return memory.ToArray();
    }
}
