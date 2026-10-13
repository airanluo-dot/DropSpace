using DropSpace.Core.Transfer;
using DropSpace.Infrastructure.Actions;
using DropSpace.Infrastructure.Sharing;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace DropSpace.App.Views;

public sealed partial class MainPage
{
    private Task? _nearbyManagerTask;

    private async void OnManageNearbyLinksClicked(object sender, RoutedEventArgs args) =>
        await RunAsync(() => ShowNearbySharesAsync());

    private Task ShowNearbySharesAsync(Guid? selectedId = null)
    {
        if (_nearbyManagerTask is { IsCompleted: false }) return _nearbyManagerTask;
        return _nearbyManagerTask = RunNearbyManagerAsync(selectedId, _dialogLifetime.Token);
    }

    private async Task RunNearbyManagerAsync(Guid? selectedId, CancellationToken token)
    {
        string? feedbackKey = null;
        while (!token.IsCancellationRequested)
        {
            var request = await ShowNearbyListAsync(selectedId, feedbackKey, token);
            token.ThrowIfCancellationRequested();
            if (request.Action == NearbyLinkAction.Close) return;
            selectedId = request.ShareId;
            feedbackKey = null;
            try
            {
                // The list carries metadata only. Resolve authority again after
                // the preceding dialog has fully closed, for every action.
                var descriptor = selectedId is { } id ? _sharing.GetNearbyShareDescriptor(id) : null;
                if (descriptor is null)
                {
                    feedbackKey = "NearbyLinkUnavailable";
                    continue;
                }

                switch (request.Action)
                {
                    case NearbyLinkAction.Copy:
                        await _viewModel.CopyTextAsync(descriptor.Url.ToString(), token);
                        token.ThrowIfCancellationRequested();
                        feedbackKey = _sharing.GetNearbyShareDescriptor(descriptor.ShareId) is null
                            ? "NearbyLinkUnavailable" : "ItemCopied";
                        break;
                    case NearbyLinkAction.Qr:
                        if (!await ShowNearbyQrAsync(descriptor.ShareId, token)) feedbackKey = "NearbyLinkUnavailable";
                        break;
                    case NearbyLinkAction.Revoke:
                        if (await ShowConfirmationAsync(_strings.Get("RevokeNearbyLinkTitle"),
                            _strings.Format("RevokeNearbyLinkContent", request.DisplayName ?? _strings.Get("NearbyLinkLabel")),
                            _strings.Get("RevokeShareButton")))
                        {
                            token.ThrowIfCancellationRequested();
                            var revoked = _sharing.GetNearbyShareDescriptor(descriptor.ShareId) is not null &&
                                _sharing.RevokeNearby(descriptor.ShareId);
                            feedbackKey = revoked ? "RevokeNearbyLinkCompleted" : "NearbyLinkUnavailable";
                            selectedId = null;
                        }
                        break;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "A nearby-link management action failed.");
                feedbackKey = "OperationIncompleteContent";
            }
        }
    }

