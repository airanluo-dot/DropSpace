using System.IO.Compression;
using System.Text.Json;

namespace DropSpace.App.Services.NeteaseEnhancement;

public sealed partial class InfLinkDeploymentService
{
    private void DeletePreparedDirectory(string directory)
    {
        string parent = Path.Combine(stateRoot, "prepared");
        if (!string.Equals(Path.GetDirectoryName(directory), parent, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) throw new EnhancementDeploymentException("UnsafePath");
        DeploymentPaths.AssertSafe(directory);
        foreach (string name in new[] { "loader.dll", "InfLink-rs.plugin" })
        {
            string path = Path.Combine(directory, name);
            DeploymentPaths.AssertSafe(path);
            DropSpace.Infrastructure.Downloads.HttpRangeDownloader.DeleteStagingFiles(path + ".download");
            File.Delete(path);
        }
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: false);
    }

    // Called only under the deployment gate, after receipt publication or before
    // creating a new transaction. Keep the current committed generation and both
    // generations needed to recover an uncommitted replacement.
    private async Task RetireUnusedBackupsAsync()
    {
        const int maximumReceipts = 256, maximumDirectories = 256;
        try
        {
            DeploymentPaths.AssertSafe(stateRoot);
            string backups = Path.Combine(stateRoot, "backups");
            DeploymentPaths.AssertSafe(backups);
            if (!Directory.Exists(backups)) return;
            var receiptPaths = Directory.EnumerateFiles(stateRoot, "*.json")
                .Where(path => Path.GetFileNameWithoutExtension(path) is { Length: 64 } name && name.All(char.IsAsciiHexDigit))
                .Take(maximumReceipts + 1).ToArray();
            if (receiptPaths.Length > maximumReceipts) return;
            var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in receiptPaths)
            {
                DeploymentPaths.AssertSafe(path);
                if (new FileInfo(path).Length > 65536) throw new EnhancementDeploymentException("InvalidReceipt");
                var receipt = JsonSerializer.Deserialize<InfLinkDeploymentReceipt>(await File.ReadAllTextAsync(path).ConfigureAwait(false))
                    ?? throw new EnhancementDeploymentException("InvalidReceipt");
                ValidateReceipt(receipt, receipt.Installation);
                if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(ReceiptPath(receipt.Installation)), StringComparison.OrdinalIgnoreCase))
                    throw new EnhancementDeploymentException("InvalidReceipt");
                retained.Add(receipt.TransactionId);
                if (receipt.Committed) continue;
                string priorPath = Path.Combine(backups, receipt.TransactionId, "previous.json");
                DeploymentPaths.AssertSafe(priorPath);
                if (!File.Exists(priorPath))
                {
                    if (receipt.Files.Any(file => file.BackupName is not null))
                        throw new EnhancementDeploymentException("InvalidReceipt");
                    continue;
                }
                if (new FileInfo(priorPath).Length > 65536) throw new EnhancementDeploymentException("InvalidReceipt");
                var prior = JsonSerializer.Deserialize<InfLinkDeploymentReceipt>(await File.ReadAllTextAsync(priorPath).ConfigureAwait(false))
                    ?? throw new EnhancementDeploymentException("InvalidReceipt");
                ValidateReceipt(prior, receipt.Installation);
                // Install admits only committed predecessors. Unexpected history
                // is left intact rather than guessing which recovery files to keep.
                if (!prior.Committed) throw new EnhancementDeploymentException("InvalidReceipt");
                retained.Add(prior.TransactionId);
            }

            foreach (string directory in Directory.EnumerateDirectories(backups).Take(maximumDirectories + retained.Count))
            {
                string transaction = Path.GetFileName(directory);
                if (!Guid.TryParseExact(transaction, "N", out _) || retained.Contains(transaction)) continue;
                try
                {
                    DeploymentPaths.AssertSafe(directory);
                    var children = Directory.EnumerateFileSystemEntries(directory).Take(4).ToArray();
                    if (children.Length > 3 || children.Any(path => Path.GetFileName(path) is not ("0.bak" or "1.bak" or "previous.json"))) continue;
                    // Validate every fixed child before deleting any. Unknown files,
                    // directories and reparse points never become cleanup targets.
                    foreach (string path in children)
                    {
                        DeploymentPaths.AssertSafe(path);
                        if (!File.Exists(path)) throw new EnhancementDeploymentException("UnsafePath");
                    }
                    foreach (string path in children)
                    {
                        DeploymentPaths.AssertSafe(path);
                        File.Delete(path);
                    }
                    DeploymentPaths.AssertSafe(directory);
                    Directory.Delete(directory, recursive: false);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or EnhancementDeploymentException)
                { System.Diagnostics.Trace.TraceWarning("NetEase backup retirement remains retryable ({0}).", error.GetType().Name); }
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Receipt uncertainty or maintenance failure must never undo a completed
            // commit or remove files still needed by a pending recovery transaction.
            System.Diagnostics.Trace.TraceWarning("NetEase backup inventory retirement skipped ({0}).", error.GetType().Name);
        }
    }

    private static void ValidatePluginArchive(string path, string expectedVersion)
    {
        using var archive = ZipFile.OpenRead(path);
        long expandedSize = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            expandedSize += entry.Length;
            if (expandedSize > MaximumAssetBytes || !names.Add(entry.FullName) || entry.FullName.StartsWith('/') || entry.FullName.Contains('\\') || entry.FullName.Contains(':') || entry.FullName.Split('/').Any(part => part is ".." or ".")) throw new EnhancementDeploymentException("InvalidPlugin");
        }
        using var manifest = ReadManifest(archive);
        var root = manifest.RootElement;
        if (root.GetProperty("name").GetString() != "InfLink-rs" || root.GetProperty("version").GetString() != expectedVersion.TrimStart('v') ||
            root.GetProperty("native_plugin").GetString() != "backend.dll" || archive.GetEntry("backend.dll") is null || archive.GetEntry("backend.dll.x64.dll") is null || archive.GetEntry("index.js") is null)
            throw new EnhancementDeploymentException("InvalidPlugin");
    }

    private static JsonDocument ReadManifest(ZipArchive archive)
    {
        if (archive.Entries.Count > 256) throw new EnhancementDeploymentException("InvalidPlugin");
        var entry = archive.GetEntry("manifest.json") ?? throw new EnhancementDeploymentException("InvalidPlugin");
        if (entry.Length > 65536) throw new EnhancementDeploymentException("InvalidPlugin");
        using var stream = entry.Open();
        return JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 32 });
    }

    private static void AssertNoConflictingPlugins(string profile, string target)
    {
        foreach (string folder in new[] { "plugins", "plugins_dev" })
        {
            string directory = Path.Combine(profile, folder);
            DeploymentPaths.AssertSafe(directory);
            if (!Directory.Exists(directory)) continue;
            string[] entries = Directory.EnumerateFileSystemEntries(directory).Take(513).ToArray();
            if (entries.Length > 512) throw new EnhancementDeploymentException("PluginScanLimit");
            foreach (string path in entries)
            {
                DeploymentPaths.AssertSafe(path);
                if (string.Equals(path, target, StringComparison.OrdinalIgnoreCase)) continue;
                if (Path.GetFileName(path).Contains("InfLink", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path).Contains("InfinityLink", StringComparison.OrdinalIgnoreCase)) throw new EnhancementDeploymentException("ConflictingPlugin");
                try
                {
                    JsonDocument? manifest = null;
                    if (Directory.Exists(path))
                    {
                        string manifestPath = Path.Combine(path, "manifest.json");
                        DeploymentPaths.AssertSafe(manifestPath);
                        if (File.Exists(manifestPath) && new FileInfo(manifestPath).Length <= 65536) manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
                    }
                    else if (string.Equals(Path.GetExtension(path), ".plugin", StringComparison.OrdinalIgnoreCase))
                    {
                        if (new FileInfo(path).Length > MaximumAssetBytes) throw new EnhancementDeploymentException("PluginScanLimit");
                        using var archive = ZipFile.OpenRead(path);
                        if (archive.GetEntry("manifest.json") is not null) manifest = ReadManifest(archive);
                    }
                    using (manifest)
                    {
                        if (manifest is not null && (IsInfLink(manifest.RootElement, "name") || IsInfLink(manifest.RootElement, "fork_of"))) throw new EnhancementDeploymentException("ConflictingPlugin");
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or JsonException) { throw new EnhancementDeploymentException("PluginInspectionFailed"); }
            }
        }
    }
    private static bool IsInfLink(JsonElement manifest, string property) => manifest.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && (value.GetString()?.Contains("InfLink", StringComparison.OrdinalIgnoreCase) == true || value.GetString()?.Contains("InfinityLink", StringComparison.OrdinalIgnoreCase) == true);
}
