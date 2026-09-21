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
            File.Delete(path);
        }
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: false);
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
