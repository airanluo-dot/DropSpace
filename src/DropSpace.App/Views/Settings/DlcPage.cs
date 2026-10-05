using DropSpace.App.Services;
using System.Diagnostics;
using DropSpace.App.Services.Dlc;
using DropSpace.Core.Abstractions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DropSpace.App.Views.Settings;

/// <summary>Projects the application-owned DLC state; navigation never starts/cancels a transfer.</summary>
public sealed class DlcPage : UserControl
{
    private readonly DlcManagerService _manager;
    private readonly IAppStringLocalizer _strings;
    private readonly ViewModels.NativeSettingsEditor _editor;
    private readonly StackPanel _installed = new() { Spacing = 12 };
    private readonly StackPanel _available = new() { Spacing = 12 };
    private readonly TextBlock _emptyInstalled;
    private readonly TextBlock _emptyAvailable;
    private readonly Button _refresh;
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly Dictionary<string, PackageCard> _cards = new(StringComparer.Ordinal);
    private CancellationTokenSource? _pageStop;
    private bool _confirming;
    private int _renderQueued;

    public DlcPage(DlcManagerService manager, IAppStringLocalizer strings, ViewModels.NativeSettingsEditor editor, nint windowHandle)
    {
        _manager = manager;
        _strings = strings;
        _editor = editor;
        var body = new StackPanel { Spacing = 18 };
        body.Children.Add(new TextBlock { Text = strings.Get("DlcTitle"), FontSize = 22, FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = strings.Get("DlcDescription"), TextWrapping = TextWrapping.Wrap, Opacity = 0.72 });
        _refresh = new Button { Content = strings.Get("DlcRefresh"), HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(_refresh, strings.Get("DlcRefresh"));
        AutomationProperties.SetAutomationId(_refresh, "DlcRefresh");
        _refresh.Click += async (_, _) => await _manager.RefreshAsync(force: true);
        body.Children.Add(_refresh);
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Polite);
        body.Children.Add(_error);
        body.Children.Add(Heading("DlcInstalled"));
        _emptyInstalled = new TextBlock { Text = strings.Get("DlcEmptyInstalled"), TextWrapping = TextWrapping.Wrap, Opacity = 0.72 };
        body.Children.Add(_emptyInstalled);
        body.Children.Add(_installed);
        body.Children.Add(Heading("DlcAvailable"));
        _emptyAvailable = new TextBlock { Text = strings.Get("DlcEmptyAvailable"), TextWrapping = TextWrapping.Wrap, Opacity = 0.72 };
        body.Children.Add(_emptyAvailable);
        body.Children.Add(_available);
        body.Children.Add(new DownloadPanel(editor, strings, windowHandle));
        Content = body;
        AutomationProperties.SetName(this, strings.Get("DlcTitle"));
        AutomationProperties.SetAutomationId(this, "DlcPage");
        Loaded += async (_, _) =>
        {
            if (_pageStop is not null) return;
            _pageStop = new();
            _manager.Changed += OnChanged;
            Render();
            await _manager.RefreshAsync();
        };
        Unloaded += (_, _) =>
        {
            _manager.Changed -= OnChanged;
            var stop = _pageStop;
            _pageStop = null;
            stop?.Cancel();
            stop?.Dispose();
            _confirming = false;
        };
    }

