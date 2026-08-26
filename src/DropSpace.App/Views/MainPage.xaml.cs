using System.Diagnostics;
using System.Collections.ObjectModel;
using DropSpace.App.Services;
using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Actions;
using DropSpace.Core.Models;
using DropSpace.Core.Preview;
using DropSpace.Core.Transfer;
using DropSpace.Core.Updates;
using DropSpace.Infrastructure.Actions;
using DropSpace.Infrastructure.Network;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Input;
using Windows.UI.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Data.Pdf;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using WinRT.Interop;

namespace DropSpace.App.Views;

public sealed partial class MainPage : Page
{
    private readonly MainViewModel _viewModel;
    private readonly nint _windowHandle;
    private readonly IAppStringLocalizer _strings;
    private readonly QuickPreviewService _previews;
    private readonly IItemActionRegistry _actions;
    private readonly DeviceHandoffService _deviceHandoff;
    private readonly CrossDeviceClipboardService _crossDeviceClipboard;
    private readonly DropLinkHost _dropLinkHost;
    private readonly ItemSharingService _sharing;
    private readonly ObservableCollection<DeviceDescriptor> _discoveredDevices = [];
    private readonly Dictionary<Guid, PairedPeer> _pairedPeers = [];
    private readonly SemaphoreSlim _dialogGate = new(1, 1);
    private bool _syncingNavigation;
    private bool _syncingSettings;

    public MainPage(
        MainViewModel viewModel,
        nint windowHandle,
        IAppStringLocalizer strings,
        QuickPreviewService previews,
        IItemActionRegistry actions,
        DeviceHandoffService deviceHandoff,
        CrossDeviceClipboardService crossDeviceClipboard,
        DropLinkHost dropLinkHost,
        ItemSharingService sharing)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        _windowHandle = windowHandle;
        _strings = strings;
        _previews = previews;
        _actions = actions;
        _deviceHandoff = deviceHandoff;
        _crossDeviceClipboard = crossDeviceClipboard;
        _dropLinkHost = dropLinkHost;
        _sharing = sharing;
        try
        {
            InitializeComponent();
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("Main-page XAML initialization failed.", exception);
        }

        DataContext = viewModel;
        DiscoveredDevicesList.ItemsSource = _discoveredDevices;
        _dropLinkHost.TransferOffered += OnTransferOfferedAsync;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    public async Task ConfirmClearAsync(ClearRange range)
    {
        await RunAsync(async () =>
        {
            var count = await _viewModel.GetClearPreviewCountAsync(range);
            if (count == 0)
            {
                await ShowMessageAsync(_strings.Get("ClearNothingTitle"), _strings.Get("ClearNothingContent"));
                return;
            }

            var rangeLabel = range switch
            {
                ClearRange.LastHour => _strings.Get("ClearRangeLastHour"),
                ClearRange.Today => _strings.Get("ClearRangeToday"),
                _ => _strings.Get("ClearRangeAll"),
            };
            var result = await ShowConfirmationAsync(
                _strings.Format("ClearConfirmTitle", rangeLabel),
                _strings.Format("ClearConfirmContent", count),
                _strings.Get("ClearConfirmButton"));
            if (result)
            {
                await _viewModel.ClearClipboardAsync(range);
            }
        });
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        SyncNavigationSelection();
        SyncSettingsControls();
        UpdateSectionChrome();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private async void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_syncingNavigation || args.SelectedItemContainer?.Tag is not string section)
        {
            return;
        }

