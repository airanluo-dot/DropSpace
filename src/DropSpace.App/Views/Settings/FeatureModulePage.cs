using System.Text.Json;
using DropSpace.App.Services.Dlc;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Dlc;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DropSpace.App.Views.Settings;

/// <summary>The host renders validated declarations; modules never supply XAML or UI delegates.</summary>
public sealed class FeatureModulePage : UserControl
{
    private readonly FeatureModuleRuntime _runtime;
    private readonly IAppStringLocalizer _strings;
    private readonly ModuleManifest _manifest;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, Func<string>> _settingValues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Action<string>> _settingSetters = new(StringComparer.Ordinal);
    private readonly List<Button> _buttons = [];
    private readonly TextBlock _result = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private bool _busy, _retired, _initialized;

    public FeatureModulePage(FeatureModuleRuntime runtime, IAppStringLocalizer strings, ModuleManifest manifest, ModulePage? page, Action? close = null)
    {
        _runtime = runtime; _strings = strings; _manifest = manifest;
        var body = new StackPanel { Spacing = 18, MaxWidth = 780, HorizontalAlignment = HorizontalAlignment.Left };
        if (close is not null)
        {
            var closeButton = new Button { Content = strings.Get("CommonClose"), HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetName(closeButton, strings.Get("CommonClose"));
            closeButton.Click += (_, _) => close(); body.Children.Add(closeButton);
        }
        body.Children.Add(new TextBlock { Text = Text(page?.Title ?? manifest.Name), FontSize = 28, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (page is not null) body.Children.Add(new TextBlock { Text = Text(page.Description), TextWrapping = TextWrapping.Wrap, Opacity = .72 });
        var actions = new StackPanel { Spacing = 8 };
        foreach (var action in page?.Actions ?? [])
        {
            var button = new Button { Content = new TextBlock { Text = Text(action.Label), TextWrapping = TextWrapping.Wrap }, HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetName(button, Text(action.Label));
            AutomationProperties.SetAutomationId(button, "ModuleAction-" + manifest.Id + "-" + action.Id);
            button.Click += async (_, _) => await RunAsync(async token =>
            {
                var reply = await _runtime.InvokeAsync(manifest.Id, action.Id, JsonSerializer.SerializeToElement(new { }), token);
                if (!token.IsCancellationRequested && reply.Result.ValueKind == JsonValueKind.Object &&
                    reply.Result.TryGetProperty("messageKey", out var key) && key.ValueKind == JsonValueKind.String &&
                    key.GetString() is { } resourceKey && manifest.Resources["en-US"].ContainsKey(resourceKey))
                {
                    _result.Text = Text(new(resourceKey)); _result.Visibility = Visibility.Visible;
                }
            });
            _buttons.Add(button); actions.Children.Add(button);
        }
        if (actions.Children.Count > 0) body.Children.Add(Card(actions));
        if (manifest.Ui.Settings.Length > 0)
        {
            var settings = new StackPanel { Spacing = 14 };
            foreach (var setting in manifest.Ui.Settings)
            {
                if (setting.Kind == "boolean")
                {
                    var toggle = new ToggleSwitch { Header = Text(setting.Label), IsOn = setting.DefaultValue == "true" };
                    AutomationProperties.SetName(toggle, Text(setting.Label));
                    _settingValues[setting.Id] = () => toggle.IsOn ? "true" : "false";
                    _settingSetters[setting.Id] = value => toggle.IsOn = value == "true";
                    settings.Children.Add(toggle);
                }
                else
                {
                    var input = new TextBox { Header = Text(setting.Label), Text = setting.DefaultValue, MaxLength = 4096, TextWrapping = TextWrapping.Wrap, AcceptsReturn = false };
                    AutomationProperties.SetName(input, Text(setting.Label));
                    _settingValues[setting.Id] = () => input.Text;
                    _settingSetters[setting.Id] = value => input.Text = value;
                    settings.Children.Add(input);
                }
            }
            var save = new Button { Content = strings.Get("ModuleSave"), HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetName(save, strings.Get("ModuleSave"));
            save.Click += async (_, _) => await RunAsync(async token =>
            {
                foreach (var (id, read) in _settingValues)
                {
                    token.ThrowIfCancellationRequested();
                    await _runtime.WriteSettingAsync(manifest.Id, id, read(), token);
                }
            });
            _buttons.Add(save); settings.Children.Add(save); body.Children.Add(Card(settings));
        }
        body.Children.Add(_result);
        Content = new ScrollViewer { Content = body, Padding = new(24), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        AutomationProperties.SetName(this, Text(page?.Title ?? manifest.Name));
        Loaded += async (_, _) =>
        {
            if (_initialized || _retired) return;
            _initialized = true;
            await RunAsync(async token =>
            {
                var values = await _runtime.ReadSettingsAsync(manifest.Id, token);
                if (token.IsCancellationRequested) return;
                foreach (var (id, value) in values) if (_settingSetters.TryGetValue(id, out var set)) set(value);
            });
        };
        Unloaded += (_, _) => Retire();
    }

    public void Retire()
    {
        if (_retired) return;
        _retired = true; _lifetime.Cancel();
        foreach (var button in _buttons) button.IsEnabled = false;
        // CancellationTokenSource remains owned by this bounded view until awaited callbacks finish.
        if (!_busy) _lifetime.Dispose();
    }

    private string Text(ModuleText text) => ModuleContract.Text(_manifest, text, _strings.Culture.Name);
    private static Border Card(UIElement body) => new()
    {
        Child = body, Padding = new(16), CornerRadius = new(12),
        Background = Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] as Brush,
    };
    private async Task RunAsync(Func<CancellationToken, Task> operation)
    {
        if (_busy || _retired) return;
        _busy = true;
        foreach (var button in _buttons) button.IsEnabled = false;
        _result.Visibility = Visibility.Collapsed;
        try { await operation(_lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            System.Diagnostics.Debug.WriteLine("Module page operation failed: " + error.GetType().Name);
            if (!_retired) { _result.Text = _strings.Get("DlcOperationFailed"); _result.Visibility = Visibility.Visible; }
        }
        finally
        {
            _busy = false;
            if (_retired) _lifetime.Dispose();
            else foreach (var button in _buttons) button.IsEnabled = true;
        }
    }
}
