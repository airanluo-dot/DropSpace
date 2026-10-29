using DropSpace.Core.Models;
using DropSpace.App.Services;
using DropSpace.App.ViewModels;
using DropSpace.App.Views.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using DropSpace.Core.SystemActivities;
using Microsoft.Extensions.Logging;

namespace DropSpace.App.Views;

public sealed partial class MainPage
{
    private Action? _openDlcSettings;
    private DlcPage? _dlcSettingsPage;
    private DownloadPanel? _downloadSettingsPanel;
    private FeatureModuleInventoryView? _moduleInventory;
    private string _selectedSettingsPage = "General";
    private NavigationView? _settingsNavigation;

    internal async Task<bool> ConfirmAgentOperationAsync(string operation, string details, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _dialogLifetime.Token);
        lifetime.Token.ThrowIfCancellationRequested();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot ?? throw new InvalidOperationException("The main window is not ready."),
            Title = _strings.Get("AgentConfirmTitle"),
            Content = new ScrollViewer { MaxHeight = 320, Content = new TextBlock
            { Text = operation[..Math.Min(operation.Length, 128)] + "\n\n" + details[..Math.Min(details.Length, 4000)], TextWrapping = TextWrapping.Wrap } },
            PrimaryButtonText = _strings.Get("AgentConfirmAllow"), CloseButtonText = _strings.Get("Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };
        return await ContentDialogLifetime.ShowAsync(dialog, lifetime.Token) == ContentDialogResult.Primary;
    }

    internal async Task OpenSettingsPageAsync(string key)
    {
        var item = _settingsNavigation?.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(item => Equals(item.Tag, key))
            ?? throw new ArgumentException("Unknown settings page.", nameof(key));
        var ownerToken = _dialogLifetime.Token;
        var ownership = await SelectSectionAsync("Settings");
        if (ownership is { } current && OwnsNavigation(current, ownerToken) && _viewModel.CurrentSection == "Settings")
            _settingsNavigation!.SelectedItem = item;
    }

    private void OpenDlcSettings() => _openDlcSettings?.Invoke();

