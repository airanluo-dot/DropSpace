namespace DropSpace.App.Services.Diagnostics;

/// <summary>Fail-closed inputs for the isolated, synthetic UI diagnostic.</summary>
internal sealed record MusicVisualSmokeOptions(string Root, string Language)
{
    internal const string Switch = "--music-visual-smoke";
    internal const string RootPrefix = "DropSpace-visual-";

    internal static MusicVisualSmokeOptions Parse(IReadOnlyList<string> arguments, string? root, string temporaryDirectory)
    {
        if (!arguments.Contains(Switch, StringComparer.OrdinalIgnoreCase) ||
            !arguments.Contains("--test-mode", StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("Visual diagnostics require both explicit diagnostic and test-mode switches.");
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new ArgumentException("Visual diagnostics require a new absolute test root.");
        var fullRoot = Path.GetFullPath(root);
        var parent = Path.GetDirectoryName(fullRoot);
        var expectedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(temporaryDirectory));
        if (!string.Equals(parent, expectedParent, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Visual diagnostics must use a direct child of the temporary directory.");
        var name = Path.GetFileName(fullRoot);
        if (!name.StartsWith(RootPrefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(name[RootPrefix.Length..], "N", out _))
            throw new ArgumentException("The visual test root must have a fresh diagnostic GUID name.");
        if (Directory.Exists(fullRoot) || File.Exists(fullRoot))
            throw new IOException("The visual test root already exists; never reuse existing data.");
        for (var directory = new DirectoryInfo(expectedParent); directory is not null; directory = directory.Parent)
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Visual diagnostics do not follow reparse-point directories.");
        var language = "en-US";
        for (var index = 0; index < arguments.Count; index++)
        {
            if (!string.Equals(arguments[index], "--smoke-language", StringComparison.OrdinalIgnoreCase)) continue;
            if (++index >= arguments.Count) throw new ArgumentException("A visual diagnostic language is required.");
            language = arguments[index];
        }
        if (language is not ("en-US" or "zh-CN"))
            throw new ArgumentException("Visual diagnostics support en-US and zh-CN only.");
        return new(fullRoot, language);
    }

    internal static bool HasPixelContent(byte[] pixels)
    {
        if (pixels.Length < 4 || pixels.Length % 4 != 0) return false;
        var colors = new HashSet<uint>();
        var opaque = 0;
        for (var index = 0; index < pixels.Length; index += 4)
        {
            if (pixels[index + 3] > 240) opaque++;
            if (colors.Count < 64)
                colors.Add((uint)(pixels[index] | pixels[index + 1] << 8 | pixels[index + 2] << 16 | pixels[index + 3] << 24));
        }
        // Every fixture uses an opaque background; a black/clear/uniform readback is not a pass.
        return colors.Count >= 16 && opaque > pixels.Length / 8;
    }
}
