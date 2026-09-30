using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Updates;

namespace DropSpace.Infrastructure.Updates;

public sealed class GitHubReleaseUpdateSource(HttpClient client, ReleaseVersion currentVersion) : IUpdateSource
{
    private const int MaximumReleaseMetadataBytes = 2 * 1024 * 1024;
    private static readonly Uri ReleasesUri = new(
        "https://api.github.com/repos/airanluo-dot/DropSpace/releases?per_page=20&page=1");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
    };

    public async Task<IReadOnlyList<UpdateRelease>> GetReleasesAsync(CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(ReleasesUri, "application/vnd.github+json");
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var bytes = await UpdateMetadataReader.ReadBoundedAsync(response.Content, MaximumReleaseMetadataBytes, cancellationToken)
            .ConfigureAwait(false);
        ReleaseDto?[] releases;
        try
        {
            releases = JsonSerializer.Deserialize<ReleaseDto?[]>(bytes.Span, JsonOptions) ?? [];
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("GitHub returned malformed release metadata.", exception);
        }

        return releases.Take(20).Select(MapRelease).ToArray();
    }

    public async Task<ReadOnlyMemory<byte>> GetManifestAsync(
        UpdateRelease release,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        var matches = release.Assets.Where(asset =>
            string.Equals(asset.Name, "update-manifest.json", StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1 ||
            !UpdateManifestParser.IsOfficialDownloadUri(matches[0].DownloadUri, release.TagName, matches[0].Name))
        {
            throw new InvalidDataException("The selected release does not have one official update manifest asset.");
        }

        using var request = CreateRequest(matches[0].DownloadUri, "application/octet-stream");
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await UpdateMetadataReader.ReadBoundedAsync(response.Content, UpdateManifestParser.MaximumManifestBytes, cancellationToken)
            .ConfigureAwait(false);
    }

    private HttpRequestMessage CreateRequest(Uri uri, string accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        request.Headers.UserAgent.ParseAdd($"DropSpace/{currentVersion}");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private static UpdateRelease MapRelease(ReleaseDto? dto)
    {
        if (dto is null || string.IsNullOrWhiteSpace(dto.TagName) || dto.Assets is null)
        {
            throw new InvalidDataException("GitHub release metadata contains an invalid release identity or asset collection.");
        }

        if (!Uri.TryCreate(dto.HtmlUrl, UriKind.Absolute, out var htmlUri))
        {
            throw new InvalidDataException("GitHub release metadata contains an invalid HTML URL.");
        }

        var assets = dto.Assets.Select(asset =>
        {
            if (asset is null || string.IsNullOrWhiteSpace(asset.Name) ||
                !Uri.TryCreate(asset.BrowserDownloadUrl, UriKind.Absolute, out var downloadUri))
            {
                throw new InvalidDataException("GitHub release metadata contains an invalid asset identity or URL.");
            }

            return new UpdateReleaseAsset(asset.Name, asset.Size, downloadUri);
        }).ToArray();
        return new UpdateRelease(dto.TagName, dto.Draft, dto.Prerelease, dto.PublishedAt, htmlUri, assets);
    }


    private sealed record ReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; init; } = string.Empty;

        [JsonPropertyName("draft")]
        public bool Draft { get; init; }

        [JsonPropertyName("prerelease")]
        public bool Prerelease { get; init; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset? PublishedAt { get; init; }

        [JsonPropertyName("html_url")]
        public string HtmlUrl { get; init; } = string.Empty;

        [JsonPropertyName("assets")]
        public ReleaseAssetDto?[]? Assets { get; init; } = [];
    }

    private sealed record ReleaseAssetDto
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; init; }

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; init; } = string.Empty;
    }
}
