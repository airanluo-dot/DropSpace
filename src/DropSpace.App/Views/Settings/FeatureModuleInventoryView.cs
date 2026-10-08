using DropSpace.App.Services.Dlc;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Dlc;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DropSpace.App.Views.Settings;

/// <summary>Optional feature modules use their runtime ledger alongside, not instead of, legacy packages.</summary>
public sealed class FeatureModuleInventoryView : UserControl
{
    private readonly FeatureModuleRuntime _runtime;
    private readonly IAppStringLocalizer _strings;
    private readonly StackPanel _cards = new() { Spacing = 12 };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly Action<ModuleManifest> _openSettings;
    private bool _loaded, _active;
    private int _operations;
    private readonly HashSet<string> _retiring = new(StringComparer.Ordinal);
    private bool IsBusy => _operations > 0;
    private int _renderQueued;
    private CancellationTokenSource? _lifetime;

    public FeatureModuleInventoryView(FeatureModuleRuntime runtime, IAppStringLocalizer strings, Action<ModuleManifest> openSettings)
    {
        _runtime = runtime; _strings = strings; _openSettings = openSettings;
        var body = new StackPanel { Spacing = 12 };
        var refresh = new Button { Content = strings.Get("DlcRefresh"), HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(refresh, strings.Get("DlcRefresh"));
        AutomationProperties.SetAutomationId(refresh, "ModuleCatalogRefresh");
        refresh.Click += async (_, _) =>
        {
            if (IsBusy || !_loaded || !_active || _lifetime is not { IsCancellationRequested: false } owner) return;
            var token = owner.Token;
            _operations++; refresh.IsEnabled = false; _error.Visibility = Visibility.Collapsed; Render();
            try { await _runtime.RefreshCatalogAsync(token); }
            catch (OperationCanceledException) { }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                System.Diagnostics.Debug.WriteLine("Official module catalog refresh failed: " + error.GetType().Name);
                if (_loaded && ReferenceEquals(owner, _lifetime)) { _error.Text = _strings.Get("DlcOperationFailed"); _error.Visibility = Visibility.Visible; }
            }
            finally { _operations--; refresh.IsEnabled = true; if (_loaded && _active) Render(); }
        };
        body.Children.Add(refresh);
        body.Children.Add(_cards); body.Children.Add(_error); Content = body;
        Loaded += (_, _) =>
        {
            if (_loaded) return;
            _loaded = true; _lifetime = new(); _runtime.Changed += OnChanged; Render();
        };
        Unloaded += (_, _) =>
        {
            _loaded = false; _runtime.Changed -= OnChanged;
            _lifetime?.Cancel(); _lifetime?.Dispose(); _lifetime = null;
        };
    }

    public void SetActive(bool active) { _active = active; if (active) Render(); }
    private void OnChanged(object? sender, EventArgs args)
    {
        if (!_loaded || !_active || Interlocked.Exchange(ref _renderQueued, 1) != 0) return;
        if (!DispatcherQueue.TryEnqueue(() => { Interlocked.Exchange(ref _renderQueued, 0); if (_loaded && _active) Render(); }))
            Interlocked.Exchange(ref _renderQueued, 0);
    }

