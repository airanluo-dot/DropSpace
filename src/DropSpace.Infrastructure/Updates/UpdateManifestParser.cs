using System.Text.Json;
using System.Text.Json.Serialization;
using DropSpace.Core.Updates;

namespace DropSpace.Infrastructure.Updates;

public sealed class UpdateManifestParser
{
    public const int MaximumManifestBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public UpdateManifest ParseAndValidate(ReadOnlyMemory<byte> bytes, UpdateRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (bytes.IsEmpty || bytes.Length > MaximumManifestBytes)
        {
            throw new InvalidDataException("The update manifest is empty or exceeds 64 KiB.");
        }

        ManifestDto dto;
        try
        {
            dto = JsonSerializer.Deserialize<ManifestDto>(bytes.Span, Options) ??
                throw new InvalidDataException("The update manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The update manifest is malformed or contains unsupported fields.", exception);
        }

        if (dto.SchemaVersion != 1)
        {
            throw new InvalidDataException("The update manifest schema is unsupported.");
        }

        if (!ReleaseVersion.TryParse(dto.Version, out var version) ||
            !ReleaseVersion.TryParse(release.TagName, out var tagVersion) ||
            version != tagVersion)
        {
            throw new InvalidDataException("The release tag and update manifest version do not match.");
        }

        var expectedChannel = version.IsPreview ? UpdateChannel.Preview : UpdateChannel.Stable;
        if (!Enum.TryParse<UpdateChannel>(dto.Channel, ignoreCase: true, out var channel) ||
            channel != expectedChannel || release.IsPrerelease != version.IsPreview)
        {
            throw new InvalidDataException("The update channel does not match the release version.");
        }

        if (dto.VersionCode != version.ToVersionCode() || dto.MinimumWindowsBuild < 26_100)
        {
            throw new InvalidDataException("The update manifest version code or Windows requirement is invalid.");
        }

        if (dto.PublishedAt.Offset != TimeSpan.Zero || dto.PublishedAt.Year < 2025 ||
            dto.PublishedAt > DateTimeOffset.UtcNow.AddDays(1))
        {
            throw new InvalidDataException("The update publication timestamp is invalid.");
        }

        if (dto.Summary is { Length: > 500 })
        {
            throw new InvalidDataException("The update summary exceeds the supported length.");
        }

        var installer = ValidateAsset(dto.Installer, "DropSpaceSetup.exe", release);
        var portable = ValidateAsset(dto.Portable, "DropSpace.exe", release);
        var manifestAssets = release.Assets.Count(asset =>
            string.Equals(asset.Name, "update-manifest.json", StringComparison.Ordinal));
        if (manifestAssets != 1)
        {
            throw new InvalidDataException("The release must contain exactly one update-manifest.json asset.");
        }

        var unexpectedExecutables = release.Assets.Where(asset =>
            asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(asset.Name, installer.AssetName, StringComparison.Ordinal) &&
            !string.Equals(asset.Name, portable.AssetName, StringComparison.Ordinal)).ToArray();
        if (unexpectedExecutables.Length != 0)
        {
            throw new InvalidDataException("The release contains an unexpected executable asset.");
        }

        if (release.HtmlUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(release.HtmlUri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The release notes URL is not an official HTTPS GitHub URL.");
        }

        return new UpdateManifest(
            dto.SchemaVersion,
            channel,
            version,
            dto.VersionCode,
            dto.PublishedAt,
            dto.MinimumWindowsBuild,
            dto.Mandatory,
            dto.Summary,
            installer,
            portable);
    }

    private static UpdateManifestAsset ValidateAsset(
        ManifestAssetDto? dto,
        string requiredName,
        UpdateRelease release)
    {
        if (dto is null || !string.Equals(dto.AssetName, requiredName, StringComparison.Ordinal) || dto.Size <= 0 ||
            string.IsNullOrWhiteSpace(dto.Sha256) || dto.Sha256.Length != 64 ||
            !dto.Sha256.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new InvalidDataException($"The {requiredName} manifest descriptor is invalid.");
        }

        var matches = release.Assets.Where(asset => string.Equals(asset.Name, requiredName, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1 || matches[0].Size != dto.Size || !IsOfficialDownloadUri(matches[0].DownloadUri, release.TagName, requiredName))
        {
            throw new InvalidDataException($"The release asset metadata for {requiredName} does not match the manifest.");
        }

        return new UpdateManifestAsset(requiredName, dto.Size, dto.Sha256);
    }

    public static bool IsOfficialDownloadUri(Uri uri, string tagName, string assetName)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var expected = $"/airanluo-dot/DropSpace/releases/download/{Uri.EscapeDataString(tagName)}/{Uri.EscapeDataString(assetName)}";
        return string.Equals(uri.AbsolutePath, expected, StringComparison.Ordinal);
    }

    private sealed record ManifestDto
    {
        public int SchemaVersion { get; init; }

        public string Channel { get; init; } = string.Empty;

        public string Version { get; init; } = string.Empty;

        public int VersionCode { get; init; }

        public DateTimeOffset PublishedAt { get; init; }

        public int MinimumWindowsBuild { get; init; }

        public bool Mandatory { get; init; }

        public string? Summary { get; init; }

        public ManifestAssetDto? Installer { get; init; }

        public ManifestAssetDto? Portable { get; init; }
    }

    private sealed record ManifestAssetDto
    {
        public string AssetName { get; init; } = string.Empty;

        public long Size { get; init; }

        public string Sha256 { get; init; } = string.Empty;
    }
}
