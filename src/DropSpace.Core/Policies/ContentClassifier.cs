using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DropSpace.Core.Models;

namespace DropSpace.Core.Policies;

public static partial class ContentClassifier
{
    private const int TitleLimit = 160;

    public static TextCandidate CreateTextCandidate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Clipboard text cannot be empty.", nameof(text));
        }

        var (kind, subtype, confidence, url) = Classify(normalized);
        var title = CreateTitle(normalized, url);
        var fingerprint = FingerprintService.ForText(normalized);

        return new TextCandidate(normalized, fingerprint, kind, title, subtype, confidence, url);
    }

    public static (ItemKind Kind, DetectedSubtype Subtype, DetectionConfidence Confidence, UrlMetadata? Url)
        Classify(string text)
    {
        if (TryCreateUrl(text, out var url))
        {
            return (ItemKind.Url, DetectedSubtype.Url, DetectionConfidence.High, url);
        }

        if (HexColorRegex().IsMatch(text) || RgbColorRegex().IsMatch(text))
        {
            return (ItemKind.Color, DetectedSubtype.Color, DetectionConfidence.High, null);
        }

        if (LooksLikeJson(text))
        {
            return (ItemKind.Code, DetectedSubtype.Json, DetectionConfidence.High, null);
        }

        if (LooksLikeCode(text))
        {
            return (ItemKind.Code, DetectedSubtype.Code, DetectionConfidence.Medium, null);
        }

        if (WindowsPathRegex().IsMatch(text))
        {
            return (ItemKind.Text, DetectedSubtype.Path, DetectionConfidence.Medium, null);
        }

        return (ItemKind.Text, DetectedSubtype.Plain, DetectionConfidence.High, null);
    }

    public static string CreateTitle(string text, UrlMetadata? url = null)
    {
        if (url is not null)
        {
            return url.Host.Length <= TitleLimit ? url.Host : url.Host[..TitleLimit];
        }

        var firstLine = text.Split('\n', 2)[0].Trim();
        if (firstLine.Length == 0)
        {
            firstLine = "Text";
        }

        return firstLine.Length <= TitleLimit ? firstLine : string.Concat(firstLine.AsSpan(0, TitleLimit - 1), "…");
    }

    public static string BuildSearchText(string title, string? body, int maximumCharacters = 65_536)
    {
        var source = string.Concat(title, " ", body ?? string.Empty);
        var normalized = SearchNormalizer.Normalize(source);
        return normalized.Length <= maximumCharacters ? normalized : normalized[..maximumCharacters];
    }

    private static bool TryCreateUrl(string text, out UrlMetadata? metadata)
    {
        metadata = null;
        if (text.Any(char.IsWhiteSpace) || !Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        var builder = new UriBuilder(uri)
        {
            Fragment = string.Empty,
        };

        var normalized = builder.Uri.AbsoluteUri;
        metadata = new UrlMetadata(
            normalized,
            uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.PathAndQuery, UriFormat.Unescaped),
            uri.IdnHost,
            uri.Scheme);
        return true;
    }

    private static bool LooksLikeJson(string text)
    {
        var trimmed = text.AsSpan().Trim();
        if (trimmed.Length < 2 || !((trimmed[0] == '{' && trimmed[^1] == '}') || (trimmed[0] == '[' && trimmed[^1] == ']')))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool LooksLikeCode(string text)
    {
        var signals = 0;
        ReadOnlySpan<string> tokens = ["=>", "{", "}", ";", "public ", "private ", "class ", "const ", "function ", "import ", "SELECT "];
        foreach (var token in tokens)
        {
            if (text.Contains(token, StringComparison.Ordinal))
            {
                signals++;
            }
        }

        return signals >= 2 || (text.Contains('\n') && IndentedLineRegex().IsMatch(text));
    }

    [GeneratedRegex("^#(?:[0-9A-Fa-f]{3}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$")]
    private static partial Regex HexColorRegex();

    [GeneratedRegex("^rgba?\\(\\s*(?:25[0-5]|2[0-4]\\d|1?\\d?\\d)\\s*,\\s*(?:25[0-5]|2[0-4]\\d|1?\\d?\\d)\\s*,\\s*(?:25[0-5]|2[0-4]\\d|1?\\d?\\d)(?:\\s*,\\s*(?:0|1|0?\\.\\d+))?\\s*\\)$", RegexOptions.IgnoreCase)]
    private static partial Regex RgbColorRegex();

    [GeneratedRegex("^(?:[A-Za-z]:\\\\|\\\\\\\\)[^\\r\\n]+$")]
    private static partial Regex WindowsPathRegex();

    [GeneratedRegex("(?m)^(?: {2,}|\\t)\\S")]
    private static partial Regex IndentedLineRegex();
}
