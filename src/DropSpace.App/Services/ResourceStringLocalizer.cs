using System.Globalization;
using DropSpace.Core.Abstractions;
using Microsoft.Windows.ApplicationModel.Resources;

namespace DropSpace.App.Services;

/// <summary>
/// Resource-backed bridge used by App, Core, and Infrastructure user-facing status text.
/// Resources.resw remains the single translation source for XAML and imperative code.
/// </summary>
public sealed class ResourceStringLocalizer : IAppStringLocalizer
{
    private readonly AppLanguageService _language;
    private readonly ResourceLoader _resources = new();

    public ResourceStringLocalizer(AppLanguageService language)
    {
        _language = language;
    }

    public CultureInfo Culture => CultureInfo.GetCultureInfo(_language.EffectiveLanguageTag);

    public string Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var value = _resources.GetString(key.Replace('.', '/'));
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Missing DropSpace localized resource '{key}'.");
        }

        return value;
    }

    public string Format(string key, params object?[] arguments) =>
        string.Format(Culture, Get(key), arguments);
}
