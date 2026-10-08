using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DropSpace.Core.Dlc;
using DropSpace.Infrastructure.Downloads;
using DropSpace.Infrastructure.Storage;

namespace DropSpace.Infrastructure.Dlc;

/// <summary>Owned package/state storage. Activation, UI and process lifetime belong to the runtime.</summary>
public sealed class ModulePackageStore(string root, HttpRangeDownloader downloads) : IDisposable
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true,
    };
    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonOptions)
        { UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip };
    private const long MaximumExpandedBytes = 512L * 1024 * 1024;
    private const int MaximumManifestBytes = 512 * 1024, MaximumStateBytes = 16 * 1024, MaximumSettingsBytes = 150 * 1024;
    private const string ManifestName = "manifest.json", ArchiveName = ".package.zip", RecordName = ".package-record.json";
    private readonly string _root = Path.GetFullPath(root);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _client = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = Timeout.InfiniteTimeSpan };

    public string GetVersionDirectory(string id, string version)
    {
        Identity(id, version);
        return OwnedPath(Path.Combine("packages", id, version));
    }

    // Creating no directories in the constructor preserves the empty-module startup path.
    public string GetDataDirectory(string id)
    {
        Identity(id);
        var directory = OwnedPath(Path.Combine("data", id));
        Directory.CreateDirectory(directory);
        EnsureOwned(directory);
        return directory;
    }

    public async Task<IReadOnlyList<ModuleInstallation>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!Directory.Exists(_root)) return [];
            EnsureOwned(_root);
            var directory = OwnedPath("state");
            if (!Directory.Exists(directory)) return [];
            var result = new List<ModuleInstallation>();
            foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
            {
                var id = Path.GetFileNameWithoutExtension(file);
                try { Identity(id); } catch (InvalidDataException) { continue; }
                ModuleInstallation installation;
                try { installation = await ReadStateAsync(file, id, cancellationToken).ConfigureAwait(false); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                {
                    // Isolate one damaged journal, preserve its bytes and data, and keep
                    // every other module available. A verified backup never auto-enables.
                    try
                    {
                        installation = (await ReadStateAsync(file + ".backup", id, cancellationToken).ConfigureAwait(false))
                            with { Enabled = false, ErrorCode = "ModuleStateRecovered" };
                    }
                    catch (Exception backupError) when (backupError is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                    { installation = new(id, null, false, 0, ErrorCode: "ModuleStateInvalid"); }
                }
                result.Add(installation);
            }
            return result.AsReadOnly();
        }
        finally { _gate.Release(); }
    }

    public async Task WriteAsync(ModuleInstallation installation, CancellationToken cancellationToken = default)
    {
        ValidateState(installation);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var filename = OwnedPath(Path.Combine("state", installation.Id + ".json"));
            if (File.Exists(filename))
            {
                try
                {
                    var previous = await ReadStateAsync(filename, installation.Id, cancellationToken).ConfigureAwait(false);
                    await WriteJsonAsync(filename + ".backup", previous, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception error) when (error is JsonException or InvalidDataException) { }
            }
            await WriteJsonAsync(filename, installation, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<ModuleManifest> ReadManifestAsync(string id, string version, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ReadInstalledAsync(id, version, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>Validate/stage a new immutable version; never removes the active version or changes module data.</summary>
    public async Task<ModuleManifest> PrepareAsync(OfficialModulePackage package,
        IReadOnlyDictionary<string, ModuleInstallation> installed, CancellationToken cancellationToken = default)
    {
        OfficialModuleCatalog.Require(package);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var final = GetVersionDirectory(package.Id, package.Version);
            if (Directory.Exists(final))
            {
                var retained = await ReadInstalledAsync(package.Id, package.Version, cancellationToken, package).ConfigureAwait(false);
                ValidateCompatibility(retained, installed);
                return retained;
            }
            var staging = OwnedPath(Path.Combine("staging", package.Id, package.Version));
            Directory.CreateDirectory(staging);
            EnsureOwned(staging);
            var archivePath = OwnedPath(Path.Combine("staging", package.Id, package.Version, "package.zip"));
            if (!await VerifyFileAsync(archivePath, package.Bytes, package.Sha256, cancellationToken).ConfigureAwait(false))
            {
                DownloadStorage.CheckSpace(staging, checked(package.Bytes + MaximumExpandedBytes));
                var uri = new Uri(package.Url);
                var policy = new DownloadRequestPolicy(_client, destination => OfficialModuleCatalog.TrustedHop(destination, uri),
                    request => request.Headers.UserAgent.ParseAdd("DropSpace-official-module/1"));
                await downloads.DownloadAsync(uri, archivePath, policy, null, cancellationToken, package.Bytes, package.Sha256,
                    package.Bytes, TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            }
            if (!await VerifyFileAsync(archivePath, package.Bytes, package.Sha256, cancellationToken).ConfigureAwait(false))
                throw new InvalidDataException("ModuleArchiveIntegrity");
            var extracted = OwnedPath(Path.Combine("staging", package.Id, package.Version, "extracted"));
            if (!DeleteTree(extracted)) throw new IOException("ModuleStagingCleanupPending");
            Directory.CreateDirectory(extracted);
            EnsureOwned(extracted);
            try
            {
                var manifest = await ExtractAsync(archivePath, extracted, package, installed, cancellationToken).ConfigureAwait(false);
                // Keep the pinned original archive so future launch verification need not trust a mutable receipt.
                await using (var source = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous))
                await using (var destination = new FileStream(Path.Combine(extracted, ArchiveName), FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                    await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                    destination.Flush(true);
                }
                await WriteJsonAsync(Path.Combine(extracted, RecordName), package, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                EnsureOwned(extracted);
                EnsureOwned(final);
                Directory.CreateDirectory(Path.GetDirectoryName(final)!);
                EnsureOwned(Path.GetDirectoryName(final)!);
                Directory.Move(extracted, final);
                return manifest;
            }
            catch { _ = DeleteTree(extracted); throw; }
        }
        finally { _gate.Release(); }
    }

    public Task<bool> RemoveVersionAsync(string id, string version, CancellationToken cancellationToken = default) =>
        RemoveTreeAsync(GetVersionDirectory(id, version), cancellationToken);
    public Task<bool> RemoveStagingAsync(string id, string version, CancellationToken cancellationToken = default)
    {
        Identity(id, version);
        return RemoveTreeAsync(OwnedPath(Path.Combine("staging", id, version)), cancellationToken);
    }
    public Task<bool> RemoveStagingAsync(string id, CancellationToken cancellationToken = default)
    {
        Identity(id);
        return RemoveTreeAsync(OwnedPath(Path.Combine("staging", id)), cancellationToken);
    }

    public async Task<Dictionary<string, string>> ReadSettingsAsync(string id, CancellationToken cancellationToken = default)
    {
        Identity(id);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var filename = OwnedPath(Path.Combine("data", id, "settings.json"));
            if (!File.Exists(filename)) return new(StringComparer.Ordinal);
            var values = await ReadJsonAsync<Dictionary<string, string>>(filename, MaximumSettingsBytes, cancellationToken).ConfigureAwait(false);
            ValidateSettings(values);
            return values;
        }
        finally { _gate.Release(); }
    }

    public async Task WriteSettingsAsync(string id, Dictionary<string, string> values, CancellationToken cancellationToken = default)
    {
        Identity(id); ValidateSettings(values);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var serialized = JsonSerializer.SerializeToUtf8Bytes(values, JsonOptions);
            if (serialized.Length > MaximumSettingsBytes) throw new InvalidDataException("ModuleSettingsTooLarge");
            await WriteJsonAsync(OwnedPath(Path.Combine("data", id, "settings.json")), values, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Explicit destructive operation; caller must have stopped this module first.</summary>
    public async Task ClearDataAsync(string id)
    {
        Identity(id);
        if (!await RemoveTreeAsync(OwnedPath(Path.Combine("data", id)), CancellationToken.None).ConfigureAwait(false))
            throw new IOException("ModuleDataCleanupPending");
    }

    private async Task<ModuleManifest> ReadInstalledAsync(string id, string version, CancellationToken token, OfficialModulePackage? expectedPackage = null)
    {
        var directory = GetVersionDirectory(id, version);
        var package = await ReadJsonAsync<OfficialModulePackage>(Path.Combine(directory, RecordName), MaximumStateBytes, token).ConfigureAwait(false);
        OfficialModuleCatalog.Require(package);
        if (package.Id != id || package.Version != version || expectedPackage is not null &&
            (package.Url != expectedPackage.Url || package.Bytes != expectedPackage.Bytes ||
             !package.Sha256.Equals(expectedPackage.Sha256, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("ModulePackageIdentityConflict");
        var archivePath = Path.Combine(directory, ArchiveName);
        EnsureOwned(archivePath);
        if (!await VerifyFileAsync(archivePath, package.Bytes, package.Sha256, token).ConfigureAwait(false))
            throw new InvalidDataException("ModuleArchiveIntegrity");
        using var archive = ZipFile.OpenRead(archivePath);
        var manifest = await ReadArchiveManifestAsync(archive, package, token).ConfigureAwait(false);
        var expected = manifest.Files.Select(item => item.Path).Append(ManifestName).Append(ArchiveName).Append(RecordName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actual = EnumerateSafeTree(directory).Where(File.Exists)
            .Select(file => Path.GetRelativePath(directory, file).Replace('\\', '/')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!expected.SetEquals(actual)) throw new InvalidDataException("ModuleInstalledInventory");
        var manifestBytes = await ReadBoundedAsync(Path.Combine(directory, ManifestName), MaximumManifestBytes, token).ConfigureAwait(false);
        await using var entryStream = archive.GetEntry(ManifestName)!.Open();
        var archiveManifestHash = await SHA256.HashDataAsync(entryStream, token).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(manifestBytes), archiveManifestHash))
            throw new InvalidDataException("ModuleManifestIntegrity");
        foreach (var file in manifest.Files)
            if (!await VerifyFileAsync(Path.Combine(directory, file.Path.Replace('/', Path.DirectorySeparatorChar)), file.Bytes, file.Sha256, token).ConfigureAwait(false))
                throw new InvalidDataException("ModuleFileIntegrity");
        return manifest;
    }

    private async Task<ModuleManifest> ExtractAsync(string archivePath, string directory, OfficialModulePackage package,
        IReadOnlyDictionary<string, ModuleInstallation> installed, CancellationToken token)
    {
        EnsureOwned(archivePath);
        using var archive = ZipFile.OpenRead(archivePath);
        var manifest = await ReadArchiveManifestAsync(archive, package, token).ConfigureAwait(false);
        ValidateCompatibility(manifest, installed);
        var files = manifest.Files.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.FullName.EndsWith('/')) continue;
            var output = ReparseSafePathPolicy.PrepareContainedFileDestination(directory, entry.FullName);
            EnsureOwned(output);
            await using (var source = entry.Open())
            await using (var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var limit = entry.FullName == ManifestName ? MaximumManifestBytes : files[entry.FullName].Bytes;
                var buffer = new byte[131072];
                long written = 0;
                int count;
                while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                {
                    written = checked(written + count);
                    if (written > limit) throw new InvalidDataException("ModuleExpandedSizeLimit");
                    await target.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                }
                if (written != entry.Length) throw new InvalidDataException("ModuleExpandedSizeMismatch");
                await target.FlushAsync(token).ConfigureAwait(false);
                target.Flush(true);
            }
            if (entry.FullName != ManifestName && !await VerifyFileAsync(output, files[entry.FullName].Bytes, files[entry.FullName].Sha256, token).ConfigureAwait(false))
                throw new InvalidDataException("ModuleFileIntegrity");
        }
        return manifest;
    }

    private async Task<ModuleManifest> ReadArchiveManifestAsync(ZipArchive archive, OfficialModulePackage package, CancellationToken token)
    {
        if (archive.Entries.Count is < 2 or > 257) throw new InvalidDataException("ModuleArchiveInventory");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            ValidatePayloadPath(entry.FullName, entry.FullName.EndsWith('/'));
            var kind = (entry.ExternalAttributes >> 16) & 0xF000;
            if (!names.Add(entry.FullName.TrimEnd('/')) || kind is not (0 or 0x8000 or 0x4000) ||
                kind == 0x4000 && !entry.FullName.EndsWith('/') || kind == 0x8000 && entry.FullName.EndsWith('/') ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || entry.Length < 0 ||
                entry.FullName.EndsWith('/') && entry.Length != 0)
                throw new InvalidDataException("ModuleArchiveUnsafeEntry");
        }
        var declaration = archive.GetEntry(ManifestName) ?? throw new InvalidDataException("ModuleManifestMissing");
        if (declaration.Length is <= 0 or > MaximumManifestBytes) throw new InvalidDataException("ModuleManifestTooLarge");
        await using var stream = declaration.Open();
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (memory.Length + count > MaximumManifestBytes) throw new InvalidDataException("ModuleManifestTooLarge");
            memory.Write(buffer, 0, count);
        }
        RejectDuplicateJson(memory.ToArray());
        var manifest = JsonSerializer.Deserialize<ModuleManifest>(memory.ToArray(), ManifestJsonOptions) ?? throw new InvalidDataException("ModuleManifestInvalid");
        if (manifest.Id != package.Id || manifest.Version != package.Version) throw new InvalidDataException("ModuleManifestIdentity");
        if (manifest.Files is null || manifest.Data is null) throw new InvalidDataException("ModuleManifestInvalid");
        var files = new Dictionary<string, ModuleFile>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in manifest.Files)
        {
            ValidatePayloadPath(file.Path, false);
            if (file.Path.Equals(ManifestName, StringComparison.OrdinalIgnoreCase) || file.Path.Equals(ArchiveName, StringComparison.OrdinalIgnoreCase) ||
                file.Path.Equals(RecordName, StringComparison.OrdinalIgnoreCase) ||
                !files.TryAdd(file.Path, file) || file.Bytes is < 0 or > MaximumExpandedBytes || !ValidHash(file.Sha256))
                throw new InvalidDataException("ModuleFileDeclaration");
            total = checked(total + file.Bytes);
            if (total > MaximumExpandedBytes) throw new InvalidDataException("ModuleExpandedSizeLimit");
        }
        if (files.Count is < 1 or > 128) throw new InvalidDataException("ModuleFileDeclaration");
        var actual = archive.Entries.Where(entry => !entry.FullName.EndsWith('/') && entry.FullName != ManifestName).ToArray();
        if (actual.Length != files.Count || actual.Any(entry => !files.TryGetValue(entry.FullName, out var file) || entry.Length != file.Bytes) ||
            archive.Entries.Any(entry => entry.FullName.EndsWith('/') && !files.Keys.Any(name => name.StartsWith(entry.FullName, StringComparison.Ordinal))))
            throw new InvalidDataException("ModuleArchiveInventory");
        return manifest;
    }

    private static void ValidateCompatibility(ModuleManifest manifest, IReadOnlyDictionary<string, ModuleInstallation> installed)
    {
        ModuleContract.Validate(manifest, installed, Environment.OSVersion.Version.Build);
        // V1 has no migration executor. A new binary must keep the existing data format
        // exactly; neither activation nor rollback may silently reinterpret user data.
        if (installed.TryGetValue(manifest.Id, out var previous))
        {
            if (previous.ErrorCode == "ModuleStateInvalid") throw new InvalidDataException("ModuleStateInvalid");
            if (previous.DataVersion > 0 && previous.DataVersion != manifest.Data.Version)
                throw new InvalidDataException("ModuleDataMigrationUnsupported");
        }
    }

    private static void ValidateState(ModuleInstallation installation)
    {
        Identity(installation.Id);
        if (installation.Version is not null) Identity(installation.Id, installation.Version);
        if (installation.PreviousVersion is not null) Identity(installation.Id, installation.PreviousVersion);
        if (installation.CandidateVersion is not null) Identity(installation.Id, installation.CandidateVersion);
        if (installation.DataVersion < 0 || !Enum.IsDefined(installation.Transaction) || installation.ErrorCode?.Length > 256)
            throw new InvalidDataException("ModuleStateInvalid");
    }
    private static void ValidateSettings(Dictionary<string, string> values)
    {
        if (values.Count > 32 || values.Any(pair => string.IsNullOrEmpty(pair.Key) || !ModuleContract.ValidId(pair.Key) || pair.Value is null || pair.Value.Length > 4096))
            throw new InvalidDataException("ModuleSettingsInvalid");
    }
    internal static bool ValidHash(string? value) => value?.Length == 64 && value.All(Uri.IsHexDigit);
    private static void Identity(string id, string? version = null)
    {
        if (string.IsNullOrEmpty(id) || !ModuleContract.ValidId(id) || id.Split('.').Any(piece => piece.Length == 0) ||
            version is not null && !ModuleContract.ValidVersion(version)) throw new InvalidDataException("ModuleIdentityInvalid");
        ValidatePayloadPath(id + ".json", false);
    }
    private static void ValidatePayloadPath(string value, bool directory)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 240 || value.Contains('\\') || value.StartsWith('/') ||
            value.Any(c => char.IsControl(c) || ":*?<>|\"".Contains(c))) throw new InvalidDataException("ModulePayloadPathInvalid");
        var trimmed = directory ? value[..^1] : value;
        foreach (var segment in trimmed.Split('/'))
        {
            var device = segment.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (segment is "" or "." or ".." || segment != segment.TrimEnd(' ', '.') ||
                device is "CON" or "PRN" or "AUX" or "NUL" || device.Length == 4 &&
                (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)) &&
                (device[3] is >= '1' and <= '9' or '\u00B9' or '\u00B2' or '\u00B3'))
                throw new InvalidDataException("ModulePayloadPathInvalid");
        }
        if (!directory && Path.GetExtension(value).ToLowerInvariant() is not (".exe" or ".dll" or ".json" or ".txt" or ".md" or ".dat" or ".bin" or ".png" or ".ico"))
            throw new InvalidDataException("ModulePayloadTypeUnsupported");
    }

    private string OwnedPath(string relative)
    {
        var full = Path.GetFullPath(Path.Combine(_root, relative));
        if (full != _root && !full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ModulePathEscaped");
        EnsureOwned(full);
        return full;
    }
    private void EnsureOwned(string full)
    {
        full = Path.GetFullPath(full);
        if (full != _root && !full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ModulePathEscaped");
        // Validate every existing ancestor, including ancestors above our storage root.
        for (var current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("ModuleReparsePath"); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    private IReadOnlyList<string> EnumerateSafeTree(string directory)
    {
        EnsureOwned(directory);
        var result = new List<string>();
        var pending = new Stack<string>(); pending.Push(directory);
        while (pending.TryPop(out var current))
        {
            EnsureOwned(current);
            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                EnsureOwned(entry); result.Add(entry);
                if (Directory.Exists(entry)) pending.Push(entry);
            }
        }
        return result;
    }
    private bool DeleteTree(string directory)
    {
        try
        {
            EnsureOwned(directory);
            if (!Directory.Exists(directory)) return !File.Exists(directory);
            var entries = EnumerateSafeTree(directory);
            // Validate the complete tree before deleting anything; never follow reparse entries.
            foreach (var entry in entries.OrderByDescending(item => item.Length))
            {
                EnsureOwned(entry);
                if (Directory.Exists(entry)) Directory.Delete(entry, false); else File.Delete(entry);
            }
            EnsureOwned(directory); Directory.Delete(directory, false);
            return !Directory.Exists(directory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { return false; }
    }
    private async Task<bool> RemoveTreeAsync(string directory, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { return await Task.Run(() => DeleteTree(directory), token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
    private async Task<bool> VerifyFileAsync(string file, long bytes, string sha256, CancellationToken token)
    {
        EnsureOwned(file);
        if (!File.Exists(file) || new FileInfo(file).Length != bytes) return false;
        await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous);
        return CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(input, token).ConfigureAwait(false), Convert.FromHexString(sha256));
    }
    private async Task<byte[]> ReadBoundedAsync(string file, int maximum, CancellationToken token)
    {
        EnsureOwned(file);
        await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
        if (input.Length > maximum) throw new InvalidDataException("ModuleJsonTooLarge");
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (memory.Length + count > maximum) throw new InvalidDataException("ModuleJsonTooLarge");
            memory.Write(buffer, 0, count);
        }
        return memory.ToArray();
    }
    private async Task<T> ReadJsonAsync<T>(string file, int maximum, CancellationToken token)
    {
        var bytes = await ReadBoundedAsync(file, maximum, token).ConfigureAwait(false);
        RejectDuplicateJson(bytes);
        return JsonSerializer.Deserialize<T>(bytes, JsonOptions) ?? throw new InvalidDataException("ModuleJsonInvalid");
    }
    private async Task<ModuleInstallation> ReadStateAsync(string file, string id, CancellationToken token)
    {
        var installation = await ReadJsonAsync<ModuleInstallation>(file, MaximumStateBytes, token).ConfigureAwait(false);
        ValidateState(installation);
        if (installation.Id != id) throw new InvalidDataException("ModuleStateIdentityMismatch");
        return installation;
    }
    private async Task WriteJsonAsync<T>(string file, T value, CancellationToken token)
    {
        EnsureOwned(file);
        var destination = ReparseSafePathPolicy.PrepareContainedFileDestination(_root, Path.GetRelativePath(_root, file));
        EnsureOwned(destination); EnsureOwned(destination + ".tmp");
        await DownloadStorage.WriteAsync(destination, value, token).ConfigureAwait(false);
    }
    private static void RejectDuplicateJson(byte[] bytes)
    {
        var reader = new Utf8JsonReader(bytes);
        var scopes = new Stack<HashSet<string>?>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject) scopes.Push(new(StringComparer.OrdinalIgnoreCase));
            else if (reader.TokenType == JsonTokenType.StartArray) scopes.Push(null);
            else if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray) scopes.Pop();
            else if (reader.TokenType == JsonTokenType.PropertyName && scopes.Peek() is { } keys && !keys.Add(reader.GetString()!))
                throw new InvalidDataException("ModuleDuplicateJsonKey");
        }
    }
    public void Dispose() { _client.Dispose(); _gate.Dispose(); }
}
