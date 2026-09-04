using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using DropSpace.App.Services;
using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Actions;
using DropSpace.Core.Compatibility;
using DropSpace.Core.Models;
using DropSpace.Core.Preview;
using DropSpace.Core.Transfer;
using DropSpace.Core.Updates;
using DropSpace.Infrastructure.Actions;
using DropSpace.Infrastructure.Network;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Input;
using Windows.UI.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
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
    private readonly IWindowsCapabilityService _capabilities;
    private readonly QuickPreviewService _previews;
    private readonly IItemActionRegistry _actions;
    private readonly QuickActionDialogService _quickActionDialog;
    private readonly ILogger<MainPage> _logger;
    private readonly DeviceHandoffService _deviceHandoff;
    private readonly CrossDeviceClipboardService _crossDeviceClipboard;
    private readonly DropLinkHost _dropLinkHost;
    private readonly ItemSharingService _sharing;
    private readonly ObservableCollection<DeviceDescriptor> _discoveredDevices = [];
    private readonly Dictionary<Guid, PairedPeer> _pairedPeers = [];
    private readonly Dictionary<QuickActionProfile, QuickActionSettingsControls> _quickActionControls = [];
    private readonly SemaphoreSlim _dialogGate = new(1, 1);
    private bool _syncingNavigation;
    private bool _syncingSettings;
    private bool _quickActionsSettingsBuilt;

    public MainPage(
        MainViewModel viewModel,
        nint windowHandle,
        IAppStringLocalizer strings,
        IWindowsCapabilityService capabilities,
        QuickPreviewService previews,
        IItemActionRegistry actions,
        QuickActionDialogService quickActionDialog,
        ILogger<MainPage> logger,
        DeviceHandoffService deviceHandoff,
        CrossDeviceClipboardService crossDeviceClipboard,
        DropLinkHost dropLinkHost,
        ItemSharingService sharing)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        _windowHandle = windowHandle;
        _strings = strings;
        _capabilities = capabilities;
        _previews = previews;
        _actions = actions;
        _quickActionDialog = quickActionDialog;
        _logger = logger;
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
        _dropLinkHost.PairingOffered += OnPairingOfferedAsync;
        _dropLinkHost.HandoffOffered += OnHandoffOfferedAsync;
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
        EnsureQuickActionsSettings();
        SyncNavigationSelection();
        SyncSettingsControls();
        UpdateSectionChrome();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _dropLinkHost.TransferOffered -= OnTransferOfferedAsync;
        _dropLinkHost.PairingOffered -= OnPairingOfferedAsync;
        _dropLinkHost.HandoffOffered -= OnHandoffOfferedAsync;
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

    private async void OnUndoClicked(object sender, RoutedEventArgs args) =>
        await RunAsync(() => _viewModel.UndoAsync());

    private async void OnPrimaryQuickActionClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: QuickActionButtonViewModel quickAction })
        {
            return;
        }

        await RunAsync(async () =>
        {
            if (XamlRoot is not { } xamlRoot)
            {
                _logger.LogWarning("Main-page quick action could not open its parameter surface because XamlRoot is unavailable.");
                return;
            }

            var selection = ResolveActionSelection(quickAction.Card);
            var context = await _quickActionDialog.RequestAsync(
                selection,
                quickAction.ActionId,
                xamlRoot,
                _windowHandle);
            if (context is null)
            {
                return;
            }

            var result = await _viewModel.ExecuteQuickActionAsync(quickAction.Card, quickAction.ActionId, context, selection);
            await ShowActionResultAsync(result);
        });
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
            await ApplySettingChangeAsync(
                settings => settings with { EnableDeviceHandoff = DeviceHandoffToggle.IsOn });
            UpdateDeviceStatus();
        }
    }

    private async void OnCrossDeviceClipboardToggled(object sender, RoutedEventArgs args)
    {
        if (!_syncingSettings)
        {
            await ApplySettingChangeAsync(
                settings => settings with { EnableCrossDeviceClipboard = CrossDeviceClipboardToggle.IsOn });
        }
    }

    private async void OnNearbySharingToggled(object sender, RoutedEventArgs args)
    {
        if (!_syncingSettings)
        {
            await ApplySettingChangeAsync(
                settings => settings with { EnableNearbySharing = NearbySharingToggle.IsOn });
        }
    }

    private async void OnInternetSharingToggled(object sender, RoutedEventArgs args)
    {
        if (!_syncingSettings)
        {
            await ApplySettingChangeAsync(
                settings => settings with { EnableInternetSharing = InternetSharingToggle.IsOn });
            UpdateDeviceStatus();
        }
    }

    private async void OnDefaultClipboardSyncModeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_syncingSettings && DefaultClipboardSyncModeCombo.SelectedItem is ComboBoxItem { Tag: string value } &&
            Enum.TryParse<ClipboardSyncMode>(value, out var mode))
        {
            await ApplySettingChangeAsync(
                settings => settings with { DefaultClipboardSyncMode = mode });
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
            var peer = await _deviceHandoff.PairAsync(descriptor, (sas, token) => ConfirmPairingSasAsync(descriptor.DisplayName, sas, token));
            _crossDeviceClipboard.ConfigurePeer(peer, descriptor.Endpoint, _viewModel.DefaultClipboardSyncMode);
            _pairedPeers[peer.Id] = new PairedPeer(peer, descriptor.Endpoint);
            DeviceStatusText.Text = _strings.Format("PairedDevice", peer.DisplayName);
        });
    }

    private Task<bool> OnPairingOfferedAsync(IncomingPairingOffer offer, CancellationToken cancellationToken) =>
        EnqueueDialogAsync(() => ConfirmPairingSasAsync(offer.RemoteHello.DisplayName, offer.Sas, cancellationToken), cancellationToken);

    private async Task<bool> ConfirmPairingSasAsync(string remoteName, int sas, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = _strings.Format("PairingSasTitleWithDevice", remoteName),
            Content = _strings.Format("PairingSasContent", sas.ToString("D6", System.Globalization.CultureInfo.InvariantCulture)),
            PrimaryButtonText = _strings.Get("PairingSasConfirm"),
            CloseButtonText = _strings.Get("CommonCancel"),
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task<bool> OnHandoffOfferedAsync(IncomingHandoffOffer offer, CancellationToken cancellationToken)
    {
        return await EnqueueDialogAsync(async () =>
        {
            var message = offer.Message;
            var preview = message.Utf8Payload.Length > 600 ? string.Concat(message.Utf8Payload[..600], "…") : message.Utf8Payload;
            var content = _strings.Format(
                "IncomingHandoffContent",
                message.SenderDisplayName,
                message.Kind == HandoffMessageKind.Url ? _strings.Get("HandoffUrlKind") : _strings.Get("HandoffTextKind"),
                preview,
                message.ByteLength);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = _strings.Get("IncomingHandoffTitle"),
                Content = new ScrollViewer
                {
                    MaxHeight = 420,
                    Content = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
                },
                PrimaryButtonText = _strings.Get("IncomingTransferAccept"),
                CloseButtonText = _strings.Get("IncomingTransferReject"),
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;

            // Explicit handoff is committed to Temporary Space only. It deliberately
            // does not call ClipboardCaptureService or mutate the Windows clipboard.
            await _viewModel.AddTextToSpaceAsync(message.Utf8Payload, "device-handoff", cancellationToken: cancellationToken);
            return true;
        }, cancellationToken);
    }

    private Task<T> EnqueueDialogAsync<T>(Func<Task<T>> callback, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
            {
                try { completion.TrySetResult(await callback().ConfigureAwait(true)); }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or OperationCanceledException)
                { completion.TrySetException(exception); }
            }))
        {
            completion.TrySetException(new InvalidOperationException("The DropSpace UI dispatcher is unavailable."));
        }

        return completion.Task.WaitAsync(cancellationToken);
    }

    private Task OnTransferOfferedAsync(IncomingTransferOffer offer, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var accepted = await ShowIncomingTransferDialogAsync(offer);
                    cancellationToken.ThrowIfCancellationRequested();
                    await _deviceHandoff.ApproveIncomingTransferAsync(offer.SessionId, accepted);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(
                        exception,
                        "Incoming DropLink transfer notification failed for session {SessionId}; attempting rejection.",
                        offer.SessionId);
                    try
                    {
                        await _deviceHandoff.ApproveIncomingTransferAsync(offer.SessionId, false);
                    }
                    catch (Exception rejectionException)
                    {
                        _logger.LogWarning(
                            rejectionException,
                            "Incoming DropLink transfer rejection could not be sent for session {SessionId}.",
                            offer.SessionId);
                    }
                }
                finally
                {
                    completion.TrySetResult();
                }
            }))
        {
            completion.TrySetException(new InvalidOperationException("The DropSpace UI dispatcher is unavailable."));
        }

        return completion.Task;
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
        if (descriptor.Kind == PreviewKind.Pdf && content is PdfPreviewHost)
        {
            try
            {
                await _previews.CacheSuccessfulAsync(card.Item, descriptor);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A cache failure must not hide a successfully rendered preview.
            }
        }
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
            try { return await CreateImageElementAsync(bytes, 640, 520); }
            catch (Exception exception) when (exception is InvalidDataException or IOException or COMException or ArgumentException)
            { return CreateUnavailablePreviewContent(descriptor); }
        }

        if (descriptor.Bytes is { Length: > 0 } pdfBytes && descriptor.Kind == PreviewKind.Pdf)
        {
            if (!_capabilities.IsAvailable(WindowsCapability.PdfPreview))
            {
                return CreateUnavailablePreviewContent();
            }

            try
            {
                return await CreatePdfPreviewHostAsync(pdfBytes, _strings);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or COMException)
            {
                // Keep the metadata fallback visible for a PDF that the platform renderer cannot decode.
            }
        }

        if (descriptor.Kind is PreviewKind.Audio or PreviewKind.Video &&
            _previews.ResolveSourcePath(item) is { } mediaPath && File.Exists(mediaPath))
        {
            if (!_capabilities.IsAvailable(WindowsCapability.MediaPreview))
            {
                return CreateUnavailablePreviewContent();
            }

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

        return CreateUnavailablePreviewContent(descriptor);
    }

    private TextBlock CreateUnavailablePreviewContent(PreviewDescriptor? descriptor = null)
    {
        return new TextBlock
        {
            Text = descriptor is null || descriptor.Metadata.Count == 0
                ? _strings.Get("PreviewUnavailable")
                : string.Join(Environment.NewLine, descriptor.Metadata.Select(pair => string.Concat(pair.Key, ": ", pair.Value))),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
    }

    private async Task<Image> CreateImageElementAsync(byte[] bytes, double maxWidth, double maxHeight)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        await ImageDecoderPreflight.ValidateAsync(stream, _viewModel.Settings.MaxImageBytes, _viewModel.Settings.MaxImagePixels);
        stream.Seek(0);
        var bitmap = new BitmapImage { DecodePixelWidth = checked((int)Math.Ceiling(maxWidth)) };
        await bitmap.SetSourceAsync(stream);
        return new Image
        {
            Source = bitmap,
            MaxWidth = maxWidth,
            MaxHeight = maxHeight,
            Stretch = Stretch.Uniform,
        };
    }

    private static async Task<PdfPreviewHost> CreatePdfPreviewHostAsync(byte[] bytes, IAppStringLocalizer strings)
    {
        var host = new PdfPreviewHost(bytes, strings);
        try
        {
            await host.InitializeAsync();
            return host;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    private static async Task<Image> RenderPdfPageAsync(
        PdfDocument document,
        int pageNumber,
        double maxWidth,
        double maxHeight,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (document.PageCount == 0) throw new InvalidDataException("The PDF has no pages.");
        using var page = document.GetPage((uint)Math.Clamp(pageNumber - 1, 0, (int)document.PageCount - 1));
        await page.PreparePageAsync();
        var size = page.Size;
        if (size.Width <= 0 || size.Height <= 0) throw new InvalidDataException("The PDF page dimensions are invalid.");
        const double maximumPixels = 4_000_000;
        var scale = Math.Min(maxWidth / size.Width, maxHeight / size.Height);
        scale = Math.Min(scale, Math.Sqrt(maximumPixels / (size.Width * size.Height)));
        var width = (uint)Math.Clamp(Math.Round(size.Width * scale), 1, 4096);
        var height = (uint)Math.Clamp(Math.Round(size.Height * scale), 1, 4096);
        using var output = new InMemoryRandomAccessStream();
        var options = new PdfPageRenderOptions
        {
            DestinationWidth = width,
            DestinationHeight = height,
            BitmapEncoderId = BitmapEncoder.PngEncoderId,
        };
        await page.RenderToStreamAsync(output, options);
        cancellationToken.ThrowIfCancellationRequested();
        if (output.Size is <= 0 or > 16L * 1024 * 1024) throw new InvalidDataException("The rendered PDF page exceeds the preview bound.");
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

    private static async Task<InMemoryRandomAccessStream> CreatePdfInputAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = new InMemoryRandomAccessStream();
        try
        {
            using var writer = new DataWriter(input.GetOutputStreamAt(0));
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
            input.Seek(0);
            return input;
        }
        catch
        {
            input.Dispose();
            throw;
        }
    }

    private sealed class PdfPreviewHost : Grid, IDisposable
    {
        private readonly byte[] _bytes;
        private readonly IAppStringLocalizer _strings;
        private readonly Image _image = new() { MaxWidth = 640, MaxHeight = 520, Stretch = Stretch.Uniform };
        private readonly Button _previous = new();
        private readonly Button _next = new();
        private readonly TextBlock _pageLabel = new();
        private readonly CancellationTokenSource _lifetime = new();
        private InMemoryRandomAccessStream? _input;
        private PdfDocument? _document;
        private CancellationTokenSource? _pageCancellation;
        private int _page = 1;
        private int _disposed;

        public PdfPreviewHost(byte[] bytes, IAppStringLocalizer strings)
        {
            _bytes = bytes;
            _strings = strings;
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            var navigation = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 8 };
            _previous.Content = _strings.Get("PdfPreviousPage");
            _next.Content = _strings.Get("PdfNextPage");
            _previous.Click += OnPreviousClicked;
            _next.Click += OnNextClicked;
            navigation.Children.Add(_previous);
            navigation.Children.Add(_pageLabel);
            navigation.Children.Add(_next);
            Children.Add(navigation);
            Grid.SetRow(navigation, 0);
            Children.Add(_image);
            Grid.SetRow(_image, 1);
        }

        public async Task InitializeAsync()
        {
            _input = await CreatePdfInputAsync(_bytes, _lifetime.Token);
            _document = await PdfDocument.LoadFromStreamAsync(_input);
            if (_document.PageCount == 0) throw new InvalidDataException("The PDF has no pages.");
            await LoadPageAsync(1, throwOnFailure: true);
        }

        private async void OnPreviousClicked(object sender, RoutedEventArgs args) => await NavigateAsync(_page - 1);

        private async void OnNextClicked(object sender, RoutedEventArgs args) => await NavigateAsync(_page + 1);

        private async Task NavigateAsync(int page)
        {
            if (_document is null || _document.PageCount == 0) return;
            await LoadPageAsync(Math.Clamp(page, 1, (int)_document.PageCount));
        }

        private async Task LoadPageAsync(int page, bool throwOnFailure = false)
        {
            if (_document is null) return;
            _pageCancellation?.Cancel();
            _pageCancellation?.Dispose();
            _pageCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var cancellationToken = _pageCancellation.Token;
            try
            {
                var image = await RenderPdfPageAsync(_document, page, 640, 520, cancellationToken);
                if (cancellationToken.IsCancellationRequested) return;
                _image.Source = image.Source;
                _page = page;
                _pageLabel.Text = _strings.Format("PdfPageIndicator", page, _document.PageCount);
                _previous.IsEnabled = page > 1;
                _next.IsEnabled = page < _document.PageCount;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or COMException)
            {
                if (throwOnFailure) throw;
                _pageLabel.Text = _strings.Get("PreviewUnavailable");
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _pageCancellation?.Cancel();
            _pageCancellation?.Dispose();
            _lifetime.Cancel();
            _lifetime.Dispose();
            _image.Source = null;
            // Windows.Data.Pdf.PdfDocument is a WinRT projection without a Close/Dispose
            // member in the target SDK; releasing the reference is its lifetime boundary.
            _document = null;
            _input?.Dispose();
            _input = null;
        }
    }

    private async Task<MediaPreviewHost> CreateMediaElementAsync(string path)
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
            return new MediaPreviewHost(element, player, _strings.Get("PreviewUnavailable"));
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
            if (XamlRoot is not { } xamlRoot)
            {
                _logger.LogWarning("Main-page More action could not open its parameter surface because XamlRoot is unavailable.");
                return;
            }

            var selection = ResolveActionSelection(card);
            var context = await _quickActionDialog.RequestAsync(
                selection,
                action,
                xamlRoot,
                _windowHandle);
            if (context is null)
            {
                return;
            }

            var result = await _viewModel.ExecuteQuickActionAsync(card, action, context, selection);
            await ShowActionResultAsync(result);
        });
    }

    private void OnMoreItemFlyoutOpened(object sender, object args)
    {
        if (sender is not MenuFlyout flyout) return;
        foreach (var menu in flyout.Items.OfType<MenuFlyoutItem>())
        {
            if (menu.CommandParameter is not string actionText || menu.Tag is not ItemCardViewModel card ||
                !Enum.TryParse<ItemActionId>(actionText, out var action)) continue;

            var available = action switch
            {
                ItemActionId.SendToDevice => _viewModel.EnableDeviceHandoff && _pairedPeers.Count > 0,
                ItemActionId.CreateNearbyLink => _viewModel.EnableNearbySharing,
                ItemActionId.CreateSecureInternetLink => _viewModel.EnableInternetSharing && _sharing.IsInternetConfigured,
                _ => _viewModel.EvaluateQuickActions(card, ResolveActionSelection(card)).More.Any(
                    capability => capability.Descriptor.Id == action),
            };
            menu.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        }
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
            if (item.Url?.NormalizedUrl is { } normalizedUrl)
            {
                var handoffResponse = await _deviceHandoff.SendTextOrUrlAsync(peer.Peer, peer.Endpoint, HandoffMessageKind.Url, normalizedUrl, item.Title);
                await ShowMessageAsync(
                    handoffResponse.Accepted ? _strings.Get("TransferSentTitle") : _strings.Get("TransferUnavailableTitle"),
                    handoffResponse.Accepted ? _strings.Get("TransferSentContent") : handoffResponse.ErrorCategory ?? _strings.Get("ActionUnavailable"));
                return;
            }

            if (item.Text?.InlineText is { } inlineText)
            {
                var handoffResponse = await _deviceHandoff.SendTextOrUrlAsync(peer.Peer, peer.Endpoint, HandoffMessageKind.Text, inlineText, item.Title);
                await ShowMessageAsync(
                    handoffResponse.Accepted ? _strings.Get("TransferSentTitle") : _strings.Get("TransferUnavailableTitle"),
                    handoffResponse.Accepted ? _strings.Get("TransferSentContent") : handoffResponse.ErrorCategory ?? _strings.Get("ActionUnavailable"));
                return;
            }

            var clipboardResponse = await _crossDeviceClipboard.SendManualAsync(peer.Peer, peer.Endpoint, item);
            await ShowMessageAsync(
                clipboardResponse.Accepted ? _strings.Get("TransferSentTitle") : _strings.Get("TransferUnavailableTitle"),
                clipboardResponse.Accepted ? _strings.Get("TransferSentContent") : clipboardResponse.ErrorCategory ?? _strings.Get("ActionUnavailable"));
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
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(descriptor.Url.ToString());
            Clipboard.SetContent(package);
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
                await new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = _strings.Get("OpenShareQr"),
                    Content = qr,
                    CloseButtonText = _strings.Get("CommonClose"),
                }.ShowAsync();
            }
            finally { qr.Source = null; }
        }
    }

    private sealed record PairedPeer(PeerDevice Peer, Uri Endpoint);

    private sealed class MediaPreviewHost : Grid, IDisposable
    {
        private readonly MediaPlayer _player;
        private int _disposed;
        private readonly string _fallback;

        public MediaPreviewHost(MediaPlayerElement element, MediaPlayer player, string fallback)
        {
            _player = player;
            _fallback = fallback;
            Children.Add(element);
            _player.MediaFailed += OnMediaFailed;
        }

        private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (Volatile.Read(ref _disposed) != 0) return;
                _player.Source = null;
                Children.Clear();
                Children.Add(new TextBlock { Text = _fallback, TextWrapping = TextWrapping.Wrap });
            });
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _player.MediaFailed -= OnMediaFailed;
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
            .Select(value => Path.GetFileNameWithoutExtension(value) ?? string.Empty)
            .Where(value => value.Length > 0)
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

    private async void OnUndoAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await RunAsync(() => _viewModel.UndoAsync());
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

    private void EnsureQuickActionsSettings()
    {
        if (_quickActionsSettingsBuilt)
        {
            return;
        }

        QuickActionsSettingsPanel.Children.Clear();
        QuickActionsSettingsPanel.Children.Add(new TextBlock
        {
            Text = _strings.Get("QuickActionsSection"),
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        });
        QuickActionsSettingsPanel.Children.Add(new TextBlock
        {
            Text = _strings.Get("QuickActionsDescription"),
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            TextWrapping = TextWrapping.Wrap,
        });

        foreach (var profile in Enum.GetValues<QuickActionProfile>())
        {
            var group = new StackPanel { Spacing = 6 };
            group.Children.Add(new TextBlock
            {
                Text = GetQuickActionProfileLabel(profile),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });

            var automaticToggle = new ToggleSwitch
            {
                Header = _strings.Get("QuickActionAutomatic"),
                Tag = profile,
            };
            AutomationProperties.SetName(automaticToggle, _strings.Get("QuickActionAutomatic"));
            automaticToggle.Toggled += OnQuickActionAutomaticToggled;
            group.Children.Add(automaticToggle);

            var slotsGrid = new Grid { ColumnSpacing = 8 };
            var slots = new ComboBox[QuickActionPreferencePolicy.MaximumPrimaryActions];
            for (var index = 0; index < slots.Length; index++)
            {
                slotsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var slot = new ComboBox
                {
                    Header = _strings.Format("QuickActionSlot", index + 1),
                    Tag = new QuickActionSlotContext(profile, index),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                slot.Items.Add(new ComboBoxItem
                {
                    Content = _strings.Get("QuickActionNone"),
                    Tag = string.Empty,
                });
                foreach (var action in _actions.Actions)
                {
                    slot.Items.Add(new ComboBoxItem
                    {
                        Content = _strings.Get(action.Descriptor.LabelResourceKey),
                        Tag = action.Descriptor.Id.ToString(),
                    });
                }

                slot.SelectionChanged += OnQuickActionSlotChanged;
                Grid.SetColumn(slot, index);
                slotsGrid.Children.Add(slot);
                slots[index] = slot;
            }

            group.Children.Add(slotsGrid);
            var resetButton = new Button
            {
                Content = _strings.Get("ResetQuickActions"),
                Tag = profile,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            resetButton.Click += OnResetQuickActionsClicked;
            group.Children.Add(resetButton);
            QuickActionsSettingsPanel.Children.Add(group);
            _quickActionControls[profile] = new QuickActionSettingsControls(automaticToggle, slots);
        }

        var resetAllButton = new Button
        {
            Content = _strings.Get("ResetAllQuickActions"),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        resetAllButton.Click += OnResetAllQuickActionsClicked;
        QuickActionsSettingsPanel.Children.Add(resetAllButton);
        _quickActionsSettingsBuilt = true;
    }

    private async void OnQuickActionAutomaticToggled(object sender, RoutedEventArgs args)
    {
        if (!_syncingSettings)
        {
            await SaveQuickActionSettingsAsync();
        }
    }

    private async void OnQuickActionSlotChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_syncingSettings || sender is not ComboBox changed ||
            changed.Tag is not QuickActionSlotContext context)
        {
            return;
        }

        var selected = GetSelectedQuickAction(changed);
        var controls = _quickActionControls[context.Profile];
        if (selected is { } selectedId && controls.Slots.Any(slot => !ReferenceEquals(slot, changed) && GetSelectedQuickAction(slot) == selectedId))
        {
            _syncingSettings = true;
            try
            {
                SelectQuickAction(changed, null);
            }
            finally
            {
                _syncingSettings = false;
            }

            return;
        }

        await SaveQuickActionSettingsAsync();
    }

    private async Task SaveQuickActionSettingsAsync()
    {
        await RunAsync(async () =>
        {
            var preferences = QuickActionPreferencePolicy.CreateAutomaticPreferences();
            foreach (var (profile, controls) in _quickActionControls)
            {
                if (controls.AutomaticToggle.IsOn)
                {
                    continue;
                }

                var slots = controls.Slots.Select(GetSelectedQuickAction).ToArray();
                preferences[profile] = new QuickActionPreference(false, slots[0], slots[1], slots[2]);
            }

            try
            {
                await _viewModel.UpdateSettingsAsync(_viewModel.Settings with
                {
                    QuickActionPreferences = preferences,
                });
            }
            catch
            {
                SyncSettingsControls();
                throw;
            }
        });
    }

    private async void OnResetQuickActionsClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: QuickActionProfile profile })
        {
            return;
        }

        await ApplySettingChangeAsync(settings => settings with
        {
            QuickActionPreferences = new QuickActionPreferenceCollection(
                settings.QuickActionPreferences)
            {
                [profile] = QuickActionPreference.Automatic,
            },
        });
    }

    private async void OnResetAllQuickActionsClicked(object sender, RoutedEventArgs args)
    {
        await ApplySettingChangeAsync(settings => settings with
        {
            QuickActionPreferences = QuickActionPreferencePolicy.CreateAutomaticPreferences(),
        });
    }

    private void SyncQuickActionsSettings()
    {
        EnsureQuickActionsSettings();
        foreach (var (profile, controls) in _quickActionControls)
        {
            var preference = _viewModel.Settings.QuickActionPreferences.TryGetValue(profile, out var configured)
                ? configured
                : QuickActionPreference.Automatic;
            controls.AutomaticToggle.IsOn = preference.IsAutomatic;
            var slots = preference.Slots;
            for (var index = 0; index < controls.Slots.Length; index++)
            {
                SelectQuickAction(controls.Slots[index], slots[index]);
                controls.Slots[index].IsEnabled = !preference.IsAutomatic;
            }
        }
    }

    private static ItemActionId? GetSelectedQuickAction(ComboBox comboBox) =>
        comboBox.SelectedItem is ComboBoxItem { Tag: string value } &&
        Enum.TryParse<ItemActionId>(value, out var action)
            ? action
            : null;

    private static void SelectQuickAction(ComboBox comboBox, ItemActionId? action)
    {
        var tag = action?.ToString() ?? string.Empty;
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, tag, StringComparison.Ordinal));
    }

    private string GetQuickActionProfileLabel(QuickActionProfile profile) => profile switch
    {
        QuickActionProfile.File => _strings.Get("QuickActionFiles"),
        QuickActionProfile.Image => _strings.Get("QuickActionImages"),
        QuickActionProfile.Text => _strings.Get("QuickActionText"),
        QuickActionProfile.Url => _strings.Get("QuickActionUrls"),
        _ => profile.ToString(),
    };

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
            SyncQuickActionsSettings();
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

    private ItemSelectionSnapshot ResolveActionSelection(ItemCardViewModel clickedCard)
    {
        ArgumentNullException.ThrowIfNull(clickedCard);
        return _viewModel.ResolveActionSelection(
            clickedCard,
            ItemsList.SelectedItems.OfType<ItemCardViewModel>());
    }

    private sealed record QuickActionSlotContext(QuickActionProfile Profile, int Index);

    private sealed record QuickActionSettingsControls(
        ToggleSwitch AutomaticToggle,
        ComboBox[] Slots);

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

    private Task ShowActionResultAsync(ItemActionResult result)
    {
        var messageKey = result.MessageResourceKey ?? "ActionUnavailable";
        var title = _strings.Get(messageKey);
        var content = result.OutputPaths.Count == 0
            ? _strings.Get(messageKey)
            : _strings.Format("ActionOutputSaved", string.Join(Environment.NewLine, result.OutputPaths));
        return ShowMessageAsync(title, content);
    }

    private async Task ApplySettingChangeAsync(
        Func<AppSettings, AppSettings> update,
        Action? rollback = null)
    {
        await RunAsync(async () =>
        {
            try
            {
                await _viewModel.UpdateSettingsAsync(update(_viewModel.Settings));
            }
            catch
            {
                rollback?.Invoke();
                throw;
            }
        });
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
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "A main-page operation failed.");
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