    private void Render()
    {
        if (!_loaded || !_active) return;
        _cards.Children.Clear();
        foreach (var snapshot in _runtime.Snapshots)
        {
            var installation = snapshot.Installation;
            var manifest = snapshot.Manifest;
            var body = new StackPanel { Spacing = 8 };
            var name = manifest is null ? installation.Id : ModuleContract.Text(manifest, manifest.Name, _strings.Culture.Name);
            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            title.Children.Add(new FontIcon { Glyph = manifest?.Ui.Icon switch { "Document" => "\uE8A5", "Tools" => "\uE713", _ => "\uEA86" }, FontSize = 16 });
            title.Children.Add(new TextBlock { Text = name, FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            body.Children.Add(title);
            var package = _runtime.AvailablePackages.FirstOrDefault(item => item.Id == installation.Id);
            var version = installation.Version ?? package?.Version;
            if (version is not null) body.Children.Add(new TextBlock { Text = version, Opacity = .72 });
            if (installation.Version is not null)
            {
                body.Children.Add(new TextBlock { Text = _strings.Get(installation.Enabled ? "ModuleEnabled" : "ModuleDisabled"), TextWrapping = TextWrapping.Wrap });
                body.Children.Add(new TextBlock { Text = _strings.Get(snapshot.RunState switch
                {
                    ModuleRunState.Starting => "ModuleStarting", ModuleRunState.Running => "ModuleRunning",
                    ModuleRunState.Stopping => "ModuleStopping", ModuleRunState.Faulted => "DlcOperationFailed", _ => "ModuleStopped",
                }), TextWrapping = TextWrapping.Wrap });
            }
            else body.Children.Add(new TextBlock { Text = _strings.Get("DlcNotInstalled"), TextWrapping = TextWrapping.Wrap });
            if (installation.Transaction != ModuleTransactionState.None)
                body.Children.Add(new TextBlock { Text = _strings.Get(installation.Transaction switch
                {
                    ModuleTransactionState.Downloading => "DlcDownloading", ModuleTransactionState.Preparing => "DlcVerifying",
                    ModuleTransactionState.Activating => "ModuleStarting", ModuleTransactionState.PendingCleanup => "ModulePendingCleanup", _ => "DlcRemoving",
                }), TextWrapping = TextWrapping.Wrap });
            if (installation.ErrorCode is { } error)
            {
                var minimumHost = manifest?.MinimumHostInterface ?? ModuleContract.HostInterface;
                if (error.StartsWith("HostInterfaceRequired:", StringComparison.Ordinal) && int.TryParse(error.Split(':').Last(), out var required)) minimumHost = required;
                body.Children.Add(new TextBlock { Text = error.StartsWith("HostInterfaceRequired", StringComparison.Ordinal) || error.StartsWith("RequiredCapabilityMissing", StringComparison.Ordinal)
                    ? _strings.Format("ModuleHostRequired", minimumHost) : _strings.Get("DlcOperationFailed"), TextWrapping = TextWrapping.Wrap });
            }
            var buttons = new StackPanel { Spacing = 8 };
            var canAct = !IsBusy && installation.Transaction == ModuleTransactionState.None;
            var canEnable = !IsBusy && (installation.Transaction is ModuleTransactionState.None or ModuleTransactionState.PendingCleanup);
            // Retirement cancels the runtime generation synchronously before waiting for its
            // transaction gate. A cleanup receipt must never trap a running or failed worker.
            var canRetire = !_retiring.Contains(installation.Id);
            if (installation.Version is null || package?.Version is { } available && Version.Parse(available) > Version.Parse(installation.Version))
                AddButton(buttons, "DlcDownload", installation.Id, canAct, () => _runtime.InstallAsync(installation.Id));
            if (installation.Version is not null)
            {
                if (!installation.Enabled || snapshot.RunState == ModuleRunState.Faulted)
                    AddButton(buttons, snapshot.RunState == ModuleRunState.Faulted ? "DlcRetry" : "ModuleEnable", installation.Id,
                        canEnable, () => _runtime.EnableAsync(installation.Id));
                if (manifest is { Ui.Settings.Length: > 0 } && installation.Enabled && snapshot.RunState == ModuleRunState.Running)
                {
                    var settings = new Button { Content = _strings.Get("PageTitleSettings"), IsEnabled = canAct, HorizontalAlignment = HorizontalAlignment.Left };
                    AutomationProperties.SetName(settings, _strings.Get("PageTitleSettings"));
                    settings.Click += (_, _) => _openSettings(manifest); buttons.Children.Add(settings);
                }
            }
            if (installation.Enabled || snapshot.RunState != ModuleRunState.Stopped)
                AddButton(buttons, "ModuleDisable", installation.Id, canRetire, () => _runtime.DisableAsync(installation.Id), retire: true);
            if (installation.Version is not null || installation.Transaction != ModuleTransactionState.None)
                AddButton(buttons, "DlcDelete", installation.Id, canRetire, () => _runtime.UninstallAsync(installation.Id), retire: true);
            if (installation.Transaction == ModuleTransactionState.PendingCleanup)
                AddButton(buttons, "ModuleRetryCleanup", installation.Id, !IsBusy, () => _runtime.RetryCleanupAsync(installation.Id));
            if (installation.DataVersion > 0 || installation.ErrorCode == "ModuleStateInvalid")
                AddButton(buttons, "ModuleClearData", installation.Id, canEnable && !installation.Enabled && snapshot.RunState == ModuleRunState.Stopped, async () =>
                {
                    if (_lifetime is not { IsCancellationRequested: false } owner) return;
                    var token = owner.Token;
                    var dialog = new ContentDialog
                    {
                        XamlRoot = XamlRoot, Title = _strings.Get("ModuleClearData"),
                        Content = new TextBlock { Text = _strings.Get("ModuleClearDataConfirm"), TextWrapping = TextWrapping.Wrap },
                        PrimaryButtonText = _strings.Get("ModuleClearData"), CloseButtonText = _strings.Get("CommonCancel"),
                        DefaultButton = ContentDialogButton.Close,
                    };
                    if (await ContentDialogLifetime.ShowAsync(dialog, token) == ContentDialogResult.Primary &&
                        !token.IsCancellationRequested && ReferenceEquals(owner, _lifetime)) await _runtime.ClearDataAsync(installation.Id);
                });
            body.Children.Add(buttons);
            _cards.Children.Add(new Border { Child = body, Padding = new(16), CornerRadius = new(12), Background = Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] as Brush });
        }
    }

    private void AddButton(StackPanel body, string key, string id, bool enabled, Func<Task> action, bool retire = false)
    {
        var button = new Button { Content = new TextBlock { Text = _strings.Get(key), TextWrapping = TextWrapping.Wrap }, IsEnabled = enabled, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(button, _strings.Get(key));
        AutomationProperties.SetAutomationId(button, key + "-" + id);
        button.Click += async (_, _) =>
        {
            if (!enabled || !_loaded || !_active || retire && !_retiring.Add(id) || !retire && IsBusy) return;
            _operations++; _error.Visibility = Visibility.Collapsed; Render();
            var owner = _lifetime;
            try { await action(); }
            catch (OperationCanceledException) { }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                System.Diagnostics.Debug.WriteLine("Module inventory operation failed: " + error.GetType().Name);
                if (_loaded && ReferenceEquals(owner, _lifetime)) { _error.Text = _strings.Get("DlcOperationFailed"); _error.Visibility = Visibility.Visible; }
            }
            finally { _operations--; if (retire) _retiring.Remove(id); if (_loaded && _active) Render(); }
        };
        body.Children.Add(button);
    }
}
