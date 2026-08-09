using DropSpace.App.ViewModels;
using DropSpace.Core.Models;
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
    private readonly SemaphoreSlim _dialogGate = new(1, 1);
    private bool _syncingNavigation;
    private bool _syncingSettings;

    public MainPage(MainViewModel viewModel, nint windowHandle)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        _windowHandle = windowHandle;
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
                await ShowMessageAsync("无需清理", "所选时间范围内没有可清理的未固定项目。");
                return;
            }

            var rangeLabel = range switch
            {
                ClearRange.LastHour => "最近一小时",
                ClearRange.Today => "今天",
                _ => "全部历史",
            };
            var result = await ShowConfirmationAsync(
                $"清除{rangeLabel}？",
                $"将从 Clipboard 移除 {count} 项未固定记录。固定项目会保留，Space 中的原始文件不会受影响。",
                "清除");
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
                "从 DropSpace 移除？",
                card.IsFileReference
                    ? $"只会移除“{card.Title}”的引用，原始文件不会被移动或删除。"
                    : $"将移除“{card.Title}”及 DropSpace 保存的本地副本。",
                "移除");
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
            picker.FileTypeChoices.Add(extension == ".jpg" ? "JPEG 图片" : "PNG 图片", [extension]);
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

    private async void OnOpenDropTraySettingsClicked(object sender, RoutedEventArgs args)
    {
        await RunAsync(async () =>
        {
            if (!await _viewModel.OpenDropTraySettingsAsync())
            {
                await ShowMessageAsync(
                    "无法打开系统设置",
                    "请手动打开 Windows 设置 → 系统 → 多任务处理，查看 Drop Tray 选项。");
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
            await ShowMessageAsync("限制值无效", "单次文件总大小上限不能小于单个文件大小上限。");
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

    private async void OnCloseBehaviorChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_syncingSettings && CloseBehaviorCombo.SelectedItem is ComboBoxItem { Tag: string value } &&
            Enum.TryParse<CloseBehavior>(value, out var closeBehavior))
        {
            await RunAsync(() => _viewModel.UpdateSettingsAsync(
                _viewModel.Settings with { CloseBehavior = closeBehavior }));
        }
    }

    private async void OnOverlayDisplayModeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_syncingSettings && OverlayDisplayModeCombo.SelectedItem is ComboBoxItem { Tag: string value } &&
            Enum.TryParse<OverlayDisplayMode>(value, out var displayMode))
        {
            await RunAsync(() => _viewModel.UpdateSettingsAsync(
                _viewModel.Settings with { OverlayDisplayMode = displayMode }));
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
            MaxImageMegabytesNumber.Value = _viewModel.MaxImageMegabytes;
            MaxImageMegapixelsNumber.Value = _viewModel.MaxImageMegapixels;
            MaxClipboardFileMegabytesNumber.Value = _viewModel.MaxClipboardFileMegabytes;
            MaxClipboardFileTotalMegabytesNumber.Value = _viewModel.MaxClipboardFileTotalMegabytes;
            MaxClipboardFileItemsNumber.Value = _viewModel.MaxClipboardFileItems;
            RetentionDaysNumber.Value = _viewModel.RetentionDays;
            RetentionCountNumber.Value = _viewModel.RetentionItemCount;
            SelectComboItem(ThemeCombo, _viewModel.Theme.ToString());
            SelectComboItem(CloseBehaviorCombo, _viewModel.CloseBehavior.ToString());
            SelectComboItem(OverlayDisplayModeCombo, _viewModel.OverlayDisplayMode.ToString());
            SelectComboItem(OverlayMotionCombo, _viewModel.OverlayMotion.ToString());
            SelectComboItem(OverlayMonitorCombo, _viewModel.OverlayMonitor.ToString());
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
                EmptyTitle.Text = "暂无剪贴板记录";
                EmptyDescription.Text = "复制文本、图片、文件或文件夹后，支持的内容会出现在这里。";
                break;
            case "Pinned":
                EmptyTitle.Text = "暂无固定项目";
                EmptyDescription.Text = "固定 Space 或 Clipboard 中的项目，它们就会集中显示在这里。";
                break;
            default:
                EmptyTitle.Text = "把文件或文件夹拖到这里";
                EmptyDescription.Text = "DropSpace 只保存引用，不会移动或删除原始文件。";
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
            args.DragUIOverride.Caption = "添加到 DropSpace";
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
                CloseButtonText = "取消",
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
                CloseButtonText = "知道了",
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
        catch (Exception exception)
        {
            await ShowMessageAsync("操作未完成", $"{exception.GetType().Name}：{exception.Message}");
        }
    }
}
