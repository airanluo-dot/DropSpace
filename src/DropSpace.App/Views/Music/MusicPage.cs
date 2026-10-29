using System.ComponentModel;
using DropSpace.Core.Lyrics;
using DropSpace.App.Services.Media;
using DropSpace.App.ViewModels;
using DropSpace.App.Views.Island;
using DropSpace.App.Views.Settings;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using DropSpace.Core.Policies;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Text;
using System.Diagnostics;
using Windows.Foundation;

namespace DropSpace.App.Views.Music;

public sealed class MusicPage : UserControl
{
    private const int MaximumDisplayedLyricsLines = 2_000;
    private readonly NativeSettingsEditor _editor;
    private readonly MediaViewModel _media;
    private readonly WindowsMediaSessionService _sessions;
    private readonly MediaApplicationIconService _icons;
    private readonly IAppStringLocalizer _strings;
    private readonly MediaExperienceService _experience;
    private readonly Button _refreshMusic;
    private readonly TextBlock _restartStatus = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private CancellationTokenSource? _pageStop;
    private long _pageGeneration;
    private Task _restartWait = Task.CompletedTask;
    private readonly StackPanel _body = new() { Spacing = 16, MaxWidth = 780, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel _applications = new() { Spacing = 8 };
    private readonly TextBlock _folder = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _lyricsRows = new() { Spacing = 8 };
    private readonly ScrollViewer _lyricsScroll;
    private readonly TextBlock _lyricsStatus = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.72 };
    private readonly MediaExpandedView _nowPlaying;
    private readonly TextBlock _emptyPlayback = new()
    {
        TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(28, 24, 28, 24),
    };
    private CancellationTokenSource? _iconStop;
    private Task _iconJob = Task.CompletedTask;
    private string _sourcesKey = string.Empty;
    private readonly LyricsRowCollection<StackPanel> _lyricRowCache;
    private readonly MediaRenderQueue _refreshQueue = new();
    private bool _presentationActive;
    private string _lyricsTrackIdentity = string.Empty;
    private int _lastHighlightedLyric = -1;
    private int _lastCenteredLyric = -1;
    public MusicPage(NativeSettingsEditor editor, MediaViewModel media, WindowsMediaSessionService sessions,
        MediaExperienceService experience, MediaApplicationIconService icons, IAppStringLocalizer strings, nint windowHandle,
        Services.Dlc.DlcManagerService dlc, Action openDlc, Action? openMusicWindow = null)
    {
        _editor = editor; _media = media; _sessions = sessions; _icons = icons; _strings = strings;
        _experience = experience;
        _lyricRowCache = new(MaximumDisplayedLyricsLines, CreateLyricRow, UpdateLyricRow);
        _nowPlaying = new MediaExpandedView { ViewModel = media, MinHeight = 280, Height = 380 };
        _nowPlaying.SetActive(false);
        // This action belongs only to the main Music page, never the shared island player.
        _refreshMusic = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children =
                {
                    new FontIcon { Glyph = "\uE72C", FontFamily = (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"],
                        FontSize = 16, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false },
                    new TextBlock { Text = strings.Get("MusicRefresh"), VerticalAlignment = VerticalAlignment.Center },
                },
            },
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };
        AutomationProperties.SetName(_refreshMusic, strings.Get("MusicRefresh"));
        AutomationProperties.SetAutomationId(_refreshMusic, "MusicRefresh");
        ToolTipService.SetToolTip(_refreshMusic, strings.Get("MusicRefreshHelp"));
        AutomationProperties.SetLiveSetting(_restartStatus, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        _refreshMusic.Click += OnRefreshMusic;
        var musicActions = new Grid { ColumnSpacing = 12, Margin = new Thickness(12, 12, 12, 0) };
        musicActions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        musicActions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        musicActions.Children.Add(_restartStatus);
        var openWindow = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children =
                {
                    new FontIcon { Glyph = "\uE8A7", FontSize = 16, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = strings.Get("MusicWindowOpen"), VerticalAlignment = VerticalAlignment.Center },
                },
            },
            IsEnabled = openMusicWindow is not null,
        };
        AutomationProperties.SetName(openWindow, strings.Get("MusicWindowOpen"));
        AutomationProperties.SetAutomationId(openWindow, "MusicWindowOpen");
        openWindow.Click += (_, _) => openMusicWindow?.Invoke();
        var actionButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        actionButtons.Children.Add(openWindow); actionButtons.Children.Add(_refreshMusic);
        Grid.SetColumn(actionButtons, 1); musicActions.Children.Add(actionButtons);
        var playback = new StackPanel();
        playback.Children.Add(musicActions);
        // The main page owns its centered empty state; the shared island player stays unchanged.
        var playbackSurface = new Grid { MinHeight = 280 };
        playbackSurface.Children.Add(_nowPlaying); playbackSurface.Children.Add(_emptyPlayback);
        playback.Children.Add(playbackSurface);
        _body.Children.Add(CreateCard(playback, new Thickness(0)));
        _lyricsScroll = new ScrollViewer
        {
            Content = _lyricsRows,
            MaxHeight = 360,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new(4, 2, 4, 8),
        };
        // The lyrics viewport is an intentional wheel-input safe zone. Let the
        // ScrollViewer consume the wheel first, then stop any remaining routed
        // event from moving the outer page at the same time or at a boundary.
        _lyricsScroll.AddHandler(
            UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(OnLyricsPointerWheelChanged),
            handledEventsToo: true);
        AutomationProperties.SetName(_lyricsScroll, strings.Get("MusicLyricsSection"));
        var lyricsPanel = new StackPanel { Spacing = 8 };
        lyricsPanel.Children.Add(new TextBlock
        {
            Text = strings.Get("MusicLyricsSection"),
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
        });
        lyricsPanel.Children.Add(_lyricsStatus);
        lyricsPanel.Children.Add(_lyricsScroll);
        _body.Children.Add(CreateCard(lyricsPanel, new Thickness(16)));
        _body.Children.Add(new AiLyricsSettingsCard(editor, experience.AiLyrics, strings, experience.ClearLyricsCacheAsync, dlc, openDlc));
        var form = new SettingsForm(editor, strings); _body.Children.Add(form);
        form.AddHeading("MusicPlaybackSection");
        form.AddToggle("MusicEnabled", s => s.IslandActivity.EnableMediaActivity, (s,v) => s with { IslandActivity = s.IslandActivity with { EnableMediaActivity = v } });
        form.AddToggle("MusicArtwork", s => s.IslandActivity.ShowArtwork, (s,v) => s with { IslandActivity = s.IslandActivity with { ShowArtwork = v } });
        form.AddToggle("MusicSpectrum", s => s.IslandActivity.ShowSpectrum, (s,v) => s with { IslandActivity = s.IslandActivity with { ShowSpectrum = v } });
        form.AddToggle("MusicDynamicWidth", s => s.IslandActivity.CompactDynamicWidth, (s,v) => s with { IslandActivity = s.IslandActivity with { CompactDynamicWidth = v } });
        form.AddToggle("MusicProgress", s => s.IslandActivity.ShowProgress, (s,v) => s with { IslandActivity = s.IslandActivity with { ShowProgress = v } });
        form.AddHeading("MusicLyricsSection");
        form.AddToggle("LyricsEnabled", s => s.Lyrics.Enabled, (s,v) => s with { Lyrics = s.Lyrics with { Enabled = v } });
        var languageTarget = form.AddChoice("LyricsTranslationTargetTitle", new[]
        {
            (LyricsTranslationTarget.SimplifiedChinese, AppLanguageCatalog.All.Single(language => language.Preference == AppLanguagePreference.SimplifiedChinese).NativeName),
            (LyricsTranslationTarget.English, AppLanguageCatalog.All.Single(language => language.Preference == AppLanguagePreference.English).NativeName),
        }, s => s.LyricsTranslationTarget,
            (s,v) => s with { LyricsTranslationTarget = v, LyricsTranslationTargetExplicit = true });
        AutomationProperties.SetAutomationId(languageTarget, "LyricsTranslationTarget");
        form.AddToggle("LyricsCompact", s => s.IslandActivity.ShowLyricsInCompact, (s,v) => s with { IslandActivity = s.IslandActivity with { ShowLyricsInCompact = v } });
        form.AddChoice("LyricsMode", new[] { (LyricsMode.Online, strings.Get("LyricsOnline")), (LyricsMode.LocalLrc, strings.Get("LyricsLocal")) }, s => s.Lyrics.Mode, (s,v) => s with { Lyrics = s.Lyrics with { Mode = v } });
        var onlineProviders = Enum.GetValues<LyricsProviderKind>().Where(value => value != LyricsProviderKind.LocalLrc).ToArray();
        form.AddChoice("LyricsProvider", onlineProviders.Select(value => (value, strings.Get("LyricsProvider" + value))),
            s => s.Lyrics.Provider,
            (s,v) => s with { Lyrics = s.Lyrics with { Provider = v, BackupProvider = s.Lyrics.BackupProvider == v ? null : s.Lyrics.BackupProvider } });
        var backupChoices = new[] { (new ProviderChoice(null), strings.Get("LyricsBackupProviderNone")) }
            .Concat(onlineProviders.Select(value => (new ProviderChoice(value), strings.Get("LyricsProvider" + value))));
        form.AddChoice("LyricsBackupProvider", backupChoices,
            s => new ProviderChoice(s.Lyrics.BackupProvider),
            (s,v) => s with { Lyrics = s.Lyrics with { BackupProvider = v.Value == s.Lyrics.Provider ? null : v.Value } });
        form.AddToggle("LyricsSearchRemainingProviders", s => s.Lyrics.SearchRemainingProviders,
            (s,v) => s with { Lyrics = s.Lyrics with { SearchRemainingProviders = v } });
        form.Rows.Children.Add(new QqMusicLoginCard(experience, strings));
        form.AddToggle("LyricsShowAiLabel", s => s.Lyrics.ShowAiLyricsLabel, (s,v) => s with { Lyrics = s.Lyrics with { ShowAiLyricsLabel = v } });
        form.AddToggle("LyricsSecondary", s => s.Lyrics.SecondaryLyrics, (s,v) => s with { Lyrics = s.Lyrics with { SecondaryLyrics = v } });
        form.Rows.Children.Add(new LyricsFontSizeControl(editor, strings));
        form.AddToggle("LyricsWords", s => s.Lyrics.WordSyncedHighlighting, (s,v) => s with { Lyrics = s.Lyrics with { WordSyncedHighlighting = v } });
        form.AddSlider("LyricsDelay", -30000, 30000, 100, s => s.Lyrics.DelayMilliseconds, (s,v) => s with { Lyrics = s.Lyrics with { DelayMilliseconds = (int)v } }, v => v.ToString("+0;-0;0", strings.Culture) + " ms");
        form.AddToggle("LyricsScrolling", s => s.Lyrics.Scrolling, (s,v) => s with { Lyrics = s.Lyrics with { Scrolling = v } });
        form.AddSlider("LyricsWidth", 80, 610, 10,
            s => s.Lyrics.UnlimitedScrollingWidth ? 610 : s.Lyrics.ScrollingMaxWidth,
            (s,v) => s with { Lyrics = s.Lyrics with { UnlimitedScrollingWidth = v == 610,
                ScrollingMaxWidth = v == 610 ? s.Lyrics.ScrollingMaxWidth : (int)v } },
            v => v == 610 ? strings.Get("LyricsWidthUnlimited") : v.ToString("0", strings.Culture));
        form.AddSlider("LyricsCacheSize", 0, 10, 1,
            s => s.Lyrics.CacheMaximumBytes / 1_000_000_000d,
            (s,v) => s with { Lyrics = s.Lyrics with { CacheMaximumBytes = (long)v * 1_000_000_000 } },
            v => v == 0 ? strings.Get("LyricsCacheDisabled") : v.ToString("0.#########", strings.Culture) + " GB");
        var folderButton = new Button { Content = strings.Get("ChooseFolder") };
        folderButton.Click += async (_, _) => await editor.PickLyricsFolderAsync(windowHandle);
        form.AddRow("LyricsFolder", folderButton); form.Rows.Children.Add(_folder);
        var clear = new Button { Content = strings.Get("ClearLyricsCache") };
        var cacheStatus = new TextBlock { TextWrapping = TextWrapping.Wrap };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(cacheStatus, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        clear.Click += async (_, _) =>
        {
            clear.IsEnabled = false;
            cacheStatus.Text = strings.Get("AiLyricsClearingCache");
            try { await experience.ClearLyricsCacheAsync(); cacheStatus.Text = strings.Get("AiLyricsCacheCleared"); }
            catch (OperationCanceledException) { cacheStatus.Text = strings.Get("LyricsCacheClearFailed"); }
            catch (Exception error) when (error is not OutOfMemoryException) { cacheStatus.Text = strings.Get("LyricsCacheClearFailed"); }
            finally { clear.IsEnabled = true; }
        };
        form.Rows.Children.Add(clear);
        form.Rows.Children.Add(cacheStatus);
        form.AddHeading("MusicApplications");
        form.AddToggle("MusicAllApplications", s => !s.IslandActivity.UseMediaSourceAllowList && s.IslandActivity.AllowedMediaSourceAppIds.Length == 0,
            (s,v) => s with { IslandActivity = s.IslandActivity with { UseMediaSourceAllowList = !v, AllowedMediaSourceAppIds = v ? [] : sessions.AvailableSources.ToArray() } });
        form.Rows.Children.Add(_applications);
        var enhancementEntry = new Button
        {
            Content = new TextBlock { Text = strings.Get("DlcManage"), TextWrapping = TextWrapping.Wrap },
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetAutomationId(enhancementEntry, "NeteaseEnhancementOpenDlc");
        AutomationProperties.SetName(enhancementEntry, strings.Get("NeteaseEnhancementTitle") + " · " + strings.Get("DlcManage"));
        enhancementEntry.Click += (_, _) => openDlc();
        form.AddRow("NeteaseEnhancementTitle", enhancementEntry);
        var scroll = new ScrollViewer { Content = _body, Padding = new(24), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        scroll.SizeChanged += (_, _) => _body.Width = Math.Clamp(scroll.ActualWidth - 48, 0, 780);
        IsTabStop = true;
        SettingsEditBehavior.Attach(scroll, this);
        Content = scroll;
        Loaded += OnLoaded; Unloaded += OnUnloaded;
        RegisterPropertyChangedCallback(VisibilityProperty, OnVisibilityChanged);
    }
    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_pageStop is not null) return;
        _pageStop = new(); ++_pageGeneration;
        _nowPlaying.SetActive(_presentationActive && Visibility == Visibility.Visible);
        _refreshMusic.IsEnabled = true;
        _restartStatus.Text = string.Empty;
        _editor.PropertyChanged += OnSettings;
        _media.PropertyChanged += OnMedia;
        RequestRefresh();
    }
    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        CancelRefresh();
        _nowPlaying.SetActive(false);
        var pageStop = _pageStop;
        _pageStop = null; ++_pageGeneration;
        pageStop?.Cancel();
        _ = ObserveIconShutdownAsync(_restartWait, pageStop);
        _editor.PropertyChanged -= OnSettings;
        _media.PropertyChanged -= OnMedia;
        var stop = Interlocked.Exchange(ref _iconStop, null);
        stop?.Cancel();
        var job = _iconJob;
        _iconJob = Task.CompletedTask;
        _ = ObserveIconShutdownAsync(job, stop);
        _sourcesKey = string.Empty;
    }
    private async void OnRefreshMusic(object sender, RoutedEventArgs args)
    {
        if (_pageStop is null || !_refreshMusic.IsEnabled) return;
        _restartWait = RefreshMusicAsync(_pageGeneration, _pageStop.Token);
        await _restartWait;
    }

    private async Task RefreshMusicAsync(long generation, CancellationToken token)
    {
        _refreshMusic.IsEnabled = false;
        _restartStatus.Text = _strings.Get("MusicReconnecting");
        try
        {
            await _experience.RestartAsync(token);
            if (IsCurrentPage(generation)) _restartStatus.Text = _strings.Get("MusicReconnected");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Debug.WriteLine(exception.GetType().Name);
            if (IsCurrentPage(generation)) _restartStatus.Text = _strings.Get("MusicReconnectFailed");
        }
        finally
        {
            if (IsCurrentPage(generation)) _refreshMusic.IsEnabled = true;
        }
    }

    private bool IsCurrentPage(long generation) =>
        generation == _pageGeneration && _pageStop is { IsCancellationRequested: false };

    private void OnSettings(object? sender, PropertyChangedEventArgs args) { if (args.PropertyName == nameof(NativeSettingsEditor.Settings)) RequestRefresh(); }
    private void OnMedia(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MediaViewModel.Session) or nameof(MediaViewModel.LyricsLines) or
            nameof(MediaViewModel.CurrentLyricIndex) or nameof(MediaViewModel.LyricsStatus) or nameof(MediaViewModel.Settings))
        {
            RequestRefresh();
        }
    }
    internal void SetActive(bool active)
    {
        _presentationActive = active;
        _nowPlaying.SetActive(active && Visibility == Visibility.Visible);
        if (CanRefresh) RequestRefresh(); else CancelRefresh();
    }
    private bool CanRefresh => _presentationActive && IsLoaded && _pageStop is not null && Visibility == Visibility.Visible;
    private void RequestRefresh()
    {
        if (_refreshQueue.Request(CanRefresh)) CompositionTarget.Rendering += OnRendering;
    }
    private void OnRendering(object? sender, object args)
    {
        CompositionTarget.Rendering -= OnRendering;
        if (_refreshQueue.BeginRender(CanRefresh)) Refresh();
    }
    private void CancelRefresh()
    {
        CompositionTarget.Rendering -= OnRendering;
        _refreshQueue.Cancel();
    }
    private void OnVisibilityChanged(DependencyObject sender, DependencyProperty property)
    {
        _nowPlaying.SetActive(_presentationActive && Visibility == Visibility.Visible);
        if (CanRefresh) RequestRefresh(); else CancelRefresh();
    }
    private void Refresh()
    {
        _folder.Text = _editor.Settings.Lyrics.LocalLrcDirectory;
        var hasTrack = !string.IsNullOrWhiteSpace(_media.Title);
        _nowPlaying.Visibility = hasTrack ? Visibility.Visible : Visibility.Collapsed;
        _emptyPlayback.Visibility = hasTrack ? Visibility.Collapsed : Visibility.Visible;
        _emptyPlayback.Text = _strings.Get("MediaNoSession.Text");
        _nowPlaying.Height = 380;
        RefreshLyrics();
        var settings = _editor.Settings.IslandActivity;
        var sources = _sessions.AvailableSources.Concat(settings.AllowedMediaSourceAppIds).Distinct(StringComparer.OrdinalIgnoreCase).Take(512).ToArray();
        var key = string.Join('\n', sources) + "|" + settings.UseMediaSourceAllowList + "|" + string.Join('\n', settings.AllowedMediaSourceAppIds);
        if (_sourcesKey == key && _applications.Children.Count > 0) return;
        _sourcesKey = key; _iconStop?.Cancel(); _applications.Children.Clear();
        var images = new List<(string Source, Image Image)>();
        foreach (var source in sources)
        {
            var restricted = settings.UseMediaSourceAllowList || settings.AllowedMediaSourceAppIds.Length > 0;
            var check = new CheckBox { IsChecked = !restricted || settings.AllowedMediaSourceAppIds.Contains(source, StringComparer.OrdinalIgnoreCase) };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            var icon = new Image { Width = 28, Height = 28 }; images.Add((source, icon)); row.Children.Add(icon);
            var labels = new StackPanel(); labels.Children.Add(new TextBlock { Text = WindowsMediaSessionService.FriendlyName(source, _strings) });
            labels.Children.Add(new TextBlock { Text = source, FontSize = 11, Opacity = 0.6, TextWrapping = TextWrapping.Wrap }); row.Children.Add(labels); check.Content = row;
            AutomationProperties.SetName(check, WindowsMediaSessionService.FriendlyName(source, _strings));
            check.Click += async (_, _) =>
            {
                var selected = check.IsChecked == true;
                await _editor.UpdateAsync(s =>
                {
                    var values = s.IslandActivity.UseMediaSourceAllowList || s.IslandActivity.AllowedMediaSourceAppIds.Length > 0
                        ? s.IslandActivity.AllowedMediaSourceAppIds : sources;
                    var list = values.Where(value => !value.Equals(source, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (selected) list.Add(source);
                    return s with { IslandActivity = s.IslandActivity with { UseMediaSourceAllowList = true, AllowedMediaSourceAppIds = list.ToArray() } };
                });
            };
            _applications.Children.Add(check);
        }
        if (sources.Length == 0) _applications.Children.Add(new TextBlock { Text = _strings.Get("MusicApplicationsEmpty"), TextWrapping = TextWrapping.Wrap });
        var previous = _iconJob; var oldStop = _iconStop; _iconStop = new();
        _iconJob = LoadIconsAsync(previous, oldStop, images, _iconStop.Token);
    }

    private static void OnLyricsPointerWheelChanged(object sender, PointerRoutedEventArgs args) =>
        args.Handled = true;

    private sealed record ProviderChoice(LyricsProviderKind? Value);

    private static Border CreateCard(UIElement content, Thickness padding) => new()
    {
        Padding = padding,
        CornerRadius = new CornerRadius(8),
        Style = (Style)Application.Current.Resources["DropSpaceCardStyle"],
        BorderThickness = new Thickness(1),
        Child = content,
    };

    private void RefreshLyrics()
    {
        var hasTrack = !string.IsNullOrWhiteSpace(_media.Title);
        var lines = _media.LyricsLines;
        var lyricsEnabled = _media.Settings.Lyrics.Enabled;
        _lyricsStatus.Text = hasTrack && lyricsEnabled ? _media.LyricsStatusText : string.Empty;
        _lyricsStatus.Visibility = string.IsNullOrWhiteSpace(_lyricsStatus.Text) ? Visibility.Collapsed : Visibility.Visible;
        _lyricsScroll.Visibility = hasTrack && lyricsEnabled && lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var lyricsOptions = string.Concat(
            LyricsTranslationTargetPolicy.ToLanguageTag(_media.Settings.LyricsTranslationTarget), "|",
            _media.Settings.Lyrics.Enabled, "|",
            _media.Settings.Lyrics.SecondaryLyrics, "|", _media.Settings.Lyrics.ShowAiLyricsLabel, "|", _media.Settings.Lyrics.OriginalFontSize, "|", _media.Settings.Lyrics.TranslationFontSize);
        var trackChanged = !string.Equals(_lyricsTrackIdentity, _media.Session.TrackIdentity, StringComparison.Ordinal);
        var structureChanged = _lyricRowCache.Update(lines, _media.Session.TrackIdentity, lyricsOptions);
        if (structureChanged)
        {
            while (_lyricsRows.Children.Count > _lyricRowCache.Count)
                _lyricsRows.Children.RemoveAt(_lyricsRows.Children.Count - 1);
            for (var index = 0; index < _lyricRowCache.Count; index++)
            {
                var row = _lyricRowCache[index];
                if (index == _lyricsRows.Children.Count) _lyricsRows.Children.Add(row);
                else if (!ReferenceEquals(_lyricsRows.Children[index], row)) _lyricsRows.Children[index] = row;
            }
        }
        if (trackChanged)
        {
            _lyricsTrackIdentity = _media.Session.TrackIdentity;
            _lastCenteredLyric = -1;
        }

        var current = _media.CurrentLyricIndex;
        if (current != _lastHighlightedLyric || structureChanged)
        {
            SetLyricActive(_lastHighlightedLyric, false);
            SetLyricActive(current, true);
            _lastHighlightedLyric = current;
        }

        if (current < 0 || current >= _lyricsRows.Children.Count || current == _lastCenteredLyric || !_lyricsScroll.IsLoaded)
        {
            return;
        }

        try
        {
            if (_lyricsRows.Children[current] is not FrameworkElement row) return;
            var point = row.TransformToVisual(_lyricsScroll).TransformPoint(new Point(0, 0));
            var target = Math.Max(0, _lyricsScroll.VerticalOffset + point.Y -
                Math.Max(0, (_lyricsScroll.ViewportHeight - row.ActualHeight) / 2));
            _lyricsScroll.ChangeView(null, target, null, _media.IsReducedMotion);
            _lastCenteredLyric = current;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            Debug.WriteLine($"DropSpace lyrics scroll positioning failed: {exception.GetType().Name}");
        }
    }
    private StackPanel CreateLyricRow(LyricsLine line)
    {
        var row = new StackPanel { Spacing = 2, Opacity = 0.58 };
        row.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap });
        UpdateLyricRow(row, line);
        return row;
    }

    private void UpdateLyricRow(StackPanel row, LyricsLine line)
    {
        var original = (TextBlock)row.Children[0];
        if (original.Text != line.Text) original.Text = line.Text;
        if (original.FontSize != _media.Settings.Lyrics.OriginalFontSize)
            original.FontSize = _media.Settings.Lyrics.OriginalFontSize;
        var secondary = LyricsDisplayPolicy.SecondaryPresentation(line, LyricsTranslationTargetPolicy.ToLanguageTag(_media.Settings.LyricsTranslationTarget),
            _media.Settings.Lyrics.Enabled && _media.Settings.Lyrics.SecondaryLyrics, _media.Settings.Lyrics.ShowAiLyricsLabel);
        if (row.Children.Count == 1 && !string.IsNullOrEmpty(secondary))
            row.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.72 });
        if (row.Children.Count > 1)
        {
            var translation = (TextBlock)row.Children[1];
            var text = secondary ?? string.Empty;
            if (translation.Text != text) translation.Text = text;
            if (translation.FontSize != _media.Settings.Lyrics.TranslationFontSize)
                translation.FontSize = _media.Settings.Lyrics.TranslationFontSize;
            translation.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void SetLyricActive(int index, bool active)
    {
        if (index < 0 || index >= _lyricRowCache.Count) return;
        var row = _lyricRowCache[index];
        ((TextBlock)row.Children[0]).FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        row.Opacity = active ? 1 : 0.58;
    }

    private async Task LoadIconsAsync(Task previous, CancellationTokenSource? oldStop, List<(string Source, Image Image)> images, CancellationToken token)
    {
        try
        {
            try
            {
                await previous.ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                // A superseded icon job must not poison the replacement job.
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                Debug.WriteLine($"DropSpace media icon job predecessor failed: {exception.GetType().Name}");
            }

            foreach (var (source, image) in images)
            {
                if (token.IsCancellationRequested) break;
                try
                {
                    var icon = await _icons.LoadAsync(source, token).ConfigureAwait(true);
                    if (!token.IsCancellationRequested) image.Source = icon;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    Debug.WriteLine($"DropSpace media icon load failed for {source}: {exception.GetType().Name}");
                }
            }
        }
        finally
        {
            oldStop?.Dispose();
        }
    }

    private static async Task ObserveIconShutdownAsync(Task job, CancellationTokenSource? stop)
    {
        try
        {
            await job.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Debug.WriteLine($"DropSpace media icon shutdown failed: {exception.GetType().Name}");
        }
        finally
        {
            stop?.Dispose();
        }
    }
}
