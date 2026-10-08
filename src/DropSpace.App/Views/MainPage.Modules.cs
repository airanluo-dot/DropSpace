using DropSpace.App.Services.Dlc;
using DropSpace.App.Views.Settings;
using DropSpace.Core.Dlc;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DropSpace.App.Views;

public sealed partial class MainPage
{
    private readonly FeatureModuleRuntime _modules;
    private readonly Dictionary<string, ModuleNavigation> _moduleNavigation = new(StringComparer.Ordinal);
    private string? _selectedModuleKey, _selectedModuleId;
    private FeatureModulePage? _modulePage;
    private int _moduleRefreshQueued;
    internal bool IsModulePageVisible => _selectedModuleId is not null;
    internal event EventHandler? ModulePresentationChanged;
    private sealed record ModuleNavigation(string Id, ModuleManifest Manifest, ModulePage Page, NavigationViewItem Item);

    private bool IsModuleActive(string id) => _modules.Snapshots.Any(item => item.Installation.Id == id &&
        item.Installation.Enabled && item.RunState == ModuleRunState.Running && item.Manifest is not null);

    private void OnModulesChanged(object? sender, EventArgs args)
    {
        if (Interlocked.Exchange(ref _moduleRefreshQueued, 1) != 0) return;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _moduleRefreshQueued, 0);
            if (!_dialogLifetime.IsCancellationRequested) RefreshModuleNavigation();
        })) Interlocked.Exchange(ref _moduleRefreshQueued, 0);
    }

    private void RefreshModuleNavigation()
    {
        var current = _modules.Snapshots.Where(item => item.Installation.Enabled && item.RunState == ModuleRunState.Running && item.Manifest is not null).ToArray();
        var pages = current.SelectMany(item => item.Manifest!.Ui.Pages.Select(page =>
            (Key: "module:" + item.Installation.Id + "/" + page.Id, Id: item.Installation.Id, Manifest: item.Manifest!, Page: page))).ToArray();
        var liveKeys = pages.Select(page => page.Key).ToHashSet(StringComparer.Ordinal);
        if (_selectedModuleId is { } selected && (!current.Any(item => item.Installation.Id == selected) ||
            _selectedModuleKey is { } selectedKey && !liveKeys.Contains(selectedKey)))
        {
            Interlocked.Increment(ref _navigationRevision);
            RetireModulePage();
            SyncNavigationSelection();
            UpdateSectionChrome();
            GetNavigationItem(_viewModel.CurrentSection).Focus(FocusState.Programmatic);
        }
        _syncingNavigation = true;
        try
        {
            foreach (var obsolete in _moduleNavigation.Keys.Where(key => !liveKeys.Contains(key)).ToArray())
            {
                Navigation.MenuItems.Remove(_moduleNavigation[obsolete].Item);
                _moduleNavigation.Remove(obsolete);
            }
            foreach (var page in pages)
            {
                if (_moduleNavigation.TryGetValue(page.Key, out var existing))
                {
                    existing.Item.Content = ModuleContract.Text(page.Manifest, page.Page.Title, _strings.Culture.Name);
                    _moduleNavigation[page.Key] = existing with { Manifest = page.Manifest, Page = page.Page };
                    continue;
                }
                var glyph = page.Manifest.Ui.Icon switch { "Document" => "\uE8A5", "Tools" => "\uE713", _ => "\uEA86" };
                var item = new NavigationViewItem
                {
                    Content = ModuleContract.Text(page.Manifest, page.Page.Title, _strings.Culture.Name),
                    Tag = page.Key, Icon = new FontIcon { Glyph = glyph },
                };
                AutomationProperties.SetName(item, ModuleContract.Text(page.Manifest, page.Page.Title, _strings.Culture.Name));
                _moduleNavigation[page.Key] = new(page.Id, page.Manifest, page.Page, item);
                Navigation.MenuItems.Add(item);
            }
        }
        finally { _syncingNavigation = false; }
    }

    private void ShowModulePage(string? navigationKey, ModuleManifest manifest, ModulePage? page)
    {
        if (_dialogLifetime.IsCancellationRequested || !IsModuleActive(manifest.Id)) return;
        RetireModulePage();
        _selectedModuleKey = navigationKey; _selectedModuleId = manifest.Id;
        _modulePage = new FeatureModulePage(_modules, _strings, manifest, page, () =>
        {
            Interlocked.Increment(ref _navigationRevision);
            RetireModulePage(); SyncNavigationSelection(); UpdateSectionChrome();
            GetNavigationItem(_viewModel.CurrentSection).Focus(FocusState.Programmatic);
        });
        ModuleContent.Content = _modulePage;
        ModuleContent.Visibility = Visibility.Visible;
        BuiltInSurfaces.Visibility = Visibility.Collapsed;
        SyncNavigationSelection();
        UpdatePresentationActivity();
        ModulePresentationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RetireModulePage()
    {
        if (_modulePage is null && _selectedModuleId is null) return;
        _modulePage?.Retire(); _modulePage = null;
        _selectedModuleKey = null; _selectedModuleId = null;
        ModuleContent.Content = null; ModuleContent.Visibility = Visibility.Collapsed;
        BuiltInSurfaces.Visibility = Visibility.Visible;
        ModulePresentationChanged?.Invoke(this, EventArgs.Empty);
    }
}
