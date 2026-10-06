using System.Security.Cryptography;
using System.Text.Json;
using DropSpace.Core.Lyrics;

namespace DropSpace.Infrastructure.Lyrics;

/// <summary>Pinned transport layout. Model execution identity remains the full upstream hash.</summary>
internal static class AiModelDeliveryManifest
{
    internal const string Identity = "2da82e004022c52dc4315b8f6414273d13802c6c5202668e5be26a08ba6c0ef9";
    internal const string ReleaseBase = "https://github.com/airanluo-dot/DropSpace/releases/download/models-hy-mt2-q8-v1/";
    internal sealed record Part(int Order, string Name, long Bytes, string Sha256, Uri Url);
    private static readonly Lazy<Dictionary<string, (long Bytes, string Hash, Part[] Parts)>> Models = new(Read);

    internal static IReadOnlyList<Part>? Find(AiLyricsModelDescriptor model)
    {
        if (!Models.Value.TryGetValue(model.Id, out var entry)) return null;
        if (entry.Bytes != model.Bytes || !entry.Hash.Equals(model.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Model mirror does not match the execution catalog.");
        return entry.Parts;
    }

    internal static bool Trusted(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        ((uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
          uri.AbsolutePath.StartsWith("/airanluo-dot/DropSpace/releases/download/models-hy-mt2-q8-v1/", StringComparison.Ordinal)) ||
         uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    private static Dictionary<string, (long, string, Part[])> Read()
    {
        using var input = typeof(AiModelDeliveryManifest).Assembly.GetManifestResourceStream(
            "DropSpace.Infrastructure.Lyrics.Manifests.models-hy-mt2-q8-v1.json") ?? throw new InvalidDataException("Missing model delivery manifest.");
        using var bytes = new MemoryStream(); input.CopyTo(bytes);
        if (bytes.Length != 4785 || !Convert.ToHexString(SHA256.HashData(bytes.ToArray())).Equals(Identity, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Model delivery manifest identity mismatch.");
        using var json = JsonDocument.Parse(bytes.ToArray());
        var result = new Dictionary<string, (long, string, Part[])>(StringComparer.Ordinal);
        foreach (var model in json.RootElement.GetProperty("models").EnumerateArray())
        {
            var parts = model.GetProperty("parts").EnumerateArray().Select(part => new Part(
                part.GetProperty("order").GetInt32(), part.GetProperty("name").GetString()!, part.GetProperty("bytes").GetInt64(),
                part.GetProperty("sha256").GetString()!, new Uri(part.GetProperty("url").GetString()!))).ToArray();
            var total = model.GetProperty("fullsize").GetInt64();
            if (parts.Length is < 1 or > 4 || parts.Where((part, i) => part.Order != i + 1 || part.Bytes <= 0 ||
                part.Sha256.Length != 64 || !part.Sha256.All(Uri.IsHexDigit) || !part.Url.AbsoluteUri.StartsWith(ReleaseBase, StringComparison.Ordinal)).Any() ||
                parts.Sum(part => part.Bytes) != total) throw new InvalidDataException("Invalid model delivery layout.");
            result.Add(model.GetProperty("modelId").GetString()!, (total, model.GetProperty("fullsha").GetString()!, parts));
        }
        return result;
    }
}
