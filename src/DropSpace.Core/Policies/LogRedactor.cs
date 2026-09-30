using System.Text.RegularExpressions;

namespace DropSpace.Core.Policies;

public static partial class LogRedactor
{
    public static string Redact(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        var value = UrlUserInfoRegex().Replace(message, "$1[credentials]@");
        value = UrlQueryRegex().Replace(value, "$1?[redacted]");
        value = WindowsPathRegex().Replace(value, "[path]");
        value = BearerRegex().Replace(value, "Bearer [secret]");
        // Redact the credential before a key/value match can consume its Bearer label.
        value = TokenRegex().Replace(value, "$1=[secret]");
        return value.Length <= 4_096 ? value : string.Concat(value.AsSpan(0, 4_095), "…");
    }

    [GeneratedRegex("(https?://)[^\\s/?#]*@", RegexOptions.IgnoreCase)]
    private static partial Regex UrlUserInfoRegex();

    [GeneratedRegex("(https?://[^\\s?#]+)(?:\\?[^\\s#]*)?(?:#[^\\s]*)?", RegexOptions.IgnoreCase)]
    private static partial Regex UrlQueryRegex();

    [GeneratedRegex("(?<![A-Za-z0-9])(?:[A-Za-z]:\\\\|\\\\\\\\)[^\\r\\n<>|\"']+")]
    private static partial Regex WindowsPathRegex();

    [GeneratedRegex("(?i)\\b(api[_-]?key|token|secret|password|authorization)\\s*[:=]\\s*[^\\s,;]+")]
    private static partial Regex TokenRegex();

    [GeneratedRegex("(?i)Bearer\\s+[A-Za-z0-9._~+/-]+=*")]
    private static partial Regex BearerRegex();
}