    private TextBlock Heading(string key) => new()
    {
        Text = _strings.Get(key), FontSize = 18, FontWeight = FontWeights.SemiBold,
        TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 0),
    };

    private void OnChanged(object? sender, EventArgs args)
    {
        if (Interlocked.Exchange(ref _renderQueued, 1) != 0) return;
        try
        {
            if (!DispatcherQueue.TryEnqueue(() =>
            {
                Interlocked.Exchange(ref _renderQueued, 0);
                if (_pageStop is not null) Render();
            })) Interlocked.Exchange(ref _renderQueued, 0);
        }
        catch (Exception error)
        {
            Interlocked.Exchange(ref _renderQueued, 0);
            Debug.WriteLine($"DLC dispatcher retired: {error.GetType().Name}");
        }
    }

    private void Render()
    {
        var snapshots = _manager.Packages;
        var ids = snapshots.Select(item => item.Package.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var obsolete in _cards.Keys.Where(id => !ids.Contains(id)).ToArray())
        {
            var card = _cards[obsolete];
            _installed.Children.Remove(card);
            _available.Children.Remove(card);
            _cards.Remove(obsolete);
        }
        var busy = _manager.IsBusy || _confirming;
        _refresh.IsEnabled = !busy;
        foreach (var snapshot in snapshots)
        {
            if (!_cards.TryGetValue(snapshot.Package.Id, out var card))
            {
                card = new PackageCard(_strings, snapshot.Package.Id, action => _ = ActAsync(snapshot.Package.Id, action),
                    () => _manager.CancelDownload(snapshot.Package.Id));
                _cards.Add(snapshot.Package.Id, card);
            }
            var isInstalled = snapshot.Installation?.IsInstalled == true ||
                !snapshot.Package.CanDownload && snapshot.Installation?.HasArtifacts == true;
            var destination = isInstalled ? _installed : _available;
            var other = isInstalled ? _available : _installed;
            if (!destination.Children.Contains(card)) { other.Children.Remove(card); destination.Children.Add(card); }
            card.Update(snapshot, busy);
        }
        _emptyInstalled.Visibility = _installed.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _emptyAvailable.Visibility = _available.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task ActAsync(string id, DlcPackageAction action)
    {
        if (_pageStop is not { IsCancellationRequested: false } stop || _confirming || _manager.IsBusy) return;
        var token = stop.Token;
        var snapshot = _manager.Packages.FirstOrDefault(item => item.Package.Id == id);
        if (snapshot is null) return;
        if (action == DlcPackageAction.Inspect) { await _manager.RefreshAsync(force: true); return; }
        _confirming = true;
        _error.Visibility = Visibility.Collapsed;
        Render();
        try
        {
            var download = action == DlcPackageAction.Download;
            var title = _strings.Get(download ? "DlcDownload" : "DlcDelete");
            var content = download ? _strings.Format("DlcDownloadConfirm", snapshot.Package.Name,
                PackageCard.Size(_strings, snapshot.Package.DownloadBytes)) : _strings.Format("DlcDeleteConfirm", snapshot.Package.Name);
            if (download && snapshot.Package.Source is { } source) content += "\n\n" + _strings.Format("DlcSource", source);
            if ((download ? snapshot.Package.DownloadConfirmationResourceKey : snapshot.Package.DeleteConfirmationResourceKey) is { } notice)
                content += "\n\n" + _strings.Get(notice);
            if (!download && id == _editor.Settings.Lyrics.AiModelId && _editor.Settings.Lyrics.AiTranslationEnabled)
                content += "\n\n" + _strings.Get("DlcDeleteEnabledModel");
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = title, Content = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = title, CloseButtonText = _strings.Get("AiLyricsNotNow"), DefaultButton = ContentDialogButton.Close,
            };
            if (await ContentDialogLifetime.ShowAsync(dialog, token) != ContentDialogResult.Primary ||
                token.IsCancellationRequested || !ReferenceEquals(_pageStop, stop)) return;
            _confirming = false;
            if (download) await _manager.DownloadAsync(id, consent: true);
            else await _manager.DeleteAsync(id);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Debug.WriteLine($"DLC dialog failed: {error.GetType().Name}");
            if (ReferenceEquals(_pageStop, stop))
            {
                _error.Text = _strings.Get("DlcOperationFailed");
                _error.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            if (ReferenceEquals(_pageStop, stop)) { _confirming = false; Render(); }
        }
    }

    private sealed class PackageCard : UserControl
    {
        private readonly IAppStringLocalizer _strings;
        private readonly TextBlock _name = new() { FontSize = 16, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _purpose = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.72 };
        private readonly TextBlock _size = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.72 };
        private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
        private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100 };
        private readonly Button _download = new();
        private readonly Button _delete = new();
        private readonly Button _retry = new();
        private readonly Button _cancel = new();
        private DlcPackageAction _retryAction = DlcPackageAction.Inspect;

        public PackageCard(IAppStringLocalizer strings, string id, Action<DlcPackageAction> action, Action cancel)
        {
            _strings = strings;
            var body = new StackPanel { Spacing = 10 };
            body.Children.Add(_name); body.Children.Add(_purpose); body.Children.Add(_size); body.Children.Add(_status); body.Children.Add(_progress);
            var actions = new Grid { ColumnSpacing = 8, HorizontalAlignment = HorizontalAlignment.Left };
            var buttons = new[] { _download, _delete, _retry, _cancel };
            for (var i = 0; i < buttons.Length; i++)
            {
                actions.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                Grid.SetColumn(buttons[i], i); actions.Children.Add(buttons[i]);
            }
            body.Children.Add(actions);
            _download.Click += (_, _) => action(DlcPackageAction.Download);
            _delete.Click += (_, _) => action(DlcPackageAction.Delete);
            _retry.Click += (_, _) => action(_retryAction);
            _cancel.Click += (_, _) => cancel();
            AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
            AutomationProperties.SetAutomationId(this, "DlcPackage-" + id);
            AutomationProperties.SetAutomationId(_download, "DlcDownload-" + id);
            AutomationProperties.SetAutomationId(_delete, "DlcDelete-" + id);
            AutomationProperties.SetAutomationId(_retry, "DlcRetry-" + id);
            AutomationProperties.SetAutomationId(_cancel, "DlcCancel-" + id);
            Content = new Border
            {
                Padding = new(16), CornerRadius = new(8), BorderThickness = new(1),
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                Child = body,
            };
        }

        public void Update(DlcPackageSnapshot item, bool busy)
        {
            _name.Text = item.Package.Name;
            _purpose.Text = _strings.Get(item.Package.PurposeResourceKey);
            _size.Text = _strings.Format("DlcInstalledSize", Size(_strings, item.Installation?.InstalledBytes));
            if (item.Package.CanDownload) _size.Text += "\n" + _strings.Format("DlcDownloadSize", Size(_strings, item.Package.DownloadBytes));
            var downloading = item.State is DlcPackageState.Downloading or DlcPackageState.Canceling;
            var key = item.State switch
            {
                DlcPackageState.Checking => "DlcChecking",
                DlcPackageState.Downloading => item.Progress >= 1 ? "DlcVerifying" : "DlcDownloading",
                DlcPackageState.Canceling => "DlcCanceling",
                DlcPackageState.Removing => "DlcRemoving",
                DlcPackageState.Installed => "DlcReady",
                DlcPackageState.Failed => item.FailedAction switch
                {
                    DlcPackageAction.Download => "DlcDownloadFailed", DlcPackageAction.Delete => "DlcDeleteFailed", _ => "DlcInspectFailed",
                },
                _ => item.WasCanceled ? "DlcCanceled" : item.Installation?.HasArtifacts == true ? "DlcIncomplete" : "DlcNotInstalled",
            };
            var status = _strings.Get(key);
            if (!downloading && item.State != DlcPackageState.Failed && item.Installation?.UnavailableReasonResourceKey is { } reason)
                status += "\n" + _strings.Get(reason);
            if (_status.Text != status) _status.Text = status;
            AutomationProperties.SetName(this, item.Package.Name);
            AutomationProperties.SetName(_progress, _strings.Format("DlcPackageProgress", item.Package.Name));
            _progress.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
            _progress.IsIndeterminate = item.Progress is null || item.Progress >= 1 || item.State == DlcPackageState.Canceling;
            _progress.Value = (item.Progress ?? 0) * 100;
            var canDownload = item.Package.CanDownload && item.Installation is { CanDownload: true };
            UpdateButton(_download, item.WasCanceled || item.Installation?.HasArtifacts == true ? "DlcResume" : "DlcDownload",
                item.Installation?.IsInstalled != true && item.State != DlcPackageState.Failed && !downloading && item.Package.CanDownload, !busy && canDownload, item.Package.Name);
            UpdateButton(_delete, "DlcDelete", item.Installation is { HasArtifacts: true, CanDelete: true } && !downloading, !busy, item.Package.Name);
            _retryAction = item.FailedAction ?? DlcPackageAction.Inspect;
            UpdateButton(_retry, "DlcRetry", item.State == DlcPackageState.Failed,
                !busy && (_retryAction != DlcPackageAction.Download || canDownload), item.Package.Name);
            UpdateButton(_cancel, "DlcCancel", downloading, item.State == DlcPackageState.Downloading, item.Package.Name);
        }

        private void UpdateButton(Button button, string key, bool visible, bool enabled, string name)
        {
            var label = _strings.Get(key);
            button.Content = label; button.Visibility = visible ? Visibility.Visible : Visibility.Collapsed; button.IsEnabled = enabled;
            AutomationProperties.SetName(button, _strings.Format("DlcPackageAction", label, name));
        }

        public static string Size(IAppStringLocalizer strings, long? bytes) => bytes is >= 0
            ? strings.Format("DlcSizeFormat", (bytes.Value / 1_000_000_000d).ToString("0.00", strings.Culture), bytes.Value.ToString("N0", strings.Culture))
            : strings.Get("DlcUnknownSize");
    }
}
