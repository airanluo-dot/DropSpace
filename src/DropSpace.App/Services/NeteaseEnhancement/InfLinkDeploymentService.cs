using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace DropSpace.App.Services.NeteaseEnhancement;

public sealed record PreparedInfLinkDeployment(NeteaseInstallation Installation, string ProfilePath, string LoaderPath, string PluginPath, string LoaderHash, string PluginHash, string PluginVersion);
public sealed record InfLinkManagedFile(string TargetPath, string InstalledHash, string? PreviousHash, string? BackupName, bool Owned = true);
public sealed record InfLinkDeploymentReceipt(string TransactionId, NeteaseInstallation Installation, string ProfilePath, string PluginVersion, bool Committed, IReadOnlyList<InfLinkManagedFile> Files);

public sealed partial class InfLinkDeploymentService : IDisposable
{
    private const int MaximumAssetBytes = 32 * 1024 * 1024;
    private readonly string stateRoot;
    private readonly HttpClient client;
    private readonly SemaphoreSlim gate = new(1, 1);
    public InfLinkDeploymentService(string? stateRoot = null, HttpMessageHandler? handler = null)
    {
        this.stateRoot = stateRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DropSpace", "NeteaseEnhancement");
        client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(2) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DropSpace-NeteaseEnhancement/1.0");
    }

    public async Task<PreparedInfLinkDeployment> PrepareAsync(NeteaseInstallation installation, CancellationToken cancellationToken = default)
    {
        if (installation.Architecture is not (Architecture.X64 or Architecture.X86)) throw new EnhancementDeploymentException("UnsupportedArchitecture");
        var existing = await new BetterNcmProbe().FindAsync(installation, cancellationToken).ConfigureAwait(false);
        if (!existing.VcRuntimeAvailable) throw new EnhancementDeploymentException("UnsupportedPrerequisite");
        byte[] metadata = await DownloadAsync(new Uri("https://api.github.com/repos/apoint123/inflink-rs/releases/latest"), 1024 * 1024, cancellationToken).ConfigureAwait(false);
        using var release = JsonDocument.Parse(metadata);
        if (release.RootElement.GetProperty("draft").GetBoolean() || release.RootElement.GetProperty("prerelease").GetBoolean()) throw new EnhancementDeploymentException("InvalidRelease");
        string version = release.RootElement.GetProperty("tag_name").GetString() ?? throw new EnhancementDeploymentException("InvalidRelease");
        if (!Version.TryParse(version.TrimStart('v'), out _)) throw new EnhancementDeploymentException("InvalidRelease");
        var asset = release.RootElement.GetProperty("assets").EnumerateArray().Single(a => a.GetProperty("name").GetString() == "InfLink-rs.plugin");
        string digest = asset.GetProperty("digest").GetString() ?? throw new EnhancementDeploymentException("MissingDigest");
        if (!digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71 || digest[7..].Any(c => !char.IsAsciiHexDigit(c))) throw new EnhancementDeploymentException("InvalidDigest");
        string pluginHash = digest[7..];
        var pluginUrl = new Uri(asset.GetProperty("browser_download_url").GetString()!);
        if (pluginUrl.Host != "github.com" || !pluginUrl.AbsolutePath.StartsWith("/apoint123/inflink-rs/releases/download/", StringComparison.Ordinal)) throw new EnhancementDeploymentException("UntrustedDownload");
        long size = asset.GetProperty("size").GetInt64();
        if (size <= 0 || size > MaximumAssetBytes) throw new EnhancementDeploymentException("AssetTooLarge");
        bool x64 = installation.Architecture == Architecture.X64;
        string loaderHash = x64 ? "a7c77af418d7940e63faa58ea036fba1f4baad497947109ea52ed78c8e86608f" : "b7be606f353c5ca36235ff1f4dfccde62dc459a4b07a506698d663a1953864f2";
        string directory = Path.Combine(stateRoot, "prepared", Guid.NewGuid().ToString("N"));
        DeploymentPaths.AssertSafe(directory);
        string preparedRoot = Path.GetDirectoryName(directory)!;
        if (Directory.Exists(preparedRoot) && Directory.EnumerateDirectories(preparedRoot).Take(5).Count() >= 4) throw new EnhancementDeploymentException("PreparedCacheFull");
        Directory.CreateDirectory(directory);
        string loader = Path.Combine(directory, "loader.dll"), plugin = Path.Combine(directory, "InfLink-rs.plugin");
        try
        {
        await DownloadVerifiedAsync(new Uri("https://github.com/std-microblock/chromatic/releases/download/1.3.4/" + (x64 ? "BetterNCMII.dll" : "BetterNCMII86.dll")), loader, loaderHash, cancellationToken).ConfigureAwait(false);
        await DownloadVerifiedAsync(pluginUrl, plugin, pluginHash, cancellationToken).ConfigureAwait(false);
        if (new FileInfo(plugin).Length != size) throw new EnhancementDeploymentException("InvalidAssetSize");
        ValidatePluginArchive(plugin, version);
        return new(installation, existing.ProfilePath, loader, plugin, loaderHash, pluginHash, version);
        }
        catch { DeletePreparedDirectory(directory); throw; }
    }

