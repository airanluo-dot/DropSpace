using System.ComponentModel;
using DropSpace.Core.Lyrics;
using DropSpace.App.Services.Media;
using DropSpace.App.ViewModels;
using DropSpace.App.Views.Island;
using DropSpace.App.Views.Settings;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
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
    private readonly StackPanel _applications = new() { Spacing = 8 };
    private readonly TextBlock _folder = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _source = new() { Opacity = 0.7 };
    private readonly StackPanel _lyricsRows = new() { Spacing = 8 };
    private readonly ScrollViewer _lyricsScroll;
    private readonly TextBlock _lyricsStatus = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.72 };
    private readonly MediaExpandedView _nowPlaying;
    private CancellationTokenSource? _iconStop;
    private Task _iconJob = Task.CompletedTask;
    private string _sourcesKey = string.Empty;
    private IReadOnlyList<DropSpace.Core.Lyrics.LyricsLine> _renderedLyrics = [];
    private string _renderedLyricsOptions = string.Empty;
    private int _lastCenteredLyric = -1;
    public MusicPage(NativeSettingsEditor editor, MediaViewModel media, WindowsMediaSessionService sessions,
        MediaExperienceService experience, MediaApplicationIconService icons, IAppStringLocalizer strings, nint windowHandle,
        NeteaseEnhancementViewModel enhancement)
    {
        _editor = editor; _media = media; _sessions = sessions; _icons = icons; _strings = strings;
        var body = new StackPanel { Spacing = 16, MaxWidth = 780, HorizontalAlignment = HorizontalAlignment.Left };
        _nowPlaying = new MediaExpandedView { ViewModel = media, MinHeight = 280, Height = 380 };
        body.Children.Add(_nowPlaying);
        body.Children.Add(new NeteaseEnhancementCard(enhancement, strings));
        _lyricsScroll = new ScrollViewer
        {
            Content = _lyricsRows,
            MaxHeight = 360,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new(4, 2, 4, 8),
        };
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
        body.Children.Add(lyricsPanel);
        body.Children.Add(_source);
        var form = new SettingsForm(editor, strings); body.Children.Add(form);
        form.AddHeading("MusicPlaybackSection");
        form.AddToggle("MusicEnabled", s => s.IslandActivity.EnableMediaActivity, (s,v) => s with { IslandActivity = s.IslandActivity with { EnableMediaActivity = v } });
        form.AddToggle("MusicArtwork", s => s.IslandActivity.ShowArtwork, (s,v) => s with { IslandActivity = s.IslandActivity with { ShowArtwork = v } });
        form.AddToggle("MusicSpectrum", s => s.IslandActivity.ShowSpectrum, (s,v) => s with { IslandActivity = s.IslandActivity with { ShowSpectrum = v } });
        form.AddToggle("MusicDynamicWidth", s => s.IslandActivity.CompactDynamicWidth, (s,v) => s with { IslandActivity = s.IslandActivity with { CompactDynamicWidth = v } });
        form.AddToggle("MusicProgress", s => s.IslandActivity.ShowProgress, (s,v) => s with { IslandActivity = s.IslandActivity with { ShowProgress = v } });
        form.AddHeading("MusicLyricsSection");
        form.AddToggle("LyricsEnabled", s => s.Lyrics.Enabled, (s,v) => s with { Lyrics = s.Lyrics with { Enabled = v } });
        form.AddToggle("LyricsCompact", s => s.IslandActivity.ShowLyricsInCompact, (s,v) => s with { IslandActivity = s.IslandActivity with { ShowLyricsInCompact = v } });
        form.AddChoice("LyricsMode", new[] { (LyricsMode.Online, strings.Get("LyricsOnline")), (LyricsMode.LocalLrc, strings.Get("LyricsLocal")) }, s => s.Lyrics.Mode, (s,v) => s with { Lyrics = s.Lyrics with { Mode = v } });
        form.AddChoice("LyricsProvider", Enum.GetValues<LyricsProviderKind>().Select(value => (value, strings.Get("LyricsProvider" + value))), s => s.Lyrics.Provider, (s,v) => s with { Lyrics = s.Lyrics with { Provider = v } });
        form.Rows.Children.Add(new TextBlock { Text = strings.Get("LyricsFallbackHelp"), TextWrapping = TextWrapping.Wrap, Opacity = 0.7 });
        form.AddToggle("LyricsSecondary", s => s.Lyrics.SecondaryLyrics, (s,v) => s with { Lyrics = s.Lyrics with { SecondaryLyrics = v } });
        form.AddToggle("LyricsWords", s => s.Lyrics.WordSyncedHighlighting, (s,v) => s with { Lyrics = s.Lyrics with { WordSyncedHighlighting = v } });
        form.AddNumber("LyricsDelay", -30000, 30000, 100, s => s.Lyrics.DelayMilliseconds, (s,v) => s with { Lyrics = s.Lyrics with { DelayMilliseconds = (int)v } });
        form.AddToggle("LyricsScrolling", s => s.Lyrics.Scrolling, (s,v) => s with { Lyrics = s.Lyrics with { Scrolling = v } });
        form.AddNumber("LyricsWidth", NativeIslandSettingsPolicy.MinimumWidth, NativeIslandSettingsPolicy.MaximumWidth, 10, s => s.Lyrics.ScrollingMaxWidth, (s,v) => s with { Lyrics = s.Lyrics with { ScrollingMaxWidth = (int)v } });
        var folderButton = new Button { Content = strings.Get("ChooseFolder") };
        folderButton.Click += async (_, _) => await editor.PickLyricsFolderAsync(windowHandle);
        form.AddRow("LyricsFolder", folderButton); form.Rows.Children.Add(_folder);
        var clear = new Button { Content = strings.Get("ClearLyricsCache") }; clear.Click += (_, _) => experience.ClearLyricsCache(); form.Rows.Children.Add(clear);
        form.AddHeading("MusicApplications");
        form.AddToggle("MusicAllApplications", s => !s.IslandActivity.UseMediaSourceAllowList && s.IslandActivity.AllowedMediaSourceAppIds.Length == 0,
            (s,v) => s with { IslandActivity = s.IslandActivity with { UseMediaSourceAllowList = !v, AllowedMediaSourceAppIds = v ? [] : sessions.AvailableSources.ToArray() } });
        form.Rows.Children.Add(_applications);
        var scroll = new ScrollViewer { Content = body, Padding = new(24), HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        scroll.SizeChanged += (_, _) => body.Width = Math.Clamp(scroll.ActualWidth - 48, 0, 780);
        Content = scroll;
        Loaded += OnLoaded; Unloaded += OnUnloaded;
    }
    private void OnLoaded(object sender, RoutedEventArgs args)
    { _editor.PropertyChanged += OnSettings; _media.PropertyChanged += OnMedia; Refresh(); }
    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _editor.PropertyChanged -= OnSettings;
        _media.PropertyChanged -= OnMedia;
        var stop = Interlocked.Exchange(ref _iconStop, null);
        stop?.Cancel();
        var job = _iconJob;
        _iconJob = Task.CompletedTask;
        _ = ObserveIconShutdownAsync(job, stop);
        _sourcesKey = string.Empty;
    }
    private void OnSettings(object? sender, PropertyChangedEventArgs args) { if (args.PropertyName == nameof(NativeSettingsEditor.Settings)) Refresh(); }
    private void OnMedia(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MediaViewModel.Session) or nameof(MediaViewModel.LyricsLines) or
            nameof(MediaViewModel.CurrentLyricIndex) or nameof(MediaViewModel.LyricsStatus) or nameof(MediaViewModel.Settings))
        {
            Refresh();
        }
    }
    private void Refresh()
    {
        _folder.Text = _editor.Settings.Lyrics.LocalLrcDirectory;
        _nowPlaying.Height = string.IsNullOrEmpty(_media.Title) ? 100 : 380;
        _source.Text = _media.Session.SourceDisplayName;
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

    private void RefreshLyrics()
    {
        var hasTrack = !string.IsNullOrWhiteSpace(_media.Title);
        var lines = _media.LyricsLines;
        var lyricsEnabled = _media.Settings.Lyrics.Enabled;
        _lyricsStatus.Text = hasTrack && lyricsEnabled ? _media.LyricsStatusText : string.Empty;
        _lyricsStatus.Visibility = string.IsNullOrWhiteSpace(_lyricsStatus.Text) ? Visibility.Collapsed : Visibility.Visible;
        _lyricsScroll.Visibility = hasTrack && lyricsEnabled && lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var lyricsOptions = string.Concat(
            _strings.Culture.Name, "|",
            _media.Settings.Lyrics.Enabled, "|",
            _media.Settings.Lyrics.SecondaryLyrics);
        if (!ReferenceEquals(_renderedLyrics, lines) || !string.Equals(_renderedLyricsOptions, lyricsOptions, StringComparison.Ordinal))
        {
            _lyricsRows.Children.Clear();
            foreach (var line in lines.Take(MaximumDisplayedLyricsLines))
            {
                var row = new StackPanel { Spacing = 2 };
                row.Children.Add(new TextBlock
                {
                    Text = line.Text,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 16,
                });
                var secondary = LyricsDisplayPolicy.Secondary(
                    line,
                    _strings.Culture.Name,
                    _media.Settings.Lyrics.Enabled && _media.Settings.Lyrics.SecondaryLyrics);
                if (secondary is { Length: > 0 })
                {
                    row.Children.Add(new TextBlock
                    {
                        Text = secondary,
                        TextWrapping = TextWrapping.Wrap,
                        FontSize = 12,
                        Opacity = 0.72,
                    });
                }
                _lyricsRows.Children.Add(row);
            }

            _renderedLyrics = lines;
            _renderedLyricsOptions = lyricsOptions;
            _lastCenteredLyric = -1;
        }

        var current = _media.CurrentLyricIndex;
        for (var index = 0; index < _lyricsRows.Children.Count; index++)
        {
            if (_lyricsRows.Children[index] is not StackPanel row || row.Children.FirstOrDefault() is not TextBlock text)
            {
                continue;
            }

            var active = index == current;
            text.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            row.Opacity = active ? 1 : 0.58;
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
