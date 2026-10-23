using System.Text.Json;
using System.Text.Json.Nodes;

namespace DropSpace.Core.Models;

public sealed record RelayDirectorySetting(string Path, bool IsPaused = false);
public sealed record RelayDirectoryStatus(string Path, bool IsPaused, bool IsAvailable, string? Reason);

public sealed record RecentFileSettings
{
    // No directory is subscribed until the user explicitly selects one.
    public IReadOnlyList<RelayDirectorySetting> Directories { get; init; } = [];
    public int RetentionDays { get; init; } = 30;
    public int CountLimit { get; init; } = 500;

    public RecentFileSettings Validate() => this with
    {
        Directories = (Directories ?? []).Where(value => value is not null &&
            !string.IsNullOrWhiteSpace(value.Path) && value.Path.Length <= 32_767)
            .DistinctBy(value => value.Path.TrimEnd('\\', '/'), StringComparer.OrdinalIgnoreCase)
            .Take(16).ToArray(),
        RetentionDays = Math.Clamp(RetentionDays, 1, 365),
        CountLimit = Math.Clamp(CountLimit, 10, 5_000),
    };
}

/// <summary>Lives in the existing item's metadata; preserving that item's other metadata is mandatory.</summary>
public sealed record RecentItemMetadata(string? WatchDirectory, string? Operation, bool IsOutput, bool IsPartial)
{
    public static RecentItemMetadata? Read(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return null;
        try
        {
            return JsonNode.Parse(metadataJson)?["recent"]?.Deserialize<RecentItemMetadata>();
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    public static string Merge(string? metadataJson, RecentItemMetadata incoming)
    {
        JsonObject metadata;
        try { metadata = JsonNode.Parse(metadataJson ?? "{}") as JsonObject ?? new JsonObject(); }
        catch (JsonException) { metadata = new JsonObject(); }
        var existing = Read(metadataJson);
        var merged = new RecentItemMetadata(
            incoming.WatchDirectory ?? existing?.WatchDirectory,
            incoming.Operation ?? existing?.Operation,
            incoming.IsOutput || existing?.IsOutput == true,
            incoming.IsOutput ? incoming.IsPartial : existing?.IsPartial == true);
        metadata["recent"] = JsonSerializer.SerializeToNode(merged);
        return metadata.ToJsonString();
    }
}
