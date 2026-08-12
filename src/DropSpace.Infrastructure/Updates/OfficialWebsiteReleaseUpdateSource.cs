using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Updates;

namespace DropSpace.Infrastructure.Updates;

/// <summary>
/// Reads the versioned, public DropSpace website release contract. Executable URLs are still
/// required to point to the matching official GitHub Release; the website API cannot redirect the
/// updater to an arbitrary program.
/// </summary>
public sealed class OfficialWebsiteReleaseUpdateSource(
    HttpClient client,
    ReleaseVersion currentVersion,
    Uri endpoint) : IUpdateSource
{
    public const int MaximumReleaseMetadataBytes = 2 * 1024 * 1024;
    private const int SupportedSchemaVersion = 1;
    private const int MaximumReleaseCount = 20;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
    };

    public async Task<IReadOnlyList<UpdateRelease>> GetReleasesAsync(CancellationToken cancellationToken = default)
    {
        if (!IsOfficialEndpoint(endpoint))
        {
            throw new InvalidDataException("The update metadata endpoint is not an official DropSpace website endpoint.");
        }

        using var request = CreateRequest(endpoint, "application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var bytes = await ReadBoundedAsync(response.Content, MaximumReleaseMetadataBytes, cancellationToken)
            .ConfigureAwait(false);
        ReleaseApiDto payload;
        try
        {
            payload = JsonSerializer.Deserialize<ReleaseApiDto>(bytes.Span, JsonOptions)
                ?? throw new InvalidDataException("The website release API returned an empty payload.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The website release API returned malformed metadata.", exception);
        }

        if (payload.SchemaVersion != SupportedSchemaVersion ||
            payload.Releases is null ||
            payload.Releases.Length > MaximumReleaseCount)
        {
            throw new InvalidDataException("The website release API schema or release count is unsupported.");
        }

        return payload.Releases.Select(MapRelease).ToArray();
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
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadBoundedAsync(response.Content, UpdateManifestParser.MaximumManifestBytes, cancellationToken)
            .ConfigureAwait(false);
    }

    public static bool IsOfficialEndpoint(Uri value) =>
        value.Scheme == Uri.UriSchemeHttps &&
        value.IsDefaultPort &&
        string.IsNullOrEmpty(value.UserInfo) &&
        string.IsNullOrEmpty(value.Query) &&
        string.IsNullOrEmpty(value.Fragment) &&
        (string.Equals(value.Host, "dropspace.pages.dev", StringComparison.OrdinalIgnoreCase) &&
         string.Equals(value.AbsolutePath, "/api/v1/releases.json", StringComparison.Ordinal) ||
         string.Equals(value.Host, "airanluo-dot.github.io", StringComparison.OrdinalIgnoreCase) &&
         string.Equals(value.AbsolutePath, "/DropSpace/api/v1/releases.json", StringComparison.Ordinal));

    private HttpRequestMessage CreateRequest(Uri uri, string accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        request.Headers.UserAgent.ParseAdd($"DropSpace/{currentVersion}");
        return request;
    }

    private static UpdateRelease MapRelease(ReleaseDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.TagName) ||
            !ReleaseVersion.TryParse(dto.TagName, out _) ||
            !Uri.TryCreate(dto.HtmlUrl, UriKind.Absolute, out var htmlUri) ||
            htmlUri.Scheme != Uri.UriSchemeHttps ||
            !htmlUri.IsDefaultPort ||
            !string.IsNullOrEmpty(htmlUri.UserInfo) ||
            !string.IsNullOrEmpty(htmlUri.Query) ||
            !string.IsNullOrEmpty(htmlUri.Fragment) ||
            !string.Equals(htmlUri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(htmlUri.AbsolutePath, $"/airanluo-dot/DropSpace/releases/tag/{dto.TagName}", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The website release API contains an invalid release identity.");
        }

        var assets = dto.Assets?.Select(asset =>
        {
            if (string.IsNullOrWhiteSpace(asset.Name) || asset.Size <= 0 ||
                !Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var downloadUri) ||
                !UpdateManifestParser.IsOfficialDownloadUri(downloadUri, dto.TagName, asset.Name))
            {
                throw new InvalidDataException("The website release API contains an invalid asset identity.");
            }

            return new UpdateReleaseAsset(asset.Name, asset.Size, downloadUri);
        }).ToArray() ?? throw new InvalidDataException("The website release API omitted release assets.");
        if (assets.Select(asset => asset.Name).Distinct(StringComparer.Ordinal).Count() != assets.Length)
        {
            throw new InvalidDataException("The website release API contains duplicate release assets.");
        }

        return new UpdateRelease(dto.TagName, dto.IsDraft, dto.IsPrerelease, dto.PublishedAt, htmlUri, assets);
    }

    private static async Task<ReadOnlyMemory<byte>> ReadBoundedAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long contentLength && contentLength > maximumBytes)
        {
            throw new InvalidDataException("The update metadata response exceeds the supported size limit.");
        }

        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > maximumBytes)
            {
                throw new InvalidDataException("The update metadata response exceeds the supported size limit.");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private sealed record ReleaseApiDto
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("releases")]
        public ReleaseDto[]? Releases { get; init; }
    }

    private sealed record ReleaseDto
    {
        [JsonPropertyName("tagName")]
        public string TagName { get; init; } = string.Empty;

        [JsonPropertyName("isDraft")]
        public bool IsDraft { get; init; }

        [JsonPropertyName("isPrerelease")]
        public bool IsPrerelease { get; init; }

        [JsonPropertyName("publishedAt")]
        public DateTimeOffset? PublishedAt { get; init; }

        [JsonPropertyName("htmlUrl")]
        public string HtmlUrl { get; init; } = string.Empty;

        [JsonPropertyName("assets")]
        public ReleaseAssetDto[]? Assets { get; init; }
    }

    private sealed record ReleaseAssetDto
    {
        [JsonPropertyName("name")]
        public string Name { get; init; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; init; }

        [JsonPropertyName("downloadUrl")]
        public string DownloadUrl { get; init; } = string.Empty;
    }
}
