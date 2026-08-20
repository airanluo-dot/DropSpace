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
    private readonly ResourceManager _resourceManager;
    private readonly ResourceContext _resourceContext;
    private readonly ResourceMap _resourceMap;

    public ResourceStringLocalizer(AppLanguageService language)
    {
        _language = language;
        var resourceIndexPath = ResolveResourceIndexPath();

        // Unpackaged WinUI apps have no default resource view. The portable build bundles this
        // PRI beside the extracted application and resolves strings through an explicit context.
        _resourceManager = new ResourceManager(resourceIndexPath);
        _resourceContext = _resourceManager.CreateResourceContext();
        _resourceContext.Languages = [_language.EffectiveLanguageTag];
        _resourceMap = _resourceManager.MainResourceMap.GetSubtree("Resources");
    }

    public CultureInfo Culture => CultureInfo.GetCultureInfo(_language.EffectiveLanguageTag);

    public string Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var value = _resourceMap.GetValue(key.Replace('.', '/'), _resourceContext).ValueAsString;
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Missing DropSpace localized resource '{key}'.");
        }

        return value;
    }

    public string Format(string key, params object?[] arguments) =>
        string.Format(Culture, Get(key), arguments);

    private static string ResolveResourceIndexPath()
    {
        var bundledPath = Path.Combine(AppContext.BaseDirectory, "resources.pri");
        if (File.Exists(bundledPath))
        {
            return bundledPath;
        }

        try
        {
            var packagedPath = ResourceLoader.GetDefaultResourceFilePath();
            if (!string.IsNullOrWhiteSpace(packagedPath) && File.Exists(packagedPath))
            {
                return packagedPath;
            }
        }
        catch (Exception)
        {
            // An unpackaged app has no default resource view; its bundled PRI is the authority.
        }

        throw new FileNotFoundException(
            "DropSpace's resource index was not found next to the application.",
            bundledPath);
    }
}
