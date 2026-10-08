using DropSpace.Core.Abstractions;

namespace DropSpace.Core.Models;

/// <summary>
/// An owned UI message identity. Store this with the status, then render with the current
/// localizer. Strings in arguments are literal data; nested messages remain translatable.
/// This has no global registry, callbacks or service references.
/// </summary>
public sealed class AppUiMessage
{
    private readonly object?[] _arguments;

    private AppUiMessage(string? resourceKey, string? literalText, object?[] arguments, long? byteCount = null)
    {
        ResourceKey = resourceKey;
        LiteralText = literalText;
        _arguments = arguments.ToArray();
        ByteCount = byteCount;
    }

    public static AppUiMessage Empty { get; } = Literal(string.Empty);

    public string? ResourceKey { get; }

    public string? LiteralText { get; }

    public long? ByteCount { get; }

    public IReadOnlyList<object?> Arguments => Array.AsReadOnly(_arguments);

    public static AppUiMessage Resource(string key, params object?[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(arguments);
        // Long-lived statuses must not retain a mutable UI object or service graph.
        foreach (var argument in arguments)
            if (argument is not null and not string and not AppUiMessage && !argument.GetType().IsValueType)
                throw new ArgumentException("UI message arguments must be literal strings, value data or nested messages.", nameof(arguments));
        return new(key, null, arguments);
    }

    public static AppUiMessage Literal(string? text) => new(null, text ?? string.Empty, []);

    public string Render(IAppStringLocalizer strings)
    {
        ArgumentNullException.ThrowIfNull(strings);
        if (ByteCount is { } bytes) return bytes switch
        {
            < 1024 => strings.Format("Bytes", bytes),
            < 1024 * 1024 => strings.Format("Kilobytes", bytes / 1024d),
            < 1024L * 1024 * 1024 => strings.Format("Megabytes", bytes / (1024d * 1024)),
            _ => strings.Format("Gigabytes", bytes / (1024d * 1024 * 1024)),
        };
        if (ResourceKey is null) return LiteralText ?? string.Empty;
        if (_arguments.Length == 0) return strings.Get(ResourceKey);
        return strings.Format(ResourceKey, _arguments.Select(argument =>
            argument is AppUiMessage nested ? nested.Render(strings) : argument).ToArray());
    }

    /// <summary>Retains the original byte count instead of a locale-formatted size.</summary>
    public static AppUiMessage Bytes(long value) => new(null, null, [], value);
}
