using System.ComponentModel;
using DropSpace.App.Services;
using DropSpace.App.Services.Dlc;
using DropSpace.App.Services.Widgets;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Dlc;
using System.Text.Json;
using DropSpace.App.ViewModels;
using DropSpace.Core.Widgets;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DropSpace.App.Views.Island;

public sealed partial class WidgetsExpandedView : UserControl
{
    private WidgetViewModel? _view;
    private readonly Dictionary<NativeWidgetId, TextBlock> _values = [];
    private readonly Dictionary<NativeWidgetId, WidgetPlacement> _placements = [];
    private readonly Dictionary<NativeWidgetId, Button> _timerButtons = [];
    private readonly Dictionary<NativeWidgetId, Button> _timerValueButtons = [];
    private readonly List<MenuFlyout> _timerFlyouts = [];
    private Button? _stopwatchButton, _clipboardButton;
    private bool _active;
    private bool _subscribed;
    private bool _structureDirty = true;
    private bool _dataDirty = true;
    private CancellationTokenSource? _nativeActionRequests;
    private FeatureModuleRuntime? _modules;
    private IAppStringLocalizer? _moduleStrings;
    private CancellationTokenSource? _moduleRequests;
    private readonly List<Flyout> _moduleFlyouts = [];
    private IReadOnlyList<(string Id, ModuleManifest Manifest, ModuleIslandContent Content)> _renderedModules = [];
    private string? _moduleLanguage;
    private bool _modulesDirty = true;
    public event EventHandler? SettingsRequested;
    public event EventHandler? PinnedRequested;
    public WidgetsExpandedView()
    {
        InitializeComponent();
        for (var i = 0; i < WidgetLayoutPolicy.Columns; i++) Tiles.ColumnDefinitions.Add(new());
        for (var i = 0; i < WidgetLayoutPolicy.Rows; i++) Tiles.RowDefinitions.Add(new());
        Loaded += OnLoaded; Unloaded += OnUnloaded;
    }
    public WidgetViewModel? ViewModel
    {
        get => _view;
        set
        {
            if (ReferenceEquals(_view, value)) return;
            RetireNativeActions();
            CloseTimerMenus();
            Unsubscribe();
            _view?.SetVisible(this, false);
            _view = value;
            if (IsLoaded) Subscribe();
            _view?.SetVisible(this, _active && IsLoaded);
            _structureDirty = true;
            RenderPending();
        }
    }
    public void RefreshLanguage()
    {
        _structureDirty = true;
        _dataDirty = true;
        _modulesDirty = true;
        RenderPending();
    }
    public void SetActive(bool active)
    {
        _active = active;
        if (!active) { RetireNativeActions(); CloseTimerMenus(); RetireModuleContent(); }
        _view?.SetVisible(this, active && IsLoaded);
        RenderPending();
    }
    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        Subscribe();
        _view?.SetVisible(this, _active);
        // Settings may have changed while the unloaded view was unsubscribed.
        _structureDirty = true;
        RenderPending();
    }
    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        RetireNativeActions();
        Unsubscribe();
        CloseTimerMenus();
        _view?.SetVisible(this, false);
        _structureDirty = true;
        _dataDirty = true;
        RetireModuleContent();
    }
    private void Subscribe()
    {
        if (_subscribed || _view is null) return;
        _view.PropertyChanged += OnChanged;
        _subscribed = true;
    }
    private void Unsubscribe()
    {
        if (_subscribed && _view is not null) _view.PropertyChanged -= OnChanged;
        _subscribed = false;
    }
    private void OnChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(WidgetViewModel.Snapshot) or nameof(WidgetViewModel.StopwatchText) or nameof(WidgetViewModel.ClipboardPauseLabel))
            _dataDirty = true;
        else
            _structureDirty = true;
        RenderPending();
    }
    private void RenderPending()
    {
        // Collapsing the native host or an ancestor does not unload this view.
        // Retain invalidation until this particular host presents Widgets again.
        if (!_active || !IsLoaded) return;
        if (_structureDirty)
        {
            Rebuild();
            _structureDirty = false;
            _dataDirty = false;
        }
        else if (_dataDirty)
        {
            RenderData();
            _dataDirty = false;
        }
        if (_modulesDirty) RenderModules();
    }

    public void SetModuleRuntime(FeatureModuleRuntime? modules, IAppStringLocalizer strings)
    {
        RetireModuleContent(); _modules = modules; _moduleStrings = strings; _modulesDirty = true;
        RenderPending();
    }
    public void RefreshModules()
    {
        var contents = _modules?.IslandContents ?? [];
        if (_moduleLanguage == _moduleStrings?.Culture.Name && contents.Count == _renderedModules.Count &&
            contents.Zip(_renderedModules).All(pair => pair.First.Id == pair.Second.Id && ReferenceEquals(pair.First.Content, pair.Second.Content))) return;
        _modulesDirty = true; RenderPending();
    }
    private void RetireModuleContent()
    {
        foreach (var flyout in _moduleFlyouts) flyout.Hide();
        _moduleFlyouts.Clear();
        _renderedModules = [];
        _moduleRequests?.Cancel(); _moduleRequests?.Dispose(); _moduleRequests = null;
        ModuleCards.Children.Clear(); ModuleStrip.Visibility = Visibility.Collapsed; _modulesDirty = true;
    }
    private void RenderModules()
    {
        RetireModuleContent(); _modulesDirty = false;
        if (_modules is null || _moduleStrings is null) return;
        var contents = _modules.IslandContents;
        _renderedModules = contents; _moduleLanguage = _moduleStrings.Culture.Name;
        if (contents.Count == 0) return;
        _moduleRequests = new();
        var owner = _moduleRequests; var token = owner.Token;
        foreach (var (id, manifest, content) in contents)
        {
            string Text(ModuleText text) => ModuleContract.Text(manifest, text, _moduleStrings.Culture.Name);
            var expanded = new StackPanel { Spacing = 10, MaxWidth = 360 };
            expanded.Children.Add(new TextBlock { Text = Text(manifest.Name), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            expanded.Children.Add(new TextBlock { Text = Text(content.Expanded), TextWrapping = TextWrapping.Wrap });
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed }; expanded.Children.Add(error);
            var flyout = new Flyout { Content = expanded }; _moduleFlyouts.Add(flyout);
            foreach (var action in content.Actions)
            {
                var button = new Button { Content = new TextBlock { Text = Text(action.Label), TextWrapping = TextWrapping.Wrap }, HorizontalAlignment = HorizontalAlignment.Left };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, Text(action.Label));
                button.Click += async (_, _) =>
                {
                    if (token.IsCancellationRequested || _modules is null || content.ExpiresAt <= DateTimeOffset.UtcNow ||
                        !_modules.IslandContents.Any(item => item.Id == id && ReferenceEquals(item.Content, content))) return;
                    button.IsEnabled = false;
                    try { await _modules.InvokeAsync(id, action.Id, JsonSerializer.SerializeToElement(new { }), token); }
                    catch (OperationCanceledException) { }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        System.Diagnostics.Debug.WriteLine("Module Island action failed: " + exception.GetType().Name);
                        if (!token.IsCancellationRequested) { error.Text = _moduleStrings.Get("DlcOperationFailed"); error.Visibility = Visibility.Visible; }
                    }
                    finally { if (!token.IsCancellationRequested) button.IsEnabled = true; }
                };
                expanded.Children.Add(button);
            }
            var chip = new Button
            {
                Content = new TextBlock { Text = Text(content.Expanded), TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 },
                Flyout = flyout, MaxWidth = 300, MinHeight = 0, Padding = new(6, 1, 6, 1), FontSize = 11,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chip, Text(manifest.Name) + ": " + Text(content.Expanded));
            ModuleCards.Children.Add(chip);
        }
        ModuleStrip.Visibility = Visibility.Visible;
    }
    private void Rebuild()
    {
        RetireNativeActions();
        CloseTimerMenus();
        _timerFlyouts.Clear(); _timerValueButtons.Clear();
        Tiles.Children.Clear(); _timerButtons.Clear(); _values.Clear(); _placements.Clear(); _stopwatchButton = null; _clipboardButton = null;
        if (_view is null) return;
        EmptyText.Visibility = !_view.Enabled || _view.Layout.Expanded.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!_view.Enabled) return;
        foreach (var placement in _view.Layout.Expanded)
        {
            _placements[placement.Id] = placement;
            var title = new TextBlock { FontSize = 11, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.None };
            XamlResourceOverride.Apply(title, "Widget" + placement.Id);
            var value = new TextBlock { FontSize = placement.ColumnSpan == 1 ? 13 : placement.RowSpan == 1 ? 14 : placement.Id == NativeWidgetId.Clock ? 26 : 16, TextWrapping = TextWrapping.Wrap };
            var content = new StackPanel { Spacing = placement.ColumnSpan == 1 ? 2 : 4, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch };
            content.Children.Add(title); content.Children.Add(value);
            FrameworkElement tile;
            if (placement.Id is NativeWidgetId.Calculator or NativeWidgetId.PinnedItems)
            {
                var view = _view;
                var busy = false;
                var button = new Button { Content = content, MinWidth = 0, MinHeight = 0, Padding = new(4), HorizontalContentAlignment = HorizontalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
                AutomationProperties.SetName(button, view.Text("Widget" + placement.Id + ".Text"));
                var icon = new FontIcon { Glyph = NativeWidgetIcons.Glyph(placement.Id), FontSize = 18 };
                AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
                AutomationProperties.SetLiveSetting(value, AutomationLiveSetting.Polite);
                value.TextWrapping = TextWrapping.NoWrap;
                value.TextAlignment = TextAlignment.Center;
                title.TextAlignment = TextAlignment.Center;
                content.Children.Remove(value);
                var artwork = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Child = icon, HorizontalAlignment = HorizontalAlignment.Stretch };
                content.Children.Add(artwork);
                void ShowLaunchFailure()
                {
                    value.Text = view.Text("WidgetLaunchFailed");
                    artwork.Child = value;
                    AutomationProperties.SetHelpText(button, value.Text);
                }
                bool OwnsTile() => IsLoaded && ReferenceEquals(_view, view) && Tiles.Children.Contains(button);
                button.Click += async (_, _) =>
                {
                    if (busy || !_active || !OwnsTile()) return;
                    var token = (_nativeActionRequests ??= new()).Token;
                    bool IsCurrent() => !token.IsCancellationRequested && _active && OwnsTile();
                    busy = true;
                    button.IsEnabled = false;
                    try
                    {
                        artwork.Child = icon;
                        AutomationProperties.SetHelpText(button, string.Empty);
                        if (placement.Id == NativeWidgetId.Calculator)
                        {
                            // Windows owns an accepted launch; retirement only suppresses
                            // this tile's late result and must not claim to cancel that launch.
                            var launched = await Windows.System.Launcher.LaunchUriAsync(new Uri("calculator:"));
                            if (IsCurrent() && !launched)
                                ShowLaunchFailure();
                        }
                        else if (await view.OpenPinnedAsync(token) && IsCurrent())
                            PinnedRequested?.Invoke(this, EventArgs.Empty);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                    catch (Exception) { if (IsCurrent()) ShowLaunchFailure(); }
                    finally
                    {
                        busy = false;
                        // A hidden host can retain this same tile. Restore its button
                        // even when the request was retired, without touching a replacement.
                        if (OwnsTile()) button.IsEnabled = true;
                    }
                };
                tile = button;
            }
            else if (placement.Id == NativeWidgetId.Settings)
            {
                var icon = new FontIcon { Glyph = NativeWidgetIcons.Glyph(placement.Id) };
                AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
                var button = new Button { Content = icon, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
                XamlResourceOverride.Apply(button, "WidgetSettingsAction");
                button.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty); tile = button;
            }
            else
            {
                _values[placement.Id] = value;
                if (placement.Id is NativeWidgetId.FocusTimer or NativeWidgetId.Countdown)
                {
                    var view = _view;
                    content.Children.Clear();
                    var timerButton = new Button
                    {
                        Content = new FontIcon { FontSize = 16 },
                        Padding = new(4), Width = 32, MinWidth = 32, MinHeight = 32,
                    };
                    timerButton.Click += (_, _) => view.ToggleTimer(placement.Id);
                    _timerButtons[placement.Id] = timerButton;
                    var menu = new MenuFlyout();
                    var duration = new MenuFlyoutSubItem { Text = view.Text("WidgetTimerDuration") };
                    var choices = new List<(int Minutes, ToggleMenuFlyoutItem Item)>();
                    foreach (var minutes in new[] { 1, 5, 10, 15, 25, 45, 60 })
                    {
                        var choice = new ToggleMenuFlyoutItem { Text = view.TimerDurationLabel(minutes) };
                        choice.Click += (_, _) =>
                        {
                            if (_active && IsLoaded && ReferenceEquals(_view, view))
                                view.SetTimerDuration(placement.Id, minutes);
                        };
                        duration.Items.Add(choice); choices.Add((minutes, choice));
                    }
                    menu.Opening += (_, _) =>
                    {
                        var timer = placement.Id == NativeWidgetId.FocusTimer ? view.FocusTimer : view.Countdown;
                        foreach (var choice in choices) choice.Item.IsChecked = timer.Duration == TimeSpan.FromMinutes(choice.Minutes);
                    };
                    menu.Items.Add(duration);
                    menu.Items.Add(new MenuFlyoutSeparator());
                    var reset = new MenuFlyoutItem();
                    XamlResourceOverride.Apply(reset, "WidgetTimerReset");
                    reset.Click += (_, _) =>
                    {
                        if (_active && IsLoaded && ReferenceEquals(_view, view)) view.ResetTimer(placement.Id);
                    };
                    menu.Items.Add(reset); content.ContextFlyout = menu;
                    _timerFlyouts.Add(menu);
                    value.TextWrapping = TextWrapping.NoWrap;
                    value.TextTrimming = TextTrimming.CharacterEllipsis;
                    value.TextAlignment = TextAlignment.Center;
                    var durationButton = new Button
                    {
                        Content = value, Flyout = menu, Padding = new(4, 1, 4, 1), MinWidth = 0,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    };
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(durationButton, view.Text("WidgetTimerDurationHelp"));
                    ToolTipService.SetToolTip(durationButton, view.Text("WidgetTimerDuration") + "\n" + view.Text("WidgetTimerDurationHelp"));
                    _timerValueButtons[placement.Id] = durationButton;
                    var controls = new Grid { ColumnSpacing = 4 };
                    controls.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                    controls.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                    controls.Children.Add(durationButton);
                    Grid.SetColumn(timerButton, 1); controls.Children.Add(timerButton);
                    content.Children.Add(controls);
                }
                if (placement.Id == NativeWidgetId.Stopwatch)
                {
                    if (placement.RowSpan == 1) { content.Children.Remove(title); content.Orientation = Orientation.Horizontal; value.VerticalAlignment = VerticalAlignment.Center; }
                    _stopwatchButton = new Button { Command = _view.StopwatchToggleCommand, Padding = new(4, 1, 4, 1), FontSize = 11 };
                    content.Children.Add(_stopwatchButton);
                    var menu = new MenuFlyout(); var reset = new MenuFlyoutItem { Command = _view.StopwatchResetCommand };
                    XamlResourceOverride.Apply(reset, "StopwatchReset"); menu.Items.Add(reset); content.ContextFlyout = menu;
                }
                if (placement.Id == NativeWidgetId.ClipboardPause)
                {
                    _clipboardButton = new Button { Command = _view.ClipboardPauseCommand, Content = new FontIcon { Glyph = "\uE77F", FontSize = 18 }, Padding = new(4), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
                    tile = _clipboardButton;
                }
                else tile = new Border { CornerRadius = new(14), Padding = new(placement.ColumnSpan == 1 ? 4 : 8), Style = (Style)Application.Current.Resources["DropSpaceOverlayWidgetTileStyle"], Child = content };
            }
            ToolTipService.SetToolTip(tile, _view.Text(placement.Id == NativeWidgetId.Settings ? "WidgetSettingsName" : "Widget" + placement.Id + ".Text"));
            Grid.SetColumn(tile, placement.Column); Grid.SetRow(tile, placement.Row);
            Grid.SetColumnSpan(tile, placement.ColumnSpan); Grid.SetRowSpan(tile, placement.RowSpan);
            Tiles.Children.Add(tile);
        }
        RenderData();
    }
    private void RenderData()
    {
        var data = _view?.Snapshot;
        if (_view is null) return;
        foreach (var (id, button) in _timerButtons)
        {
            var timer = id == NativeWidgetId.FocusTimer ? _view.FocusTimer : _view.Countdown;
            ((FontIcon)button.Content).Glyph = timer.IsRunning ? "\uE769" : "\uE768";
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, _view.Text("Widget" + id + ".Text") + ": " + _view.TimerActionLabel(id));
            ToolTipService.SetToolTip(button, _view.TimerActionLabel(id));
        }
        if (_stopwatchButton is not null) { _stopwatchButton.Content = _view.StopwatchActionLabel; Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_stopwatchButton, _view.StopwatchActionLabel); }
        if (_clipboardButton is not null)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_clipboardButton, _view.ClipboardPauseLabel);
            ToolTipService.SetToolTip(_clipboardButton, _view.ClipboardPauseLabel);
            ((FontIcon)_clipboardButton.Content).Glyph = _view.ClipboardPaused ? "\uE768" : "\uE769";
        }
        foreach (var (id, text) in _values)
            text.Text = id switch
            {
                NativeWidgetId.FocusTimer => TimerText(_view.FocusTimer),
                NativeWidgetId.Countdown => TimerText(_view.Countdown),
                NativeWidgetId.Stopwatch => _view.StopwatchText,
                _ when data is null => "—",
                NativeWidgetId.UtcClock => data.LocalTime.UtcDateTime.ToString("HH:mm", _view.Culture) + " UTC",
                NativeWidgetId.DiskSpace => data.DiskFreeBytes is { } free ? $"{free / 1073741824d:0.0} GB" : "—",
                NativeWidgetId.NetworkStatus => data.NetworkAvailable is { } available ? _view.Text(available ? "WidgetNetworkConnected" : "WidgetNetworkDisconnected") : "—",
                NativeWidgetId.WeekProgress => $"{System.Globalization.ISOWeek.GetWeekOfYear(data.LocalTime.DateTime)} · {(int)((((int)data.LocalTime.DayOfWeek + 6) % 7 + data.LocalTime.TimeOfDay.TotalDays) / 7 * 100)}%",
                NativeWidgetId.Clock => data.LocalTime.ToString("t", _view.Culture),
                NativeWidgetId.Calendar => _placements[id].ColumnSpan == 1 ? data.LocalTime.ToString("dd", _view.Culture) : data.LocalTime.ToString("dddd\nd MMMM", _view.Culture),
                NativeWidgetId.ResourceUsage => $"CPU {Percent(data.CpuPercent)}\nRAM {Percent(data.MemoryPercent)}",
                NativeWidgetId.Battery => data.BatteryPercent is { } battery ? $"{battery}%" : data.OnAcPower == true ? "AC" : "—",
                NativeWidgetId.Uptime => $"{(int)data.Uptime.TotalHours}h {data.Uptime.Minutes}m",
                _ => string.Empty,
            };
        foreach (var (id, button) in _timerValueButtons)
        {
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button,
                _view.Text("Widget" + id + ".Text") + ": " + _view.Text("WidgetTimerDuration") + ", " + _values[id].Text);
            ToolTipService.SetToolTip(button, _view.Text("WidgetTimerDuration") + ": " + _values[id].Text + "\n" + _view.Text("WidgetTimerDurationHelp"));
        }
    }
    private void CloseTimerMenus()
    {
        foreach (var menu in _timerFlyouts) menu.Hide();
    }
    private void RetireNativeActions()
    {
        var requests = _nativeActionRequests;
        _nativeActionRequests = null;
        try { requests?.Cancel(); }
        finally { requests?.Dispose(); }
    }
    private string TimerText(WidgetCountdown timer)
    {
        var seconds = timer.RemainingSeconds;
        return (seconds / 60).ToString("00", _view?.Culture) + ":" + (seconds % 60).ToString("00", _view?.Culture);
    }
    private static string Percent(double? value) => value is { } number ? $"{number:0}%" : "—";
}