    private void BuildSettingsPages(NativeSettingsEditor editor, Services.Dlc.DlcManagerService dlc)
    {
        var groups = new Dictionary<string, StackPanel>();
        foreach (var key in new[] { "General", "Island", "Shortcuts", "Relay", "Agents", "Widgets", "DLC", "Downloads", "SystemActivities", "Devices", "Updates", "About" })
            groups[key] = new() { Spacing = 18, MaxWidth = 780, HorizontalAlignment = HorizontalAlignment.Left };
        var sections = LegacySettingsSections.Children.OfType<StackPanel>().ToArray();
        LegacySettingsSections.Children.Clear();
        foreach (var section in sections)
        {
            var heading = section.Children.OfType<TextBlock>().FirstOrDefault();
            var uid = heading is null ? string.Empty : XamlResourceOverride.GetUid(heading);
            if (uid == "AppearanceSection")
            {
                var rows = section.Children.ToArray(); section.Children.Clear();
                foreach (var row in rows)
                {
                    if (ReferenceEquals(row, heading)) continue;
                    var general = ReferenceEquals(row, QuickActionsSettingsPanel) || ContainsElement(row, StartWithWindowsToggle) ||
                        ContainsElement(row, LanguageCombo) || ContainsElement(row, ThemeCombo) || ContainsElement(row, CloseBehaviorCombo);
                    groups[general ? "General" : "Island"].Children.Add(row);
                }
            }
            else groups[uid == "DevicesSharingSection" ? "Devices" : uid == "UpdatesSection" ? "Updates" : "General"].Children.Add(section);
        }
        BuildKeyboardShortcutsPage(groups["Shortcuts"]);
        groups["Relay"].Children.Add(new FileRelayPanel(editor, _strings, _windowHandle));
        groups["Agents"].Children.Add(new AgentConnectionsPanel(editor.Agents.Value, editor.AgentSkill, _strings, _viewModel.CopyAgentInvitationAsync));
        var island = new SettingsForm(editor, _strings);
        island.AddChoice("IslandMaterialStyle", new[]
        {
            (IslandMaterialStyle.Acrylic, _strings.Get("IslandMaterialAcrylic")),
            (IslandMaterialStyle.LiquidGlass, _strings.Get("IslandMaterialLiquidGlass")),
        }, s => s.IslandAppearance.MaterialStyle,
            (s, v) => s with { IslandAppearance = s.IslandAppearance with { MaterialStyle = v } });
        var glassSettings = new SettingsForm(editor, _strings);
        glassSettings.AddSlider("IslandGlassBlur", 0, 30, 1, s => s.IslandAppearance.LiquidGlassBlur,
            (s, v) => s with { IslandAppearance = s.IslandAppearance with { LiquidGlassBlur = v } }, v => v.ToString("0", _strings.Culture));
        glassSettings.AddSlider("IslandGlassRefraction", 0, 400, 10, s => s.IslandAppearance.LiquidGlassRefraction,
            (s, v) => s with { IslandAppearance = s.IslandAppearance with { LiquidGlassRefraction = v } }, v => v.ToString("0", _strings.Culture) + "%");
        glassSettings.AddSlider("IslandGlassRefractionWidth", 50, 300, 10, s => s.IslandAppearance.LiquidGlassRefractionWidth,
            (s, v) => s with { IslandAppearance = s.IslandAppearance with { LiquidGlassRefractionWidth = v } }, v => v.ToString("0", _strings.Culture) + "%");
        glassSettings.AddSlider("IslandGlassDispersion", 0, 100, 5, s => s.IslandAppearance.LiquidGlassDispersion,
            (s, v) => s with { IslandAppearance = s.IslandAppearance with { LiquidGlassDispersion = v } }, v => v.ToString("0", _strings.Culture));
        glassSettings.AddSlider("IslandGlassDarkOverlay", 0, 200, 10, s => s.IslandAppearance.LiquidGlassDarkOverlay,
            (s, v) => s with { IslandAppearance = s.IslandAppearance with { LiquidGlassDarkOverlay = v } }, v => v.ToString("0", _strings.Culture) + "%");
        glassSettings.AddToggle("IslandGlassBlackGradient", s => s.IslandAppearance.LiquidGlassBlackGradient,
            (s, v) => s with { IslandAppearance = s.IslandAppearance with { LiquidGlassBlackGradient = v } });
        glassSettings.AddChoice("IslandGlassRefreshRate", new[]
        {
            (IslandGlassRefreshRate.Standard, _strings.Get("IslandGlassRefreshStandard")),
            (IslandGlassRefreshRate.High, _strings.Get("IslandGlassRefreshHigh")),
            (IslandGlassRefreshRate.VeryHigh, _strings.Get("IslandGlassRefreshVeryHigh")),
            (IslandGlassRefreshRate.Ultra, _strings.Get("IslandGlassRefreshUltra")),
        }, s => s.IslandAppearance.LiquidGlassRefreshRate,
            (s, v) => s with { IslandAppearance = s.IslandAppearance with { LiquidGlassRefreshRate = v } });
        island.AddConditionalContent(glassSettings, s => s.IslandAppearance.MaterialStyle == IslandMaterialStyle.LiquidGlass);
        island.AddChoice<ThemePreference>("IslandAppearance", s => s.IslandAppearance.MaterialStyle == IslandMaterialStyle.LiquidGlass
            ? new[] { (s.IslandAppearance.Theme, _strings.Get("IslandAppearanceLiquidGlass")) }
            : new[]
        {
            (ThemePreference.System, _strings.Get("IslandAppearanceFollow")),
            (ThemePreference.Light, _strings.Get("IslandAppearanceLight")),
            (ThemePreference.Dark, _strings.Get("IslandAppearanceDark")),
        }, s => s.IslandAppearance.Theme,
            (s, v) => s.IslandAppearance.MaterialStyle == IslandMaterialStyle.LiquidGlass
                ? s : s with { IslandAppearance = s.IslandAppearance with { Theme = v } });
        island.AddChoice("IslandContentPriority", new[]
        {
            (IslandContentPriority.Music, _strings.Get("IslandContentPriorityMusic")),
            (IslandContentPriority.TemporarySpace, _strings.Get("IslandContentPriorityTemporarySpace")),
        }, s => s.IslandContentPriority, (s, v) => s with { IslandContentPriority = v });
        island.AddToggle("IslandShowLogoWhenIdle", s => s.IslandAppearance.ShowLogoWhenIdle,
            (s, v) => s with { IslandAppearance = s.IslandAppearance with { ShowLogoWhenIdle = v } });
        island.AddToggle("IslandResident", s => s.IslandAppearance.Resident,
            (s, v) => s with { IslandAppearance = s.IslandAppearance with { Resident = v } });
        island.AddToggle("IslandHideWhileMusicWindowVisible",
            s => s.IslandAppearance.HideWhileMusicWindowVisible,
            (s, v) => s with { IslandAppearance = s.IslandAppearance with { HideWhileMusicWindowVisible = v } });
        var forceFullscreen = island.AddToggle("IslandForceShowOverFullscreen",
            s => s.IslandAppearance.ForceShowOverFullscreen,
            (s, v) => s with { IslandAppearance = s.IslandAppearance with { ForceShowOverFullscreen = v } });
        var fullscreenDescription = _strings.Get("IslandForceShowOverFullscreenDescription");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(forceFullscreen, fullscreenDescription);
        island.Rows.Children.Add(new TextBlock
        {
            Text = fullscreenDescription, TextWrapping = TextWrapping.Wrap, Margin = new(14, 0, 14, 8),
        });
        island.AddSlider("IslandHideDelay", 0, 10, 1, s => s.IslandAppearance.HideDelayMilliseconds / 1000d,
            (s, v) => s with { IslandAppearance = s.IslandAppearance with { HideDelayMilliseconds = (int)v * 1000 } },
            v => _strings.Format("IslandDelaySeconds", v.ToString("0", _strings.Culture)));
        island.AddSlider("IslandCompactScale", 0.5, 2, 0.1, s => s.IslandAppearance.CompactScale,
            (s, v) => s with { IslandAppearance = s.IslandAppearance with { CompactScale = v } }, v => v.ToString("0.0#", _strings.Culture) + "×");
        island.AddSlider("IslandExpandedScale", 0.5, 2, 0.1, s => s.IslandAppearance.ExpandedScale,
            (s, v) => s with { IslandAppearance = s.IslandAppearance with { ExpandedScale = v } }, v => v.ToString("0.0#", _strings.Culture) + "×");
        island.AddToggle("IslandRightHoldMove", s => s.IslandAppearance.RightClickHoldToMove, (s,v) => s with { IslandAppearance = s.IslandAppearance with { RightClickHoldToMove = v } });
        groups["Island"].Children.Insert(0, island);
        groups["Widgets"].Children.Add(new WidgetEditorView(editor, _strings));
        _dlcSettingsPage = new DlcPage(dlc, _strings, editor, _modules.RefreshCatalogAsync);
        _downloadSettingsPanel = new DownloadPanel(editor, _strings, _windowHandle);
        groups["DLC"].Children.Add(_dlcSettingsPage);
        _moduleInventory = new FeatureModuleInventoryView(_modules, _strings,
            manifest =>
            {
                // This user entry bypasses SelectSectionAsync but still retires pending navigation.
                Interlocked.Increment(ref _navigationRevision);
                _viewModel.ReserveNavigation();
                ShowModulePage(null, manifest, null);
            });
        groups["DLC"].Children.Add(_moduleInventory);
        groups["Downloads"].Children.Add(_downloadSettingsPanel);
        var activities = new SettingsForm(editor, _strings);
        activities.AddToggle("ActivitiesNotifications", s => s.SystemActivities.ShowWindowsNotifications, (s,v) => s with { SystemActivities = s.SystemActivities with { ShowWindowsNotifications = v } }, editor.CheckNotificationAccessAsync);
        activities.AddToggle("ActivitiesVolume", s => s.SystemActivities.ShowVolumeChanges, (s,v) => s with { SystemActivities = s.SystemActivities with { ShowVolumeChanges = v } });
        groups["SystemActivities"].Children.Add(activities);
        var about = groups["About"];
        about.HorizontalAlignment = HorizontalAlignment.Center;
        about.Spacing = 12;
        about.Margin = new(0, 32, 0, 32);
        var logo = new Image
        {
            Width = 112, Height = 112, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 0, 0, 12),
        };
        logo.Loaded += OnAboutLogoLoaded;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(logo, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        about.Children.Add(logo);
        var version = new TextBlock { FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center };
        version.SetBinding(TextBlock.TextProperty, new Binding { Source = _viewModel, Path = new PropertyPath(nameof(MainViewModel.CurrentVersionDisplayText)) });
        groups["About"].Children.Add(version);
        var deployment = new TextBlock { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
        deployment.SetBinding(TextBlock.TextProperty, new Binding { Source = _viewModel, Path = new PropertyPath(nameof(MainViewModel.DeploymentModeText)) });
        groups["About"].Children.Add(deployment);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(24, 0, 24, 0) };
        error.SetBinding(TextBlock.TextProperty, new Binding { Source = editor, Path = new PropertyPath(nameof(NativeSettingsEditor.Error)) });
        var navigation = new NavigationView { PaneDisplayMode = NavigationViewPaneDisplayMode.Top, IsSettingsVisible = false, IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed };
        _settingsNavigation = navigation;
        var pageHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        var body = new Grid(); body.RowDefinitions.Add(new() { Height = GridLength.Auto }); body.RowDefinitions.Add(new());
        body.Children.Add(error); Grid.SetRow(pageHost, 1); body.Children.Add(pageHost); navigation.Content = body;
        var pages = groups.ToDictionary(pair => pair.Key, pair => new ScrollViewer { Content = pair.Value, Padding = new(24), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        pages["About"].VerticalContentAlignment = VerticalAlignment.Center;
        foreach (var page in pages.Values) SettingsEditBehavior.Attach(page, this);
        foreach (var (key, page) in pages) page.SizeChanged += (_, _) => groups[key].Width = Math.Clamp(page.ActualWidth - 48, 0, 780);
        foreach (var key in groups.Keys)
        {
            if (key == "SystemActivities" && !SystemActivityPolicy.IsAvailable) continue;
            var resourceKey = key switch
            {
                "General" => "SettingsPageGeneral", "Island" => "SettingsPageIsland", "Shortcuts" => "SettingsPageShortcuts",
                "Relay" => "RelayTitle", "Agents" => "AgentConnectionsTitle", "Widgets" => "SettingsPageWidgets", "DLC" => "SettingsPageDlc", "Downloads" => "SettingsPageDownloads", "SystemActivities" => "SettingsPageSystemActivities",
                "Devices" => "SettingsPageDevices", "Updates" => "SettingsPageUpdates", _ => "SettingsPageAbout",
            };
            navigation.MenuItems.Add(new NavigationViewItem { Content = _strings.Get(resourceKey), Tag = key });
        }
        navigation.SelectionChanged += (sender, args) =>
        {
            if (args.SelectedItem is not NavigationViewItem { Tag: string key }) return;
            _selectedSettingsPage = key;
            if (key == "Shortcuts") RefreshKeyboardShortcuts();
            if (key == "General") _ = _viewModel.RefreshStorageSummaryAsync(_dialogLifetime.Token);
            UpdatePresentationActivity();
            pageHost.Content = pages[key];
        };
        navigation.SelectedItem = navigation.MenuItems[0]; pageHost.Content = pages["General"];
        _openDlcSettings = async () =>
        {
            var ownerToken = _dialogLifetime.Token;
            var ownership = await SelectSectionAsync("Settings");
            if (ownership is not { } current || !OwnsNavigation(current, ownerToken) ||
                _viewModel.CurrentSection != "Settings") return;
            navigation.SelectedItem = navigation.MenuItems.OfType<NavigationViewItem>().Single(item => Equals(item.Tag, "DLC"));
        };
        SettingsPages.Content = navigation;
    }

    private async void OnAboutLogoLoaded(object sender, RoutedEventArgs args)
    {
        if (sender is not Image { Source: null } logo) return;
        try
        {
            // Read embedded bytes in both packaged and single-file deployments.
            using var resource = typeof(MainPage).Assembly.GetManifestResourceStream("DropSpace.Brand.AboutLogo.png")
                ?? throw new InvalidDataException("Embedded About logo is missing.");
            using var bytes = new MemoryStream(); resource.CopyTo(bytes);
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var writer = new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes.ToArray()); await writer.StoreAsync(); writer.DetachStream();
            }
            stream.Seek(0);
            var image = new BitmapImage();
            await image.SetSourceAsync(stream);
            if (logo.IsLoaded && !_dialogLifetime.IsCancellationRequested) logo.Source = image;
        }
        catch (Exception error) { _logger.LogWarning("About logo decode failed ({Category}).", error.GetType().Name); }
    }
    private void UpdateSettingsPresentationActivity(bool active)
    {
        _dlcSettingsPage?.SetActive(active && _selectedSettingsPage == "DLC");
        _moduleInventory?.SetActive(active && _selectedSettingsPage == "DLC");
        _downloadSettingsPanel?.SetActive(active && _selectedSettingsPage == "Downloads");
    }
    private static bool ContainsElement(DependencyObject root, DependencyObject target)
    {
        if (ReferenceEquals(root, target)) return true;
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            if (ContainsElement(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i), target)) return true;
        return false;
    }
}
