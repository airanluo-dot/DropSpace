using DropSpace.App.Services;
using DropSpace.App.ViewModels;
using DropSpace.App.Views.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace DropSpace.App.Views;

public sealed partial class MainPage
{
    private void BuildSettingsPages(NativeSettingsEditor editor)
    {
        var groups = new Dictionary<string, StackPanel>();
        foreach (var key in new[] { "General", "Island", "Widgets", "SystemActivities", "Devices", "Updates", "About" })
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
        var island = new SettingsForm(editor, _strings);
        island.AddToggle("IslandAutoHide", s => s.IslandAppearance.AutoHide, (s,v) => s with { IslandAppearance = s.IslandAppearance with { AutoHide = v } });
        island.AddNumber("IslandHideDelay", 500, 30000, 500, s => s.IslandAppearance.HideDelayMilliseconds, (s,v) => s with { IslandAppearance = s.IslandAppearance with { HideDelayMilliseconds = (int)v } });
        island.AddNumber("IslandCompactScale", 0.5, 2, 0.1, s => s.IslandAppearance.CompactScale, (s,v) => s with { IslandAppearance = s.IslandAppearance with { CompactScale = v } });
        island.AddNumber("IslandExpandedScale", 0.5, 2, 0.1, s => s.IslandAppearance.ExpandedScale, (s,v) => s with { IslandAppearance = s.IslandAppearance with { ExpandedScale = v } });
        island.AddToggle("IslandRightHoldMove", s => s.IslandAppearance.RightClickHoldToMove, (s,v) => s with { IslandAppearance = s.IslandAppearance with { RightClickHoldToMove = v } });
        groups["Island"].Children.Insert(0, island);
        groups["Widgets"].Children.Add(new WidgetEditorView(editor, _strings));
        var activities = new SettingsForm(editor, _strings);
        activities.AddToggle("ActivitiesNotifications", s => s.SystemActivities.ShowWindowsNotifications, (s,v) => s with { SystemActivities = s.SystemActivities with { ShowWindowsNotifications = v } });
        activities.AddToggle("ActivitiesVolume", s => s.SystemActivities.ShowVolumeChanges, (s,v) => s with { SystemActivities = s.SystemActivities with { ShowVolumeChanges = v } });
        activities.AddToggle("ActivitiesFullscreen", s => s.SystemActivities.SuppressOverFullscreen, (s,v) => s with { SystemActivities = s.SystemActivities with { SuppressOverFullscreen = v } });
        groups["SystemActivities"].Children.Add(activities);
        var version = new TextBlock { FontSize = 22 };
        version.SetBinding(TextBlock.TextProperty, new Binding { Source = _viewModel, Path = new PropertyPath(nameof(MainViewModel.CurrentVersionDisplayText)) });
        groups["About"].Children.Add(version);
        var deployment = new TextBlock { TextWrapping = TextWrapping.Wrap };
        deployment.SetBinding(TextBlock.TextProperty, new Binding { Source = _viewModel, Path = new PropertyPath(nameof(MainViewModel.DeploymentModeText)) });
        groups["About"].Children.Add(deployment);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(24, 0, 24, 0) };
        error.SetBinding(TextBlock.TextProperty, new Binding { Source = editor, Path = new PropertyPath(nameof(NativeSettingsEditor.Error)) });
        var navigation = new NavigationView { PaneDisplayMode = NavigationViewPaneDisplayMode.Top, IsSettingsVisible = false, IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed };
        var pageHost = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        var body = new Grid(); body.RowDefinitions.Add(new() { Height = GridLength.Auto }); body.RowDefinitions.Add(new());
        body.Children.Add(error); Grid.SetRow(pageHost, 1); body.Children.Add(pageHost); navigation.Content = body;
        var pages = groups.ToDictionary(pair => pair.Key, pair => new ScrollViewer { Content = pair.Value, Padding = new(24), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        foreach (var (key, page) in pages) page.SizeChanged += (_, _) => groups[key].Width = Math.Clamp(page.ActualWidth - 48, 0, 780);
        foreach (var key in groups.Keys) navigation.MenuItems.Add(new NavigationViewItem { Content = _strings.Get("SettingsPage" + key), Tag = key });
        navigation.SelectionChanged += (_, args) => { if (args.SelectedItem is NavigationViewItem { Tag: string key }) pageHost.Content = pages[key]; };
        navigation.SelectedItem = navigation.MenuItems[0]; pageHost.Content = pages["General"];
        SettingsPages.Content = navigation;
    }
    private static bool ContainsElement(DependencyObject root, DependencyObject target)
    {
        if (ReferenceEquals(root, target)) return true;
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            if (ContainsElement(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i), target)) return true;
        return false;
    }
}
