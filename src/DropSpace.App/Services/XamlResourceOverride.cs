using System.Reflection;
using DropSpace.Core.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;

namespace DropSpace.App.Services;

/// <summary>
/// Applies the selected language through the app's explicit MRT resource context after XAML has
/// created an element. Windows are applied directly after InitializeComponent because they do
/// not derive from DependencyObject. This keeps unpackaged WinUI language selection independent
/// of the unsupported ApplicationLanguages.PrimaryLanguageOverride API.
/// </summary>
public static class XamlResourceOverride
{
    private const string AutomationNameSuffix =
        ".[using:Microsoft.UI.Xaml.Automation]AutomationProperties.Name";

    private static readonly string[] LocalizedPropertyNames =
    [
        "Content",
        "Message",
        "PlaceholderText",
        "Subtitle",
        "Text",
        "Title",
    ];

    private static IAppStringLocalizer? _strings;

    public static readonly DependencyProperty UidProperty =
        DependencyProperty.RegisterAttached(
            "Uid",
            typeof(string),
            typeof(XamlResourceOverride),
            new PropertyMetadata(null, OnUidChanged));

    public static string GetUid(DependencyObject target) =>
        target.GetValue(UidProperty) as string ?? string.Empty;

    public static void SetUid(DependencyObject target, string value) =>
        target.SetValue(UidProperty, value);

    public static void Initialize(IAppStringLocalizer strings)
    {
        _strings = strings ?? throw new ArgumentNullException(nameof(strings));
    }

    public static void Apply(object target, string uid)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(uid);

        var strings = _strings;
        if (strings is null)
        {
            return;
        }

        foreach (var propertyName in LocalizedPropertyNames)
        {
            if (strings.TryGet($"{uid}.{propertyName}", out var value))
            {
                TrySetLocalizedProperty(target, propertyName, value);
            }
        }

        if (target is DependencyObject dependencyObject &&
            strings.TryGet($"{uid}{AutomationNameSuffix}", out var automationName))
        {
            AutomationProperties.SetName(dependencyObject, automationName);
        }
    }

    private static void OnUidChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (args.NewValue is not string uid || string.IsNullOrWhiteSpace(uid))
        {
            return;
        }

        Apply(target, uid);
        if (target is FrameworkElement element)
        {
            // x:Uid resources can be applied after attached properties during XAML construction.
            // Reapply once the element is in the visual tree so an explicit app choice wins.
            element.Loaded += (_, _) => Apply(element, uid);
        }
    }

    private static void TrySetLocalizedProperty(object target, string propertyName, string value)
    {
        var property = target.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public);
        if (property?.CanWrite != true ||
            (property.PropertyType != typeof(string) && property.PropertyType != typeof(object)))
        {
            return;
        }

        property.SetValue(target, value);
    }
}
