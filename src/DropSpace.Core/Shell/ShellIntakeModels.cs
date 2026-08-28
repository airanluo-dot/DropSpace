namespace DropSpace.Core.Shell;

public enum ShellIntakeSource
{
    ExplorerContextMenu = 1,
    SendTo = 2,
}

public sealed record ShellIntakeRequest(
    ShellIntakeSource Source,
    IReadOnlyList<string> Paths);

public sealed record ShellIntakeParseResult(
    bool IsShellIntake,
    ShellIntakeRequest? Request,
    string? ErrorCategory)
{
    public bool Succeeded => Request is not null;

    public static ShellIntakeParseResult NotShell { get; } = new(false, null, null);

    public static ShellIntakeParseResult Failure(string category) => new(true, null, category);

    public static ShellIntakeParseResult Success(ShellIntakeRequest request) => new(true, request, null);
}

public static class ShellIntakeCommandLineParser
{
    public const int MaximumItems = 128;
    public const int MaximumCommandLineCharacters = 32_767;

    public static ShellIntakeParseResult Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var shellIndex = IndexOf(arguments, "--shell-add");
        if (shellIndex < 0)
        {
            return ShellIntakeParseResult.NotShell;
        }

        var sourceOptionIndex = shellIndex + 1;
        if (sourceOptionIndex >= arguments.Count || !string.Equals(arguments[sourceOptionIndex], "--source", StringComparison.OrdinalIgnoreCase))
        {
            return ShellIntakeParseResult.Failure("missing-source");
        }

        var sourceIndex = sourceOptionIndex + 1;
        if (sourceIndex >= arguments.Count || !TryParseSource(arguments[sourceIndex], out var source))
        {
            return ShellIntakeParseResult.Failure("invalid-source");
        }

        var pathStart = sourceIndex + 1;
        var delimiterIndex = IndexOf(arguments, "--", pathStart);
        var hasDelimiter = delimiterIndex >= 0;
        if (hasDelimiter)
        {
            pathStart = delimiterIndex + 1;
        }

        if (pathStart >= arguments.Count)
        {
            return ShellIntakeParseResult.Failure("missing-paths");
        }

        var paths = new List<string>(Math.Min(MaximumItems, arguments.Count - pathStart));
        var totalCharacters = 0;
        for (var index = pathStart; index < arguments.Count; index++)
        {
            var path = arguments[index];
            if (!hasDelimiter && path.StartsWith("--", StringComparison.Ordinal))
            {
                return ShellIntakeParseResult.Failure("unexpected-option");
            }

            if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0)
            {
                return ShellIntakeParseResult.Failure("invalid-path");
            }

            if (path.Length > MaximumCommandLineCharacters)
            {
                return ShellIntakeParseResult.Failure("path-too-long");
            }

            totalCharacters = checked(totalCharacters + path.Length);
            if (totalCharacters > MaximumCommandLineCharacters)
            {
                return ShellIntakeParseResult.Failure("command-line-too-long");
            }

            if (paths.Count >= MaximumItems)
            {
                return ShellIntakeParseResult.Failure("too-many-items");
            }

            if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                paths.Add(path);
            }
        }

        return paths.Count == 0
            ? ShellIntakeParseResult.Failure("missing-paths")
            : ShellIntakeParseResult.Success(new ShellIntakeRequest(source, paths));
    }

    private static int IndexOf(IReadOnlyList<string> arguments, string value, int startIndex = 0)
    {
        for (var index = Math.Max(0, startIndex); index < arguments.Count; index++)
        {
            if (string.Equals(arguments[index], value, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool TryParseSource(string value, out ShellIntakeSource source)
    {
        if (string.Equals(value, "explorer-context-menu", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "explorercontextmenu", StringComparison.OrdinalIgnoreCase))
        {
            source = ShellIntakeSource.ExplorerContextMenu;
            return true;
        }

        if (string.Equals(value, "sendto", StringComparison.OrdinalIgnoreCase))
        {
            source = ShellIntakeSource.SendTo;
            return true;
        }

        source = default;
        return false;
    }
}
