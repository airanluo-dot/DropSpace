using System.Globalization;
using System.Text;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Policies;
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
    private readonly IReadOnlyDictionary<string, ResourceContext> _resourceContexts;
    private readonly ResourceMap _resourceMap;
    private sealed record RenderedUiString(string Key, object?[] Arguments);
    private readonly object _renderedGate = new();
    private readonly Dictionary<string, RenderedUiString> _rendered = [];
    private readonly Queue<string> _renderedOrder = new();

    public ResourceStringLocalizer(AppLanguageService language)
    {
        _language = language;
        var resourceIndexPath = ResolveResourceIndexPath();

        // Unpackaged WinUI apps have no default resource view. The portable build bundles this
        // PRI beside the extracted application and resolves strings through an explicit context.
        _resourceManager = new ResourceManager(resourceIndexPath);
        _resourceContexts = AppLanguageCatalog.All.Select(item => item.Tag)
            .ToDictionary(tag => tag, tag =>
            {
                var context = _resourceManager.CreateResourceContext();
                context.QualifierValues["Language"] = tag;
                return context;
            });
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

        Remember(value, key, []);
        return value;
    }

    public bool TryGet(string key, out string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (TryGetInLanguage(key, _language.EffectiveLanguageTag, out value)) return true;
        // Runtime fallback keeps a damaged resource from producing blank UI. The release
        // completeness gate still rejects missing translations in every required resource set.
        return _language.EffectiveLanguageTag != AppLanguageService.EnglishLanguageTag &&
            TryGetInLanguage(key, AppLanguageService.EnglishLanguageTag, out value);
    }

    private bool TryGetInLanguage(string key, string tag, out string value)
    {
        try
        {
            value = _resourceMap.GetValue(ToResourceMapPath(key), _resourceContexts[tag]).ValueAsString ?? string.Empty;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception)
        {
            value = string.Empty;
            return false;
        }
    }

    public string Format(string key, params object?[] arguments)
    {
        var value = string.Format(Culture, Get(key), arguments);
        Remember(value, key, arguments);
        return value;
    }

    public string Relocalize(string text)
    {
        RenderedUiString? rendered;
        lock (_renderedGate) _rendered.TryGetValue(text, out rendered);
        return rendered is null ? text : rendered.Arguments.Length == 0
            ? Get(rendered.Key) : Format(rendered.Key, rendered.Arguments);
    }

    private void Remember(string value, string key, object?[] arguments)
    {
        lock (_renderedGate)
        {
            if (!_rendered.ContainsKey(value)) _renderedOrder.Enqueue(value);
            _rendered[value] = new(key, arguments.ToArray());
            while (_rendered.Count > 2048 && _renderedOrder.TryDequeue(out var oldest)) _rendered.Remove(oldest);
        }
    }

    private static string ToResourceMapPath(string key)
    {
        var path = new StringBuilder(key.Length);
        var bracketDepth = 0;
        foreach (var character in key)
        {
            if (character == '[')
            {
                bracketDepth++;
            }
            else if (character == ']')
            {
                bracketDepth = Math.Max(0, bracketDepth - 1);
            }

            path.Append(character == '.' && bracketDepth == 0 ? '/' : character);
        }

        return path.ToString();
    }

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
