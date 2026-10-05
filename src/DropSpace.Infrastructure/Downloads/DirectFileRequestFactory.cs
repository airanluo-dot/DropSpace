// Adapted from NovaClip Beta8 DirectFileRequestFactory; ordinary files only.
using DropSpace.Core.Downloads;

namespace DropSpace.Infrastructure.Downloads;

public static class DirectFileRequestFactory
{
    public static bool TryParseUrl(string? value, out Uri? uri)
    {
        uri = null;
        if (value is null || value.Length > 16384 || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var candidate) ||
            candidate.Scheme is not ("http" or "https") || candidate.Host.Length == 0 || candidate.UserInfo.Length != 0) return false;
        uri = candidate; return true;
    }
    public static string SuggestFileName(Uri uri) => new FileNameSanitizer().Sanitize(Uri.UnescapeDataString(uri.AbsolutePath.Split('/').LastOrDefault() ?? ""), "download.bin");
    public static DownloadRequest Create(string url, string directory, string? fileName)
    {
        if (!TryParseUrl(url, out var uri)) throw new ArgumentException("An HTTP(S) URL without embedded credentials is required.");
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("An absolute output directory is required.");
        fileName = string.IsNullOrWhiteSpace(fileName) ? SuggestFileName(uri!) : fileName.Trim();
        if (fileName is "." or ".." || new FileNameSanitizer().Sanitize(fileName) != fileName) throw new ArgumentException("Invalid file name.");
        return new(Guid.NewGuid(), uri!.AbsoluteUri, Path.GetFullPath(directory), fileName);
    }
}
