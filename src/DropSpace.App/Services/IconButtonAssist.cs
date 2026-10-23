using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DropSpace.App.Services;

/// <summary>Shares the existing localized accessible action name with pointer users.</summary>
public sealed class IconButtonAssist : DependencyObject
{
    public static readonly DependencyProperty UseAccessibleToolTipProperty = DependencyProperty.RegisterAttached(
        "UseAccessibleToolTip", typeof(bool), typeof(IconButtonAssist), new PropertyMetadata(false, OnEnabledChanged));
    private static readonly DependencyProperty ManagedToolTipProperty = DependencyProperty.RegisterAttached(
        "ManagedToolTip", typeof(string), typeof(IconButtonAssist), new PropertyMetadata(null));

    public static bool GetUseAccessibleToolTip(DependencyObject target) => (bool)target.GetValue(UseAccessibleToolTipProperty);
    public static void SetUseAccessibleToolTip(DependencyObject target, bool value) => target.SetValue(UseAccessibleToolTipProperty, value);

    private static void OnEnabledChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not FrameworkElement element || args.NewValue is not true) return;
        // All registrations are owned by the element; no global event holds retired rows alive.
        element.RegisterPropertyChangedCallback(AutomationProperties.NameProperty, static (sender, _) => Update(sender));
        element.Loaded += static (sender, _) => Update((DependencyObject)sender);
        Update(element);
    }

    private static void Update(DependencyObject target)
    {
        if (!GetUseAccessibleToolTip(target)) return;
        if (target is FrameworkElement element && element.GetBindingExpression(ToolTipService.ToolTipProperty) is not null) return;
        var name = AutomationProperties.GetName(target);
        if (string.IsNullOrWhiteSpace(name)) return;
        var existing = ToolTipService.GetToolTip(target);
        var managed = target.GetValue(ManagedToolTipProperty) as string;
        // Explicit tooltips (including bound descriptions) remain the owning view's choice.
        if (existing is not null && !Equals(existing, managed)) return;
        ToolTipService.SetToolTip(target, name);
        target.SetValue(ManagedToolTipProperty, name);
    }
}
