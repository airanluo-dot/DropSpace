using System.IO.Compression;
using System.Text.Json;

namespace DropSpace.App.Services.NeteaseEnhancement;

/// <summary>Read-only inventory and release metadata; never downloads or installs a package.</summary>
internal static class NeteaseUpdateProbe
{
    internal static string? ReadInstalledVersion()
    {
        try
        {
            var path = Path.Combine(BetterNcmProbe.ResolveProfilePath(), "plugins", "InfLink-rs.plugin");
            DeploymentPaths.AssertSafe(path);
            if (!File.Exists(path) || new FileInfo(path).Length > 32 * 1024 * 1024) return null;
            using var archive = ZipFile.OpenRead(path);
            var entry = archive.GetEntry("manifest.json");
            if (entry is null || entry.Length > 65536) return null;
            using var stream = entry.Open();
            using var json = JsonDocument.Parse(stream);
            var root = json.RootElement;
            if (root.GetProperty("name").GetString() != "InfLink-rs") return null;
            var value = root.GetProperty("version").GetString();
            return Version.TryParse(value, out _) ? value : null;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
    }

    internal static async Task<string?> ReadLatestVersionAsync(CancellationToken token)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = 1024 * 1024 };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DropSpace-NeteaseEnhancement/1.0");
        using var response = await client.GetAsync("https://api.github.com/repos/apoint123/inflink-rs/releases/latest", token);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var root = json.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var value = root.GetProperty("tag_name").GetString()?.TrimStart('v');
        return Version.TryParse(value, out _) ? value : null;
    }
}
