using System.ComponentModel;
using DropSpace.App.Services.Media;
using DropSpace.App.ViewModels;
using DropSpace.App.Views.Island;
using DropSpace.App.Views.Settings;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DropSpace.App.Views.Music;

public sealed class MusicPage : UserControl
{
    private readonly NativeSettingsEditor _editor;
    private readonly MediaViewModel _media;
    private readonly WindowsMediaSessionService _sessions;
    private readonly MediaApplicationIconService _icons;
    private readonly IAppStringLocalizer _strings;
    private readonly StackPanel _applications = new() { Spacing = 8 };
    private readonly TextBlock _folder = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _source = new() { Opacity = 0.7 };
    private readonly MediaExpandedView _nowPlaying;
    private CancellationTokenSource? _iconStop;
    private Task _iconJob = Task.CompletedTask;
    private string _sourcesKey = string.Empty;
    public MusicPage(NativeSettingsEditor editor, MediaViewModel media, WindowsMediaSessionService sessions,
        MediaExperienceService experience, MediaApplicationIconService icons, IAppStringLocalizer strings, nint windowHandle)
    {
        _editor = editor; _media = media; _sessions = sessions; _icons = icons; _strings = strings;
        var body = new StackPanel { Spacing = 16, MaxWidth = 780, HorizontalAlignment = HorizontalAlignment.Left };
        _nowPlaying = new MediaExpandedView { ViewModel = media, Height = 280 };
        body.Children.Add(_nowPlaying); body.Children.Add(_source);
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
    { _editor.PropertyChanged -= OnSettings; _media.PropertyChanged -= OnMedia; _iconStop?.Cancel(); _sourcesKey = string.Empty; }
    private void OnSettings(object? sender, PropertyChangedEventArgs args) { if (args.PropertyName == nameof(NativeSettingsEditor.Settings)) Refresh(); }
    private void OnMedia(object? sender, PropertyChangedEventArgs args) { if (args.PropertyName == nameof(MediaViewModel.Session)) Refresh(); }
    private void Refresh()
    {
        _folder.Text = _editor.Settings.Lyrics.LocalLrcDirectory;
        _nowPlaying.Height = string.IsNullOrEmpty(_media.Title) ? 100 : 280;
        _source.Text = _media.Session.SourceDisplayName;
        var settings = _editor.Settings.IslandActivity;
        var sources = _sessions.AvailableSources.Concat(settings.AllowedMediaSourceAppIds).Distinct(StringComparer.OrdinalIgnoreCase).Take(128).ToArray();
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
    private async Task LoadIconsAsync(Task previous, CancellationTokenSource? oldStop, List<(string Source, Image Image)> images, CancellationToken token)
    {
        await previous; oldStop?.Dispose();
        foreach (var (source, image) in images)
        {
            if (token.IsCancellationRequested) break;
            try { var icon = await _icons.LoadAsync(source, token); if (!token.IsCancellationRequested) image.Source = icon; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
        }
    }
}