        await RunAsync(() => SelectSectionAsync(section));
    }

    private async Task SelectSectionAsync(string section)
    {
        _syncingNavigation = true;
        try
        {
            Navigation.SelectedItem = GetNavigationItem(section);
        }
        finally
        {
            _syncingNavigation = false;
        }

        await _viewModel.NavigateAsync(section);
        UpdateSectionChrome();
    }

    private async void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        await Task.Yield();
        _viewModel.SearchText = sender.Text;
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape)
        {
            SearchBox.Text = string.Empty;
            args.Handled = true;
        }
    }

    private async void OnAddFilesClicked(object sender, RoutedEventArgs args)
    {
        await RunAsync(async () =>
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, _windowHandle);
            var files = await picker.PickMultipleFilesAsync();
            if (files.Count > 0)
            {
                await _viewModel.AddPathsAsync(files.Select(file => file.Path));
            }
        });
    }

    private async void OnAddFolderClicked(object sender, RoutedEventArgs args)
    {
        await RunAsync(async () =>
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, _windowHandle);
            var folder = await picker.PickSingleFolderAsync();
            if (folder is not null)
            {
                await _viewModel.AddPathsAsync([folder.Path]);
            }
        });
    }

    private async void OnAddTextUrlClicked(object sender, RoutedEventArgs args)
    {
        var editor = new TextBox
        {
            AcceptsReturn = true,
            MinWidth = 420,
            MinHeight = 140,
            TextWrapping = TextWrapping.Wrap,
            PlaceholderText = _strings.Get("AddTextUrlPlaceholder"),
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = _strings.Get("AddTextUrlTitle"),
            Content = editor,
            PrimaryButtonText = _strings.Get("AddTextUrlConfirm"),
            CloseButtonText = _strings.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(editor.Text))
        {
            await RunAsync(() => _viewModel.AddTextToSpaceAsync(editor.Text, "manual-text-url"));
        }
    }

    private void OnDragEnter(object sender, DragEventArgs args) => SetDropHint(args, true);

    private void OnDragLeave(object sender, DragEventArgs args) => DropHint.Visibility = Visibility.Collapsed;

    private void OnDragOver(object sender, DragEventArgs args) => SetDropHint(args, true);

    private async void OnDrop(object sender, DragEventArgs args)
    {
        DropHint.Visibility = Visibility.Collapsed;
        await RunAsync(async () =>
        {
            if (args.DataView.Contains(StandardDataFormats.StorageItems))
            {
                var storageItems = await args.DataView.GetStorageItemsAsync();
                await _viewModel.AddPathsBatchAsync(
                    storageItems.Where(item => !string.IsNullOrWhiteSpace(item.Path)).Select(item => item.Path),
                    null,
                    "main-window-drop");
                return;
            }

            if (args.DataView.Contains(StandardDataFormats.WebLink))
            {
                await _viewModel.AddTextToSpaceAsync(
                    (await args.DataView.GetWebLinkAsync()).AbsoluteUri,
                    "main-window-url-drop");
            }
            else if (args.DataView.Contains(StandardDataFormats.Text))
            {
                await _viewModel.AddTextToSpaceAsync(
                    await args.DataView.GetTextAsync(),
                    "main-window-text-drop");
            }
        });
    }

    private void OnDragItemsStarting(object sender, DragItemsStartingEventArgs args)
    {
        var cards = args.Items.OfType<ItemCardViewModel>().ToArray();
        if (cards.Length == 1 && cards[0].DropBatchId is { } batchId)
        {
            cards = _viewModel.Items.Where(card => card.DropBatchId == batchId).ToArray();
        }
        var storageItems = cards
            .Select(card => card.DragStorageItem)
            .Where(item => item is not null)
            .Cast<IStorageItem>()
            .ToArray();
        if (storageItems.Length > 0)
        {
            args.Data.SetStorageItems(storageItems, readOnly: true);
        }
        else if (cards.Length == 1 && cards[0].Item.Text?.InlineText is { } text)
        {
            args.Data.SetText(text);
            if (cards[0].Item.Url is { NormalizedUrl: var url } && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                args.Data.SetWebLink(uri);
            }
        }
        else
        {
            args.Cancel = true;
            return;
        }

        args.Data.RequestedOperation = DataPackageOperation.Copy;
    }

    private void OnToggleBatchClicked(object sender, RoutedEventArgs args)
    {
        if (GetCard(sender) is { } card)
        {
            _viewModel.ToggleBatchExpanded(card);
        }
    }

    private async void OnPinBatchClicked(object sender, RoutedEventArgs args)
    {
        if (GetCard(sender) is { } card)
        {
            await RunAsync(() => _viewModel.ToggleBatchPinAsync(card));
        }
    }

    private async void OnRemoveBatchClicked(object sender, RoutedEventArgs args)
    {
        if (GetCard(sender) is { } card)
        {
            await RunAsync(() => _viewModel.RemoveBatchAsync(card));
        }
    }

    private async void OnItemDoubleTapped(object sender, DoubleTappedRoutedEventArgs args)
    {
        if (ItemsList.SelectedItem is ItemCardViewModel card && card.CanOpen)
        {
            await RunAsync(() => _viewModel.OpenAsync(card));
        }
    }

    private async void OnItemsListKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (ItemsList.SelectedItem is not ItemCardViewModel card)
        {
            return;
        }

        if (args.Key == VirtualKey.Enter && card.CanOpen)
        {
            args.Handled = true;
            await RunAsync(() => _viewModel.OpenAsync(card));
        }
        else if (args.Key == VirtualKey.Delete)
        {
            args.Handled = true;
            await ConfirmRemoveAsync(card);
        }
    }

    private async void OnOpenItemClicked(object sender, RoutedEventArgs args)
    {
        if (GetCard(sender) is { } card)
        {
            await RunAsync(() => _viewModel.OpenAsync(card));
        }
    }

    private async void OnDeviceHandoffToggled(object sender, RoutedEventArgs args)
    {
        if (!_syncingSettings)
        {
            await RunAsync(() => _viewModel.UpdateSettingsAsync(
                _viewModel.Settings with { EnableDeviceHandoff = DeviceHandoffToggle.IsOn }));
            UpdateDeviceStatus();
        }
    }

    private async void OnCrossDeviceClipboardToggled(object sender, RoutedEventArgs args)
    {
        if (!_syncingSettings)
        {
            await RunAsync(() => _viewModel.UpdateSettingsAsync(
                _viewModel.Settings with { EnableCrossDeviceClipboard = CrossDeviceClipboardToggle.IsOn }));
        }
    }

    private async void OnNearbySharingToggled(object sender, RoutedEventArgs args)
    {
        if (!_syncingSettings)
        {
            await RunAsync(() => _viewModel.UpdateSettingsAsync(
                _viewModel.Settings with { EnableNearbySharing = NearbySharingToggle.IsOn }));
        }
    }

    private async void OnInternetSharingToggled(object sender, RoutedEventArgs args)
    {
        if (!_syncingSettings)
        {
            await RunAsync(() => _viewModel.UpdateSettingsAsync(
                _viewModel.Settings with { EnableInternetSharing = InternetSharingToggle.IsOn }));
            UpdateDeviceStatus();
        }
    }

    private async void OnDefaultClipboardSyncModeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_syncingSettings && DefaultClipboardSyncModeCombo.SelectedItem is ComboBoxItem { Tag: string value } &&
            Enum.TryParse<ClipboardSyncMode>(value, out var mode))
        {
            await RunAsync(() => _viewModel.UpdateSettingsAsync(
                _viewModel.Settings with { DefaultClipboardSyncMode = mode }));
        }
    }

    private async void OnRefreshDevicesClicked(object sender, RoutedEventArgs args)
    {
        await RunAsync(async () =>
        {
            if (!_viewModel.EnableDeviceHandoff)
            {
                await ShowMessageAsync(_strings.Get("DevicesDisabledTitle"), _strings.Get("DevicesDisabledContent"));
                return;
            }

            var devices = await _deviceHandoff.DiscoverAsync(TimeSpan.FromSeconds(3));
            _discoveredDevices.Clear();
            foreach (var device in devices) _discoveredDevices.Add(device);
            DeviceStatusText.Text = devices.Count == 0
                ? _strings.Get("NoDevicesFound")
                : _strings.Format("DevicesFound", devices.Count);
        });
    }

    private async void OnPairDeviceClicked(object sender, RoutedEventArgs args)
    {
        if (DiscoveredDevicesList.SelectedItem is not DeviceDescriptor descriptor) return;
        await RunAsync(async () =>
        {
            var peer = await _deviceHandoff.PairAsync(descriptor, ConfirmPairingSasAsync);
            _crossDeviceClipboard.ConfigurePeer(peer, descriptor.Endpoint, _viewModel.DefaultClipboardSyncMode);
            _pairedPeers[peer.Id] = new PairedPeer(peer, descriptor.Endpoint);
            DeviceStatusText.Text = _strings.Format("PairedDevice", peer.DisplayName);
        });
    }

    private async Task<bool> ConfirmPairingSasAsync(int sas, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = _strings.Get("PairingSasTitle"),
            Content = _strings.Format("PairingSasContent", sas.ToString("D6", System.Globalization.CultureInfo.InvariantCulture)),
            PrimaryButtonText = _strings.Get("PairingSasConfirm"),
            CloseButtonText = _strings.Get("CommonCancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private Task OnTransferOfferedAsync(IncomingTransferOffer offer)
    {
        _ = DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var accepted = await ShowIncomingTransferDialogAsync(offer);
                await _deviceHandoff.ApproveIncomingTransferAsync(offer.SessionId, accepted);
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                await _deviceHandoff.ApproveIncomingTransferAsync(offer.SessionId, false);
            }
        });
        return Task.CompletedTask;
    }

    private async Task<bool> ShowIncomingTransferDialogAsync(IncomingTransferOffer offer)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = _strings.Get("IncomingTransferTitle"),
            Content = _strings.Format("IncomingTransferContent", offer.Manifest.Items.Count, offer.Manifest.TotalBytes),
            PrimaryButtonText = _strings.Get("IncomingTransferAccept"),
            CloseButtonText = _strings.Get("IncomingTransferReject"),
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void OnPreviewItemClicked(object sender, RoutedEventArgs args)
    {
        if (GetCard(sender) is not { } card) return;
        await RunAsync(() => ShowPreviewAsync(card));
    }

    private async Task ShowPreviewAsync(ItemCardViewModel card)
    {
        var descriptor = await _previews.LoadAsync(card.Item, inline: false);
        var content = await CreatePreviewContentAsync(descriptor, card.Item);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = card.Title,
            Content = content,
            CloseButtonText = _strings.Get("CommonClose"),
        };
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            if (content is IDisposable disposable) disposable.Dispose();
        }
    }

    private async Task<UIElement> CreatePreviewContentAsync(PreviewDescriptor descriptor, DropItem item)
    {
        if (descriptor.Bytes is { Length: > 0 } bytes && descriptor.Kind == PreviewKind.Image)
        {
            return await CreateImageElementAsync(bytes, 640, 520);
        }

        if (descriptor.Bytes is { Length: > 0 } pdfBytes && descriptor.Kind == PreviewKind.Pdf)
        {
            try
            {
                return await RenderPdfPageAsync(pdfBytes, 1, 640, 520);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                // Keep the metadata fallback visible for a PDF that the platform renderer cannot decode.
            }
        }

        if (descriptor.Kind is PreviewKind.Audio or PreviewKind.Video &&
            _previews.ResolveSourcePath(item) is { } mediaPath && File.Exists(mediaPath))
        {
            try
            {
                return await CreateMediaElementAsync(mediaPath);
            }
            catch (Exception exception) when (exception is FileNotFoundException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Keep the bounded metadata fallback visible when a media source disappears.
            }
        }

        if (descriptor.Text is { } text)
        {
            return new ScrollViewer
            {
                MaxHeight = 520,
                Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            };
        }

        return new TextBlock
        {
            Text = descriptor.Metadata.Count == 0
                ? _strings.Get("PreviewUnavailable")
                : string.Join(Environment.NewLine, descriptor.Metadata.Select(pair => string.Concat(pair.Key, ": ", pair.Value))),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
    }

    private static async Task<Image> CreateImageElementAsync(byte[] bytes, double maxWidth, double maxHeight)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        return new Image
        {
            Source = bitmap,
            MaxWidth = maxWidth,
            MaxHeight = maxHeight,
            Stretch = Stretch.Uniform,
        };
    }

    private static async Task<Image> RenderPdfPageAsync(byte[] bytes, int pageNumber, double maxWidth, double maxHeight)
    {
        using var input = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(input.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        input.Seek(0);
        var document = await PdfDocument.LoadFromStreamAsync(input);
        if (document.PageCount == 0) throw new InvalidDataException("The PDF has no pages.");
        var page = document.GetPage((uint)Math.Clamp(pageNumber - 1, 0, (int)document.PageCount - 1));
        using var output = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(output);
        output.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(output);
        return new Image
        {
            Source = bitmap,
            MaxWidth = maxWidth,
            MaxHeight = maxHeight,
            Stretch = Stretch.Uniform,
        };
    }

    private static async Task<MediaPreviewHost> CreateMediaElementAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var player = new MediaPlayer { AutoPlay = false };
        try
        {
            player.Source = MediaSource.CreateFromStorageFile(file);
            var element = new MediaPlayerElement
            {
                AutoPlay = false,
                AreTransportControlsEnabled = true,
                MaxWidth = 640,
                MaxHeight = 520,
            };
            element.SetMediaPlayer(player);
            return new MediaPreviewHost(element, player);
        }
        catch
        {
            player.Dispose();
            throw;
        }
    }

    private async void OnQuickActionClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not MenuFlyoutItem menu || menu.CommandParameter is not string actionText || GetCard(menu) is not { } card) return;
        if (!Enum.TryParse<ItemActionId>(actionText, out var action)) return;
        await RunAsync(async () =>
        {
            var snapshot = DropItemSnapshot.FromItem(card.Item);
            if (action == ItemActionId.SendToDevice)
            {
                await SendToDeviceAsync(card.Item);
                return;
            }
            if (action == ItemActionId.CreateNearbyLink)
            {
                await ShowShareDescriptorAsync(await _sharing.CreateNearbyAsync([card.Item], _viewModel.Settings));
                return;
            }
            if (action == ItemActionId.CreateSecureInternetLink)
            {
                await ShowShareDescriptorAsync(await _sharing.CreateInternetAsync([card.Item], _viewModel.Settings));
                return;
            }
            var result = await _actions.ExecuteAsync(action, new ItemActionContext(new ItemSelectionSnapshot([snapshot])));
            await ShowMessageAsync(
                result.Succeeded ? _strings.Get("ActionCompleted") : _strings.Get("ActionUnavailable"),
                result.OutputPaths.Count == 0 ? result.ErrorCategory ?? _strings.Get("ActionUnavailable") : string.Join(Environment.NewLine, result.OutputPaths));
        });
    }

    private async Task SendToDeviceAsync(DropItem item)
    {
        if (!_viewModel.EnableDeviceHandoff)
        {
            await ShowMessageAsync(_strings.Get("DevicesDisabledTitle"), _strings.Get("DevicesDisabledContent"));
            return;
        }

        var peer = await SelectPairedPeerAsync();
        if (peer is null) return;
        if (item.File?.OriginalPath is { } path)
        {
            await _deviceHandoff.SendFilesAsync(peer.Peer, peer.Endpoint, [path]);
            await ShowMessageAsync(_strings.Get("TransferSentTitle"), _strings.Get("TransferSentContent"));
            return;
        }

        if (item.Text is not null || item.Url is not null || item.Image is not null)
        {
            var response = await _crossDeviceClipboard.SendManualAsync(peer.Peer, peer.Endpoint, item);
            await ShowMessageAsync(
                response.Accepted ? _strings.Get("TransferSentTitle") : _strings.Get("TransferUnavailableTitle"),
                response.Accepted ? _strings.Get("TransferSentContent") : response.ErrorCategory ?? _strings.Get("ActionUnavailable"));
            return;
        }

        await ShowMessageAsync(_strings.Get("TransferUnavailableTitle"), _strings.Get("ActionUnavailable"));
    }

    private async Task<PairedPeer?> SelectPairedPeerAsync()
    {
        if (_pairedPeers.Count == 0)
        {
            await ShowMessageAsync(_strings.Get("NoPairedDevicesTitle"), _strings.Get("NoPairedDevicesContent"));
            return null;
        }

        var combo = new ComboBox
        {
            ItemsSource = _pairedPeers.Values.Select(value => value.Peer).ToArray(),
            DisplayMemberPath = nameof(PeerDevice.DisplayName),
            SelectedIndex = 0,
            MinWidth = 280,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = _strings.Get("SelectDeviceTitle"),
            Content = combo,
            PrimaryButtonText = _strings.Get("SendToDeviceAction.Text"),
            CloseButtonText = _strings.Get("CommonCancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || combo.SelectedItem is not PeerDevice selected)
        {
            return null;
        }
        return _pairedPeers.GetValueOrDefault(selected.Id);
    }

    private async Task ShowShareDescriptorAsync(ShareDescriptor descriptor)
    {
        var qrPath = Path.Combine(Path.GetTempPath(), string.Concat("DropSpace-share-", descriptor.ShareId.ToString("N"), ".png"));
        await File.WriteAllBytesAsync(qrPath, QrCodeActionService.RenderPng(descriptor.Url.ToString()));
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
            Text = _strings.Format("ShareExpires", descriptor.ExpiresAtUtc.ToLocalTime().ToString("g")),
            TextWrapping = TextWrapping.Wrap,
        });
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = _strings.Get("ShareReadyTitle"),
            Content = content,
            PrimaryButtonText = _strings.Get("CopyShareLink"),
            SecondaryButtonText = _strings.Get("OpenShareQr"),
            CloseButtonText = _strings.Get("CommonClose"),
            DefaultButton = ContentDialogButton.Primary,
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(descriptor.Url.ToString());
            Clipboard.SetContent(package);
        }
        else if (result == ContentDialogResult.Secondary)
        {
            Process.Start(new ProcessStartInfo(qrPath) { UseShellExecute = true });
        }
    }

    private sealed record PairedPeer(PeerDevice Peer, Uri Endpoint);

    private sealed class MediaPreviewHost : Grid, IDisposable
    {
        private readonly MediaPlayer _player;
        private int _disposed;

        public MediaPreviewHost(MediaPlayerElement element, MediaPlayer player)
        {
            _player = player;
            Children.Add(element);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _player.Pause();
            _player.Source = null;
            _player.Dispose();
        }
    }

    private async void OnCopyItemClicked(object sender, RoutedEventArgs args)
    {
        if (GetCard(sender) is { } card)
        {
            await RunAsync(() => _viewModel.CopyAsync(card));
        }
    }

    private async void OnPinItemClicked(object sender, RoutedEventArgs args)
    {
        if (GetCard(sender) is { } card)
        {
            await RunAsync(() => _viewModel.TogglePinAsync(card));
        }
    }

    private async void OnShowInFolderClicked(object sender, RoutedEventArgs args)
    {
        if (GetCard(sender) is { } card)
        {
            await RunAsync(() => _viewModel.ShowInFolderAsync(card));
        }
    }

    private async void OnRemoveItemClicked(object sender, RoutedEventArgs args)
    {
        if (GetCard(sender) is { } card)
        {
            await ConfirmRemoveAsync(card);
        }
    }

    private async Task ConfirmRemoveAsync(ItemCardViewModel card)
    {
        await RunAsync(async () =>
        {
            var confirmed = await ShowConfirmationAsync(
                _strings.Get("RemoveConfirmTitle"),
                card.IsFileReference
                    ? _strings.Format("RemoveFileReferenceContent", card.Title)
                    : _strings.Format("RemoveStoredItemContent", card.Title),
                _strings.Get("RemoveConfirmButton"));
            if (confirmed)
            {
                await _viewModel.RemoveAsync(card);
            }
        });
    }

    private async void OnLocateItemClicked(object sender, RoutedEventArgs args)
    {
        if (GetCard(sender) is not { } card || card.Item.File is null)
        {
            return;
        }

        await RunAsync(async () =>
        {
            string? path;
            if (card.Item.File.EntryKind == FileEntryKind.Folder)
            {
                var picker = new FolderPicker();
                picker.FileTypeFilter.Add("*");
                InitializeWithWindow.Initialize(picker, _windowHandle);
                path = (await picker.PickSingleFolderAsync())?.Path;
            }
            else
            {
                var picker = new FileOpenPicker();
                picker.FileTypeFilter.Add("*");
                InitializeWithWindow.Initialize(picker, _windowHandle);
                path = (await picker.PickSingleFileAsync())?.Path;
            }

            if (!string.IsNullOrWhiteSpace(path))
            {
                await _viewModel.ReplaceFileReferenceAsync(card, path);
            }
        });
    }

    private async void OnExportItemClicked(object sender, RoutedEventArgs args)
    {
        if (GetCard(sender) is not { } card)
        {
            return;
        }

        await RunAsync(async () =>
        {
            var extension = card.Item.Image?.MimeType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) == true
                ? ".jpg"
                : ".png";
            var picker = new FileSavePicker
            {
                SuggestedFileName = $"DropSpace-{DateTime.Now:yyyyMMdd-HHmmss}",
            };
            picker.FileTypeChoices.Add(
                extension == ".jpg" ? _strings.Get("ExportJpegImage") : _strings.Get("ExportPngImage"),
                [extension]);
            InitializeWithWindow.Initialize(picker, _windowHandle);
            var file = await picker.PickSaveFileAsync();
            if (file is not null)
            {
                await _viewModel.ExportImageAsync(card, file.Path);
            }
        });
    }

    private async void OnPauseToggled(object sender, RoutedEventArgs args)
    {
        if (!_syncingSettings)
        {
            await RunAsync(() => _viewModel.SetClipboardPausedAsync(PauseToggle.IsOn));
        }
    }

    private async void OnCaptureImagesToggled(object sender, RoutedEventArgs args)
    {
        if (!_syncingSettings)
        {
            await RunAsync(() => _viewModel.UpdateSettingsAsync(
                _viewModel.Settings with { CaptureImages = CaptureImagesToggle.IsOn }));
        }
    }

    private async void OnCaptureFilesToggled(object sender, RoutedEventArgs args)
    {
        if (!_syncingSettings)
        {
            await RunAsync(() => _viewModel.UpdateSettingsAsync(
                _viewModel.Settings with { CaptureFiles = CaptureFilesToggle.IsOn }));
        }
    }

    private async void OnCaptureFoldersToggled(object sender, RoutedEventArgs args)
    {
        if (!_syncingSettings)
        {
            await RunAsync(() => _viewModel.UpdateSettingsAsync(
                _viewModel.Settings with { CaptureFolders = CaptureFoldersToggle.IsOn }));
        }
    }

    private async void OnStartWithWindowsToggled(object sender, RoutedEventArgs args)
    {
        if (!_syncingSettings)
        {
            await RunAsync(() => _viewModel.UpdateSettingsAsync(
                _viewModel.Settings with { StartWithWindows = StartWithWindowsToggle.IsOn }));
        }
    }

    private async void OnAutoCheckUpdatesToggled(object sender, RoutedEventArgs args)
    {
        if (!_syncingSettings)
        {
            await RunAsync(() => _viewModel.UpdateSettingsAsync(
                _viewModel.Settings with { AutoCheckForUpdates = AutoCheckUpdatesToggle.IsOn }));
        }
    }

    private async void OnAutoDownloadUpdatesToggled(object sender, RoutedEventArgs args)
    {
        if (!_syncingSettings)
        {
            await RunAsync(() => _viewModel.UpdateSettingsAsync(
                _viewModel.Settings with { AutoDownloadUpdates = AutoDownloadUpdatesToggle.IsOn }));
        }
    }

    private async void OnAutoInstallUpdatesToggled(object sender, RoutedEventArgs args)
    {
        if (!_syncingSettings)
        {
            await RunAsync(() => _viewModel.UpdateSettingsAsync(
                _viewModel.Settings with { AutoInstallUpdates = AutoInstallUpdatesToggle.IsOn }));
        }
    }

    private async void OnUpdateChannelChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_syncingSettings && UpdateChannelCombo.SelectedItem is ComboBoxItem { Tag: string value } &&
            Enum.TryParse<UpdateChannel>(value, out var channel))
        {
            await RunAsync(() => _viewModel.UpdateSettingsAsync(
                _viewModel.Settings with { UpdateChannel = channel }));
        }
    }

    private async void OnCheckForUpdatesClicked(object sender, RoutedEventArgs args) =>
        await RunAsync(() => _viewModel.CheckForUpdatesManuallyAsync());

    private async void OnDownloadUpdateClicked(object sender, RoutedEventArgs args) =>
        await RunAsync(() => _viewModel.DownloadUpdateAsync());

    private async void OnInstallUpdateClicked(object sender, RoutedEventArgs args) =>
        await RunAsync(() => _viewModel.InstallUpdateAsync());

    private async void OnOpenUpdateLocationClicked(object sender, RoutedEventArgs args) =>
        await RunAsync(async () => _ = await _viewModel.OpenUpdateLocationAsync());

    private async void OnViewReleaseNotesClicked(object sender, RoutedEventArgs args) =>
        await RunAsync(async () => _ = await _viewModel.OpenUpdateReleaseNotesAsync());

    private async void OnOpenDropTraySettingsClicked(object sender, RoutedEventArgs args)
    {
        await RunAsync(async () =>
        {
            if (!await _viewModel.OpenDropTraySettingsAsync())
            {
                await ShowMessageAsync(
                    _strings.Get("DropTraySettingsUnavailableTitle"),
                    _strings.Get("DropTraySettingsUnavailableContent"));
            }
        });
    }

    private void OnCopyDragCompatibilityReportClicked(object sender, RoutedEventArgs args) =>
        _viewModel.CopyDragCompatibilityReport();

    private async void OnClipboardLimitsChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncingSettings ||
            double.IsNaN(MaxImageMegabytesNumber.Value) ||
            double.IsNaN(MaxImageMegapixelsNumber.Value) ||
            double.IsNaN(MaxClipboardFileMegabytesNumber.Value) ||
            double.IsNaN(MaxClipboardFileTotalMegabytesNumber.Value) ||
            double.IsNaN(MaxClipboardFileItemsNumber.Value))
        {
            return;
        }

        var singleFileMegabytes = (long)Math.Round(MaxClipboardFileMegabytesNumber.Value);
        var totalFileMegabytes = (long)Math.Round(MaxClipboardFileTotalMegabytesNumber.Value);
        if (totalFileMegabytes < singleFileMegabytes)
        {
            await ShowMessageAsync(_strings.Get("ClipboardLimitInvalidTitle"), _strings.Get("ClipboardLimitInvalidContent"));
            SyncSettingsControls();
            return;
        }

        await RunAsync(() => _viewModel.UpdateSettingsAsync(_viewModel.Settings with
        {
            MaxImageBytes = checked((long)Math.Round(MaxImageMegabytesNumber.Value * 1024 * 1024)),
            MaxImagePixels = checked((long)Math.Round(MaxImageMegapixelsNumber.Value * 1_000_000)),
            MaxClipboardFileBytes = checked(singleFileMegabytes * 1024 * 1024),
            MaxClipboardFileTotalBytes = checked(totalFileMegabytes * 1024 * 1024),
            MaxClipboardFileItems = (int)Math.Round(MaxClipboardFileItemsNumber.Value),
        }));
    }

    private async void OnRetentionChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_syncingSettings || double.IsNaN(RetentionDaysNumber.Value) || double.IsNaN(RetentionCountNumber.Value))
        {
            return;
        }

        var days = (int)Math.Round(RetentionDaysNumber.Value);
        var count = (int)Math.Round(RetentionCountNumber.Value);
        if (days is >= 1 and <= 3650 && count is >= 10 and <= 100_000)
        {
            await RunAsync(() => _viewModel.UpdateSettingsAsync(
                _viewModel.Settings with { RetentionDays = days, RetentionItemCount = count }));
        }
    }

    private async void OnThemeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_syncingSettings && ThemeCombo.SelectedItem is ComboBoxItem { Tag: string value } &&
            Enum.TryParse<ThemePreference>(value, out var theme))
        {
            await RunAsync(() => _viewModel.UpdateSettingsAsync(_viewModel.Settings with { Theme = theme }));
        }
    }

    private async void OnLanguageChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_syncingSettings && LanguageCombo.SelectedItem is ComboBoxItem { Tag: string value } &&
            Enum.TryParse<AppLanguagePreference>(value, out var language))
        {
            await RunAsync(() => _viewModel.UpdateSettingsAsync(
                _viewModel.Settings with { Language = language }));
        }
    }

    private async void OnCloseBehaviorChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_syncingSettings && CloseBehaviorCombo.SelectedItem is ComboBoxItem { Tag: string value } &&
            Enum.TryParse<CloseBehavior>(value, out var closeBehavior))
        {
            await RunAsync(() => _viewModel.UpdateSettingsAsync(
                _viewModel.Settings with { CloseBehavior = closeBehavior }));
        }
    }

    private async void OnOverlayMotionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_syncingSettings && OverlayMotionCombo.SelectedItem is ComboBoxItem { Tag: string value } &&
            Enum.TryParse<OverlayMotionPreference>(value, out var motion))
        {
            await RunAsync(() => _viewModel.UpdateSettingsAsync(
                _viewModel.Settings with { OverlayMotion = motion }));
        }
    }

    private async void OnOverlayMonitorChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_syncingSettings && OverlayMonitorCombo.SelectedItem is ComboBoxItem { Tag: string value } &&
            Enum.TryParse<OverlayMonitorPreference>(value, out var monitor))
        {
            await RunAsync(() => _viewModel.UpdateSettingsAsync(
                _viewModel.Settings with { OverlayMonitor = monitor }));
        }
    }

    private async void OnFileDragWakeModeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_syncingSettings && FileDragWakeModeCombo.SelectedItem is ComboBoxItem { Tag: string value } &&
            Enum.TryParse<FileDragWakeMode>(value, out var mode))
        {
            await RunAsync(() => _viewModel.UpdateSettingsAsync(
                _viewModel.Settings with { FileDragWakeMode = mode }));
        }
    }

    private async void OnOverlayPlacementModeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_syncingSettings && OverlayPlacementModeCombo.SelectedItem is ComboBoxItem { Tag: string value } &&
            OverlayPlacementMonitorCombo.SelectedValue is string monitorId &&
            Enum.TryParse<OverlayPlacementMode>(value, out var mode))
        {
            if (mode == OverlayPlacementMode.Custom && !_viewModel.CanPersistOverlayPlacement(monitorId))
            {
                SyncPlacementCoordinates();
                return;
            }
            await RunAsync(() => _viewModel.SetOverlayPlacementModeAsync(monitorId, mode));
        }
    }

    private void OnOverlayPlacementMonitorChanged(object sender, SelectionChangedEventArgs args) =>
        SyncPlacementCoordinates();

    private async void OnApplyIslandPlacementClicked(object sender, RoutedEventArgs args)
    {
        if (OverlayPlacementMonitorCombo.SelectedValue is string monitorId &&
            _viewModel.CanPersistOverlayPlacement(monitorId) &&
            !double.IsNaN(OverlayPlacementXNumber.Value) &&
            !double.IsNaN(OverlayPlacementYNumber.Value))
        {
            await RunAsync(() => _viewModel.SetCustomOverlayPlacementAsync(
                monitorId,
                OverlayPlacementXNumber.Value,
                OverlayPlacementYNumber.Value));
        }
    }

    private async void OnResetIslandPlacementClicked(object sender, RoutedEventArgs args)
    {
        if (OverlayPlacementMonitorCombo.SelectedValue is string monitorId)
        {
            await RunAsync(() => _viewModel.ResetOverlayPlacementAsync(monitorId));
            SyncPlacementCoordinates();
        }
    }

    private void OnAdjustIslandPlacementClicked(object sender, RoutedEventArgs args)
    {
        if (OverlayPlacementMonitorCombo.SelectedValue is string monitorId &&
            _viewModel.CanPersistOverlayPlacement(monitorId))
        {
            _viewModel.RequestOverlayPlacementEdit(monitorId);
        }
    }

    private void OnPlacementNumberKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Escape)
        {
            SyncPlacementCoordinates();
            args.Handled = true;
            return;
        }
        if (args.Key == VirtualKey.Enter)
        {
            OnApplyIslandPlacementClicked(sender, args);
            args.Handled = true;
            return;
        }
        var shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(CoreVirtualKeyStates.Down);
        if (shift && sender is NumberBox box && args.Key is VirtualKey.Up or VirtualKey.Down or VirtualKey.Left or VirtualKey.Right)
        {
            var direction = args.Key is VirtualKey.Up or VirtualKey.Right ? 1 : -1;
            box.Value = (double.IsNaN(box.Value) ? 0 : box.Value) + direction * 10;
            args.Handled = true;
        }
    }

    private async void OnQuickPanelHotkeyLostFocus(object sender, RoutedEventArgs args)
    {
        if (!_syncingSettings && !string.Equals(QuickPanelHotkeyText.Text, _viewModel.QuickPanelHotkey, StringComparison.Ordinal))
        {
            await RunAsync(() => _viewModel.UpdateSettingsAsync(
                _viewModel.Settings with { QuickPanelHotkey = QuickPanelHotkeyText.Text.Trim() }));
        }
    }

    private async void OnSmartDragExclusionsLostFocus(object sender, RoutedEventArgs args)
    {
        if (_syncingSettings)
        {
            return;
        }
        var exclusions = SmartDragExclusionsText.Text
            .Split([',', ';', '\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(Path.GetFileNameWithoutExtension)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        await RunAsync(() => _viewModel.UpdateSettingsAsync(
            _viewModel.Settings with { SmartDragExcludedProcesses = exclusions }));
    }

    private async void OnClearLastHourClicked(object sender, RoutedEventArgs args) => await ConfirmClearAsync(ClearRange.LastHour);

    private async void OnClearTodayClicked(object sender, RoutedEventArgs args) => await ConfirmClearAsync(ClearRange.Today);

    private async void OnClearAllClicked(object sender, RoutedEventArgs args) => await ConfirmClearAsync(ClearRange.All);

    private async void OnSpaceAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await RunAsync(() => SelectSectionAsync("Space"));
    }

    private async void OnClipboardAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await RunAsync(() => SelectSectionAsync("Clipboard"));
    }

    private async void OnPinnedAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await RunAsync(() => SelectSectionAsync("Pinned"));
    }

    private async void OnSearchAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (_viewModel.IsSettingsVisible)
        {
            await RunAsync(() => SelectSectionAsync("Space"));
        }

        SearchBox.Focus(FocusState.Keyboard);
    }

    private async void OnSettingsAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await RunAsync(() => SelectSectionAsync("Settings"));
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainViewModel.Settings))
        {
            SyncSettingsControls();
        }
        else if (args.PropertyName == nameof(MainViewModel.CurrentSection))
        {
            SyncNavigationSelection();
            UpdateSectionChrome();
        }
    }

    private void SyncNavigationSelection()
    {
        _syncingNavigation = true;
        try
        {
            Navigation.SelectedItem = GetNavigationItem(_viewModel.CurrentSection);
        }
        finally
        {
            _syncingNavigation = false;
        }
    }

    private NavigationViewItem GetNavigationItem(string section) => section switch
    {
        "Clipboard" => ClipboardNavigationItem,
        "Pinned" => PinnedNavigationItem,
        "Settings" => SettingsNavigationItem,
        _ => SpaceNavigationItem,
    };

    private void SyncSettingsControls()
    {
        _syncingSettings = true;
        try
        {
            PauseToggle.IsOn = _viewModel.IsClipboardPaused;
            CaptureImagesToggle.IsOn = _viewModel.CaptureImages;
            CaptureFilesToggle.IsOn = _viewModel.CaptureFiles;
            CaptureFoldersToggle.IsOn = _viewModel.CaptureFolders;
            DeviceHandoffToggle.IsOn = _viewModel.EnableDeviceHandoff;
            CrossDeviceClipboardToggle.IsOn = _viewModel.EnableCrossDeviceClipboard;
            NearbySharingToggle.IsOn = _viewModel.EnableNearbySharing;
            InternetSharingToggle.IsOn = _viewModel.EnableInternetSharing;
            SelectComboItem(DefaultClipboardSyncModeCombo, _viewModel.DefaultClipboardSyncMode.ToString());
            StartWithWindowsToggle.IsOn = _viewModel.StartWithWindows;
            AutoCheckUpdatesToggle.IsOn = _viewModel.AutoCheckForUpdates;
            AutoDownloadUpdatesToggle.IsOn = _viewModel.AutoDownloadUpdates;
            AutoInstallUpdatesToggle.IsOn = _viewModel.AutoInstallUpdates;
            MaxImageMegabytesNumber.Value = _viewModel.MaxImageMegabytes;
            MaxImageMegapixelsNumber.Value = _viewModel.MaxImageMegapixels;
            MaxClipboardFileMegabytesNumber.Value = _viewModel.MaxClipboardFileMegabytes;
            MaxClipboardFileTotalMegabytesNumber.Value = _viewModel.MaxClipboardFileTotalMegabytes;
            MaxClipboardFileItemsNumber.Value = _viewModel.MaxClipboardFileItems;
            RetentionDaysNumber.Value = _viewModel.RetentionDays;
            RetentionCountNumber.Value = _viewModel.RetentionItemCount;
            SelectComboItem(ThemeCombo, _viewModel.Theme.ToString());
            SelectComboItem(LanguageCombo, _viewModel.Language.ToString());
            SelectComboItem(CloseBehaviorCombo, _viewModel.CloseBehavior.ToString());
            SelectComboItem(OverlayMotionCombo, _viewModel.OverlayMotion.ToString());
            SelectComboItem(OverlayMonitorCombo, _viewModel.OverlayMonitor.ToString());
            SelectComboItem(FileDragWakeModeCombo, _viewModel.FileDragWakeMode.ToString());
            IReadOnlyList<OverlayMonitorChoice> availableOverlayMonitors;
            try
            {
                availableOverlayMonitors = _viewModel.AvailableOverlayMonitors;
            }
            catch (InvalidOperationException exception) when (
                exception.Message.Contains("active display", StringComparison.OrdinalIgnoreCase))
            {
                // Display enumeration can briefly return no monitors during a headless or display
                // reconnecting session. Keep the settings page usable; the overlay service will
                // retry on its next topology refresh.
                availableOverlayMonitors = Array.Empty<OverlayMonitorChoice>();
            }

            OverlayPlacementMonitorCombo.ItemsSource = availableOverlayMonitors;
            OverlayPlacementMonitorCombo.SelectedIndex = Math.Max(0, OverlayPlacementMonitorCombo.SelectedIndex);
            QuickPanelHotkeyText.Text = _viewModel.QuickPanelHotkey;
            SmartDragExclusionsText.Text = _viewModel.SmartDragExcludedProcessesText;
            SyncPlacementCoordinates();
            SelectComboItem(UpdateChannelCombo, _viewModel.UpdateChannel.ToString());
            UpdateDeviceStatus();
        }
        finally
        {
            _syncingSettings = false;
        }
    }

    private void SyncPlacementCoordinates()
    {
        if (OverlayPlacementMonitorCombo.SelectedValue is not string monitorId)
        {
            return;
        }
        var placement = _viewModel.GetOverlayPlacement(monitorId);
        var canPersist = _viewModel.CanPersistOverlayPlacement(monitorId);
        SelectComboItem(OverlayPlacementModeCombo, placement.Mode.ToString());
        OverlayPlacementCustomItem.IsEnabled = canPersist;
        OverlayPlacementXNumber.Value = placement.X;
        OverlayPlacementYNumber.Value = placement.Y;
        OverlayPlacementXNumber.IsEnabled = canPersist;
        OverlayPlacementYNumber.IsEnabled = canPersist;
        AdjustIslandPlacementButton.IsEnabled = canPersist;
        ApplyIslandPlacementButton.IsEnabled = canPersist;
        OverlayPlacementPersistenceWarningText.Visibility = canPersist
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void UpdateSectionChrome()
    {
        var section = _viewModel.CurrentSection;
        AddButton.Visibility = section == "Space" ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.Visibility = section == "Settings" ? Visibility.Collapsed : Visibility.Visible;
        ClipboardStatusText.Visibility = section == "Clipboard" ? Visibility.Visible : Visibility.Collapsed;
        switch (section)
        {
            case "Clipboard":
                EmptyTitle.Text = _strings.Get("EmptyClipboardTitle");
                EmptyDescription.Text = _strings.Get("EmptyClipboardDescription");
                break;
            case "Pinned":
                EmptyTitle.Text = _strings.Get("EmptyPinnedTitle");
                EmptyDescription.Text = _strings.Get("EmptyPinnedDescription");
                break;
            default:
                EmptyTitle.Text = _strings.Get("EmptySpaceTitle");
                EmptyDescription.Text = _strings.Get("EmptySpaceDescription");
                break;
        }
    }

    private void UpdateDeviceStatus()
    {
        if (!_viewModel.EnableDeviceHandoff)
        {
            DeviceStatusText.Text = _strings.Get("DevicesDisabledContent");
        }
        else if (_deviceHandoff.UnavailableReason is { Length: > 0 } reason)
        {
            DeviceStatusText.Text = _strings.Format("DeviceServiceUnavailable", reason);
        }
        else if (_deviceHandoff.FirewallStatus is { } firewall)
        {
            DeviceStatusText.Text = firewall.CanReceive
                ? _strings.Get("DeviceServiceReady")
                : _strings.Format("DeviceServiceBlocked", firewall.Reason ?? string.Empty);
        }
        else
        {
            DeviceStatusText.Text = _strings.Get("DeviceServiceStarting");
        }
    }

    private void SetDropHint(DragEventArgs args, bool visible)
    {
        var acceptsItems = args.DataView.Contains(StandardDataFormats.StorageItems) ||
                           args.DataView.Contains(StandardDataFormats.Text) ||
                           args.DataView.Contains(StandardDataFormats.WebLink);
        args.AcceptedOperation = acceptsItems ? DataPackageOperation.Copy : DataPackageOperation.None;
        DropHint.Visibility = visible && acceptsItems ? Visibility.Visible : Visibility.Collapsed;
        if (acceptsItems)
        {
            args.DragUIOverride.Caption = _strings.Get("DragCaptionAddToDropSpace");
            args.DragUIOverride.IsCaptionVisible = true;
        }
    }

    private static ItemCardViewModel? GetCard(object sender) => sender switch
    {
        FrameworkElement { Tag: ItemCardViewModel card } => card,
        FrameworkElement { DataContext: ItemCardViewModel card } => card,
        _ => null,
    };

    private static void SelectComboItem(ComboBox comboBox, string tag)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, tag, StringComparison.Ordinal));
    }

    private async Task<bool> ShowConfirmationAsync(string title, string content, string primaryText)
    {
        await _dialogGate.WaitAsync();
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = title,
                Content = content,
                PrimaryButtonText = primaryText,
                CloseButtonText = _strings.Get("CommonCancel"),
                DefaultButton = ContentDialogButton.Close,
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    private async Task ShowMessageAsync(string title, string content)
    {
        await _dialogGate.WaitAsync();
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = title,
                Content = content,
                CloseButtonText = _strings.Get("CommonAcknowledge"),
            };
            await dialog.ShowAsync();
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    private async Task RunAsync(Func<Task> operation)
    {
        try
        {
            await operation();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception)
        {
            await ShowMessageAsync(
                _strings.Get("OperationIncompleteTitle"),
                _strings.Get("OperationIncompleteContent"));
        }
    }

    public void VerifyLocalizedResources()
    {
        VerifyResourceValue(SpaceNavigationItem.Content, "NavSpace.Content");
        VerifyResourceValue(ClipboardNavigationItem.Content, "NavClipboard.Content");
        VerifyResourceValue(PinnedNavigationItem.Content, "NavPinned.Content");
        VerifyResourceValue(SettingsNavigationItem.Content, "NavSettings.Content");
        VerifyResourceValue(SearchBox.PlaceholderText, "SearchBox.PlaceholderText");
        VerifyResourceValue(AddButton.Content, "AddButton.Content");
    }

    private void VerifyResourceValue(object? actual, string key)
    {
        var expected = _strings.Get(key);
        if (!string.Equals(actual as string, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Localized XAML resource '{key}' did not resolve.");
        }
    }
}
