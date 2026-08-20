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
        _resourceContext.QualifierValues["Language"] = _language.EffectiveLanguageTag;
        _resourceMap = _resourceManager.MainResourceMap.GetSubtree("Resources");
    }

    public CultureInfo Culture => CultureInfo.GetCultureInfo(_language.EffectiveLanguageTag);

    public string Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!TryGet(key, out var value))
        {
            throw new InvalidOperationException($"Missing DropSpace localized resource '{key}'.");
        }

        return value;
    }

    public bool TryGet(string key, out string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        try
        {
            value = _resourceMap.GetValue(key.Replace('.', '/'), _resourceContext).ValueAsString ?? string.Empty;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception)
        {
            value = string.Empty;
            return false;
        }
    }

    public string Format(string key, params object?[] arguments) =>
        string.Format(Culture, Get(key), arguments);

    private static string ResolveResourceIndexPath()
    {
        // Do not name this explicit, app-owned PRI "resources.pri": WinUI treats that special
        // filename as its default index and would lose the framework theme resource maps.
        var bundledPath = Path.Combine(AppContext.BaseDirectory, "DropSpace.resources.pri");
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