    public Task DiscardPreparedAsync(PreparedInfLinkDeployment prepared, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string directory = Path.GetDirectoryName(prepared.LoaderPath)!;
        if (!string.Equals(Path.GetDirectoryName(prepared.PluginPath), directory, StringComparison.OrdinalIgnoreCase)) throw new EnhancementDeploymentException("UnsafePath");
        DeletePreparedDirectory(directory);
        return Task.CompletedTask;
    }

    public async Task<InfLinkDeploymentReceipt?> GetManagedReceiptAsync(NeteaseInstallation installation, CancellationToken cancellationToken = default)
    {
        string path = ReceiptPath(installation);
        DeploymentPaths.AssertSafe(path);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 65536) throw new EnhancementDeploymentException("InvalidReceipt");
        var receipt = JsonSerializer.Deserialize<InfLinkDeploymentReceipt>(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)) ?? throw new EnhancementDeploymentException("InvalidReceipt");
        ValidateReceipt(receipt, installation);
        return receipt;
    }

    public async Task<InfLinkDeploymentReceipt> InstallAsync(PreparedInfLinkDeployment prepared, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AssertStopped(prepared.Installation);
            var previous = await GetManagedReceiptAsync(prepared.Installation, cancellationToken).ConfigureAwait(false);
            if (previous is { Committed: false }) throw new EnhancementDeploymentException("PendingTransaction");
            if (previous is not null && !string.Equals(previous.ProfilePath, prepared.ProfilePath, StringComparison.OrdinalIgnoreCase)) throw new EnhancementDeploymentException("ProfileChanged");
            string loader = Path.Combine(Path.GetDirectoryName(prepared.Installation.ExecutablePath)!, "msimg32.dll");
            string plugin = Path.Combine(prepared.ProfilePath, "plugins", "InfLink-rs.plugin");
            AssertNoConflictingPlugins(prepared.ProfilePath, plugin);
            string transaction = Guid.NewGuid().ToString("N");
            string backupRoot = Path.Combine(stateRoot, "backups", transaction);
            DeploymentPaths.AssertSafe(backupRoot);
            Directory.CreateDirectory(backupRoot);
            var files = new List<InfLinkManagedFile>();
            foreach (var pair in new[] { (loader, prepared.LoaderPath, prepared.LoaderHash), (plugin, prepared.PluginPath, prepared.PluginHash) })
            {
                cancellationToken.ThrowIfCancellationRequested();
                DeploymentPaths.AssertSafe(pair.Item1);
                DeploymentPaths.AssertSafe(pair.Item2);
                if (!HashEquals(await HashAsync(pair.Item2, cancellationToken).ConfigureAwait(false), pair.Item3)) throw new EnhancementDeploymentException("HashMismatch");
                string? oldHash = File.Exists(pair.Item1) ? await HashAsync(pair.Item1, cancellationToken).ConfigureAwait(false) : null;
                var owned = previous?.Files.SingleOrDefault(f => string.Equals(f.TargetPath, pair.Item1, StringComparison.OrdinalIgnoreCase));
                if (oldHash is not null && (owned is null || !owned.Owned))
                {
                    if (!HashEquals(oldHash, pair.Item3)) throw new EnhancementDeploymentException("ExistingUnmanagedFile");
                    files.Add(new(pair.Item1, pair.Item3, oldHash, null, false));
                    continue;
                }
                if (oldHash is not null && !HashEquals(oldHash, owned?.InstalledHash)) throw new EnhancementDeploymentException("ExistingUnmanagedFile");
                string? backup = oldHash is null ? null : files.Count + ".bak";
                if (backup is not null) File.Copy(pair.Item1, Path.Combine(backupRoot, backup));
                files.Add(new(pair.Item1, pair.Item3, oldHash, backup));
            }
            if (previous is not null) await File.WriteAllTextAsync(Path.Combine(backupRoot, "previous.json"), JsonSerializer.Serialize(previous), cancellationToken).ConfigureAwait(false);
            var receipt = new InfLinkDeploymentReceipt(transaction, prepared.Installation, prepared.ProfilePath, prepared.PluginVersion, false, files);
            await SaveReceiptAsync(receipt, cancellationToken).ConfigureAwait(false);
            try
            {
                if (files[0].Owned) await ReplaceAsync(prepared.LoaderPath, loader, cancellationToken).ConfigureAwait(false);
                if (files[1].Owned) await ReplaceAsync(prepared.PluginPath, plugin, cancellationToken).ConfigureAwait(false);
                return receipt;
            }
            catch
            {
                await RestoreFilesAsync(receipt, CancellationToken.None).ConfigureAwait(false);
                if (previous is null) File.Delete(ReceiptPath(prepared.Installation));
                else await SaveReceiptAsync(previous, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally { gate.Release(); }
    }

    public async Task CommitAsync(InfLinkDeploymentReceipt receipt, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AssertCurrentAsync(receipt, cancellationToken).ConfigureAwait(false);
            foreach (var file in receipt.Files)
                if (!HashEquals(await HashAsync(file.TargetPath, cancellationToken).ConfigureAwait(false), file.InstalledHash)) throw new EnhancementDeploymentException("ManagedFileChanged");
            await SaveReceiptAsync(receipt with { Committed = true }, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task RollbackAsync(InfLinkDeploymentReceipt receipt, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AssertStopped(receipt.Installation);
            await AssertCurrentAsync(receipt, cancellationToken).ConfigureAwait(false);
            string priorPath = Path.Combine(stateRoot, "backups", receipt.TransactionId, "previous.json");
            DeploymentPaths.AssertSafe(priorPath);
            InfLinkDeploymentReceipt? prior = null;
            if (File.Exists(priorPath))
            {
                if (new FileInfo(priorPath).Length > 65536) throw new EnhancementDeploymentException("InvalidReceipt");
                prior = JsonSerializer.Deserialize<InfLinkDeploymentReceipt>(await File.ReadAllTextAsync(priorPath, cancellationToken).ConfigureAwait(false)) ?? throw new EnhancementDeploymentException("InvalidReceipt");
                ValidateReceipt(prior, receipt.Installation);
            }
            await RestoreFilesAsync(receipt, cancellationToken).ConfigureAwait(false);
            if (prior is null) File.Delete(ReceiptPath(receipt.Installation));
            else await SaveReceiptAsync(prior, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task RemoveAsync(NeteaseInstallation installation, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AssertStopped(installation);
            var receipt = await GetManagedReceiptAsync(installation, cancellationToken).ConfigureAwait(false);
            if (receipt is null) throw new EnhancementDeploymentException("NotManaged");
            foreach (var file in receipt.Files)
                if (File.Exists(file.TargetPath) && !HashEquals(await HashAsync(file.TargetPath, cancellationToken).ConfigureAwait(false), file.InstalledHash)) throw new EnhancementDeploymentException("ManagedFileChanged");
            foreach (var file in receipt.Files.Where(f => f.Owned))
            {
                DeploymentPaths.AssertSafe(file.TargetPath);
                await RetryFileMutationAsync(() => File.Delete(file.TargetPath), cancellationToken).ConfigureAwait(false);
            }
            File.Delete(ReceiptPath(installation));
        }
        finally { gate.Release(); }
    }

    public void Dispose() { client.Dispose(); gate.Dispose(); }
}
