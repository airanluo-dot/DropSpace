using System.Globalization;

namespace DropSpace.Core.Abstractions;

/// <summary>
/// Resolves user-facing application strings from the host application's resources.
/// Core and infrastructure code depend on this narrow contract instead of owning UI text.
/// </summary>
public interface IAppStringLocalizer
{
    CultureInfo Culture { get; }

    string Get(string key);

    string Format(string key, params object?[] arguments);
}

/// <summary>
/// Test-only fallback for components constructed outside the application composition root.
/// Production registrations always use the resource-backed implementation in DropSpace.App.
/// </summary>
public sealed class IdentityAppStringLocalizer : IAppStringLocalizer
{
    public static IdentityAppStringLocalizer Instance { get; } = new();

    private IdentityAppStringLocalizer()
    {
    }

    public CultureInfo Culture => CultureInfo.InvariantCulture;

    public string Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return key;
    }

    public string Format(string key, params object?[] arguments) =>
        string.Format(Culture, Get(key), arguments);
}
