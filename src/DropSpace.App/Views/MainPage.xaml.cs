using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using DropSpace.Core.Updates;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace DropSpace.App.Views;

public sealed partial class MainPage : Page
{
    private readonly MainViewModel _viewModel;
    private readonly nint _windowHandle;
    private readonly IAppStringLocalizer _strings;
    private readonly SemaphoreSlim _dialogGate = new(1, 1);
    private bool _syncingNavigation;
    private bool _syncingSettings;

    public MainPage(
        MainViewModel viewModel,
        nint windowHandle,
        IAppStringLocalizer strings)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        _windowHandle = windowHandle;
        _strings = strings;
        InitializeComponent();
        DataContext = viewModel;
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

    private void OnDragEnter(object sender, DragEventArgs args) => SetDropHint(args, true);

    private void OnDragLeave(object sender, DragEventArgs args) => DropHint.Visibility = Visibility.Collapsed;

    private void OnDragOver(object sender, DragEventArgs args) => SetDropHint(args, true);

    private async void OnDrop(object sender, DragEventArgs args)
    {
        DropHint.Visibility = Visibility.Collapsed;
        await RunAsync(async () =>
        {
            if (!args.DataView.Contains(StandardDataFormats.StorageItems))
            {
                return;
            }

            var storageItems = await args.DataView.GetStorageItemsAsync();
            await _viewModel.AddPathsAsync(
                storageItems.Where(item => !string.IsNullOrWhiteSpace(item.Path)).Select(item => item.Path));
        });
    }

    private void OnDragItemsStarting(object sender, DragItemsStartingEventArgs args)
    {
        var storageItems = args.Items
            .OfType<ItemCardViewModel>()
            .Select(card => card.DragStorageItem)
            .Where(item => item is not null)
            .Cast<IStorageItem>()
            .ToArray();
        if (storageItems.Length == 0)
        {
            args.Cancel = true;
            return;
        }

        args.Data.RequestedOperation = DataPackageOperation.Copy;
        args.Data.SetStorageItems(storageItems, readOnly: true);
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
            SelectComboItem(UpdateChannelCombo, _viewModel.UpdateChannel.ToString());
        }
        finally
        {
            _syncingSettings = false;
        }
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

    private void SetDropHint(DragEventArgs args, bool visible)
    {
        var acceptsItems = args.DataView.Contains(StandardDataFormats.StorageItems);
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