    private async Task<NearbyLinkRequest> ShowNearbyListAsync(Guid? selectedId, string? feedbackKey, CancellationToken token)
    {
        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            DisplayMemberPath = nameof(NearbyShareSummary.DisplayName),
            MaxHeight = 220,
        };
        AutomationProperties.SetName(list, _strings.Get("NearbyLinksTitle"));
        var empty = ShareText("NearbyLinksEmpty");
        var disabled = ShareText("NearbyLinksDisabled");
        disabled.Visibility = _viewModel.EnableNearbySharing ? Visibility.Collapsed : Visibility.Visible;
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var expires = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var url = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 96 };
        AutomationProperties.SetName(url, _strings.Get("NearbyLinkLabel"));
        var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = feedbackKey is null ? string.Empty : _strings.Get(feedbackKey) };
        AutomationProperties.SetLiveSetting(feedback, AutomationLiveSetting.Polite);
        var revoke = new Button { Content = _strings.Get("RevokeShareButton"), HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = false };
        var details = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        details.Children.Add(summary);
        details.Children.Add(expires);
        details.Children.Add(url);
        details.Children.Add(revoke);
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(ShareText("NearbyShareReady"));
        body.Children.Add(ShareText("NearbyLinksLifetime"));
        body.Children.Add(disabled);
        body.Children.Add(empty);
        body.Children.Add(list);
        body.Children.Add(details);
        body.Children.Add(feedback);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = _strings.Get("NearbyLinksTitle"),
            Content = new ScrollViewer { Content = body, MaxHeight = Math.Max(120, Math.Min(560, (XamlRoot?.Size.Height ?? 680) - 160)) },
            PrimaryButtonText = _strings.Get("CopyShareLink"),
            SecondaryButtonText = _strings.Get("OpenShareQr"),
            CloseButtonText = _strings.Get("CommonClose"),
            IsPrimaryButtonEnabled = false,
            IsSecondaryButtonEnabled = false,
            DefaultButton = ContentDialogButton.Close,
        };
        var active = true;
        var refreshing = false;
        var queued = 0;
        var revision = -1L;
        var requestedAction = NearbyLinkAction.Close;

        void SelectCurrent()
        {
            var item = list.SelectedItem as NearbyShareSummary;
            var descriptor = item is null ? null : _sharing.GetNearbyShareDescriptor(item.ShareId);
            selectedId = descriptor?.ShareId;
            var available = item is not null && descriptor is not null;
            dialog.IsPrimaryButtonEnabled = available;
            dialog.IsSecondaryButtonEnabled = available;
            revoke.IsEnabled = available;
            details.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
            url.Text = descriptor?.Url.ToString() ?? string.Empty;
            summary.Text = item is null ? string.Empty : _strings.Format("NearbyLinksSummary", item.ItemCount, FormatTransferBytes(item.TotalBytes));
            expires.Text = descriptor is null ? string.Empty : _strings.Format("ShareExpires", descriptor.ExpiresAtUtc.ToLocalTime().ToString("g", _strings.Culture));
            if (item is not null && !available) feedback.Text = _strings.Get("NearbyLinkUnavailable");
        }

        void Refresh()
        {
            if (!active || token.IsCancellationRequested) return;
            disabled.Visibility = _viewModel.EnableNearbySharing ? Visibility.Collapsed : Visibility.Visible;
            var snapshot = _sharing.GetNearbyShares();
            if (snapshot.Revision == revision) return;
            revision = snapshot.Revision;
            var previousId = selectedId;
            refreshing = true;
            try
            {
                list.ItemsSource = snapshot.Items;
                list.SelectedItem = snapshot.Items.FirstOrDefault(item => item.ShareId == previousId);
            }
            finally { refreshing = false; }
            SelectCurrent();
            if (previousId is not null && selectedId is null) feedback.Text = _strings.Get("NearbyLinkUnavailable");
            empty.Visibility = snapshot.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        void Changed(object? sender, EventArgs args)
        {
            if (token.IsCancellationRequested || Interlocked.Exchange(ref queued, 1) != 0) return;
            if (!DispatcherQueue.TryEnqueue(() =>
            {
                Interlocked.Exchange(ref queued, 0);
                if (active && !token.IsCancellationRequested) Refresh();
            })) Interlocked.Exchange(ref queued, 0);
        }

        void SettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(_viewModel.Settings)) Changed(sender, EventArgs.Empty);
        }

        list.SelectionChanged += (_, _) => { if (!refreshing && active && !token.IsCancellationRequested) SelectCurrent(); };
        dialog.Opened += (_, _) =>
        {
            if (active && !token.IsCancellationRequested && _presentationActive) list.Focus(FocusState.Programmatic);
        };
        revoke.Click += (_, _) =>
        {
            if (!active || token.IsCancellationRequested || selectedId is null) return;
            requestedAction = NearbyLinkAction.Revoke;
            dialog.Hide();
        };
        _sharing.NearbySharesChanged += Changed;
        _viewModel.PropertyChanged += SettingsChanged;
        try
        {
            Refresh();
            var result = await ShowOwnedDialogAsync(dialog, token);
            token.ThrowIfCancellationRequested();
            // No click handler opens another dialog. Confirmation/QR/copy are
            // performed only after this owned ShowAsync has finished.
            if (result == ContentDialogResult.Primary) requestedAction = NearbyLinkAction.Copy;
            if (result == ContentDialogResult.Secondary) requestedAction = NearbyLinkAction.Qr;
            return new NearbyLinkRequest(requestedAction, selectedId, (list.SelectedItem as NearbyShareSummary)?.DisplayName);
        }
        finally
        {
            active = false;
            _sharing.NearbySharesChanged -= Changed;
            _viewModel.PropertyChanged -= SettingsChanged;
            url.Text = string.Empty;
            list.ItemsSource = null;
        }
    }

    private async Task<bool> ShowNearbyQrAsync(Guid shareId, CancellationToken pageToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(pageToken);
        var qr = new Image { MaxWidth = 320, MaxHeight = 320 };
        var active = true;
        var invalidated = false;
        var queued = 0;
        void Changed(object? sender, EventArgs args)
        {
            if (pageToken.IsCancellationRequested || Interlocked.Exchange(ref queued, 1) != 0) return;
            if (!DispatcherQueue.TryEnqueue(() =>
            {
                Interlocked.Exchange(ref queued, 0);
                if (!active || pageToken.IsCancellationRequested || _sharing.GetNearbyShareDescriptor(shareId) is not null) return;
                invalidated = true;
                qr.Source = null;
                lifetime.Cancel();
            })) Interlocked.Exchange(ref queued, 0);
        }
        _sharing.NearbySharesChanged += Changed;
        try
        {
            var descriptor = _sharing.GetNearbyShareDescriptor(shareId);
            if (descriptor is null) return false;
            using var stream = new InMemoryRandomAccessStream();
            using (var output = stream.GetOutputStreamAt(0))
            using (var writer = new DataWriter(output))
            {
                writer.WriteBytes(QrCodeActionService.RenderPng(descriptor.Url.ToString()));
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            pageToken.ThrowIfCancellationRequested();
            if (invalidated || _sharing.GetNearbyShareDescriptor(shareId) is null) return false;
            qr.Source = bitmap;
            AutomationProperties.SetName(qr, _strings.Get("NearbyLinkQrAutomationName"));
            try
            {
                await ShowOwnedDialogAsync(new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = _strings.Get("OpenShareQr"),
                    Content = qr,
                    CloseButtonText = _strings.Get("CommonClose"),
                }, lifetime.Token);
            }
            catch (OperationCanceledException) when (invalidated && !pageToken.IsCancellationRequested) { return false; }
            return _sharing.GetNearbyShareDescriptor(shareId) is not null;
        }
        finally
        {
            active = false;
            _sharing.NearbySharesChanged -= Changed;
            qr.Source = null;
        }
    }

    private TextBlock ShareText(string key) => new() { Text = _strings.Get(key), TextWrapping = TextWrapping.Wrap };
    private enum NearbyLinkAction { Close, Copy, Qr, Revoke }
    private sealed record NearbyLinkRequest(NearbyLinkAction Action, Guid? ShareId, string? DisplayName);

    private async Task ShowShareDescriptorAsync(ShareDescriptor descriptor)
    {
        if (!descriptor.IsEncrypted)
        {
            await ShowNearbySharesAsync(descriptor.ShareId);
            return;
        }

        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = descriptor.IsEncrypted ? _strings.Get("EncryptedShareReady") : _strings.Get("NearbyShareReady"),
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBox
        {
            Text = descriptor.Url.ToString(),
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock
        {
            Text = _strings.Format("ShareExpires", descriptor.ExpiresAtUtc.ToLocalTime().ToString("g", _strings.Culture)),
            TextWrapping = TextWrapping.Wrap,
        });
        ContentDialog? dialog = null;
        if (descriptor.IsEncrypted && _sharing.CanRevokeInternet(descriptor.ShareId))
        {
            var revokeButton = new Button { Content = _strings.Get("RevokeShareButton"), HorizontalAlignment = HorizontalAlignment.Left };
            revokeButton.Click += async (_, _) =>
            {
                dialog?.Hide();
                if (!await ShowConfirmationAsync(_strings.Get("RevokeShareTitle"), _strings.Get("RevokeShareContent"), _strings.Get("RevokeShareButton"))) return;
                await RunAsync(async () =>
                {
                    var revoked = await _sharing.RevokeInternetAsync(descriptor.ShareId);
                    await ShowMessageAsync(
                        revoked ? _strings.Get("RevokeShareTitle") : _strings.Get("TransferUnavailableTitle"),
                        revoked ? _strings.Get("RevokeShareCompleted") : _strings.Get("ActionUnavailable"));
                });
            };
            content.Children.Add(revokeButton);
        }
        dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = _strings.Get("ShareReadyTitle"),
            Content = content,
            PrimaryButtonText = _strings.Get("CopyShareLink"),
            SecondaryButtonText = _strings.Get("OpenShareQr"),
            CloseButtonText = _strings.Get("CommonClose"),
            DefaultButton = ContentDialogButton.Primary,
        };
        var result = await ShowOwnedDialogAsync(dialog);
        if (result == ContentDialogResult.Primary)
        {
            await _viewModel.CopyTextAsync(descriptor.Url.ToString(), _dialogLifetime.Token);
        }
        else if (result == ContentDialogResult.Secondary)
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(QrCodeActionService.RenderPng(descriptor.Url.ToString()));
                await writer.StoreAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            var qr = new Image { Source = bitmap, MaxWidth = 320, MaxHeight = 320 };
            try
            {
                await ShowOwnedDialogAsync(new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = _strings.Get("OpenShareQr"),
                    Content = qr,
                    CloseButtonText = _strings.Get("CommonClose"),
                });
            }
            finally { qr.Source = null; }
        }
    }

}
