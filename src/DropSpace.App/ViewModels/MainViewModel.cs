using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DropSpace.App.Services;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace DropSpace.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly IItemRepository _repository;
    private readonly ISettingsService _settingsService;
    private readonly IPayloadStore _payloadStore;
    private readonly IFileReferenceService _fileReferences;
    private readonly ILocalStorageMetrics _storageMetrics;
    private readonly ClipboardCaptureService _clipboard;
    private readonly ShellActionService _shell;
    private readonly ThumbnailService _thumbnails;
    private readonly DragStorageItemService _dragStorageItems;
    private readonly DispatcherQueue _dispatcher;
    private readonly ILogger<MainViewModel> _logger;
    private CancellationTokenSource? _queryCancellation;
    private string _currentSection = "Space";
    private string _searchText = string.Empty;
    private string _pageTitle = "Space";
    private string _pageDescription = "把暂时无处归类的文件与文件夹拖到这里。";
    private string _statusMessage = string.Empty;
    private string _clipboardStatusText = "正在启动…";
    private bool _isBusy;
    private bool _isEmpty = true;
    private bool _isSettingsVisible;
    private int _itemCount;
    private ItemCardViewModel? _selectedItem;
    private AppSettings _settings = new();
    private string _storageSummary = "正在计算…";
    private bool _disposed;

    public MainViewModel(
        IItemRepository repository,
        ISettingsService settingsService,
        IPayloadStore payloadStore,
        IFileReferenceService fileReferences,
        ILocalStorageMetrics storageMetrics,
        ClipboardCaptureService clipboard,
        ShellActionService shell,
        ThumbnailService thumbnails,
        DragStorageItemService dragStorageItems,
        DispatcherQueue dispatcher,
        ILogger<MainViewModel> logger)
    {
        _repository = repository;
        _settingsService = settingsService;
        _payloadStore = payloadStore;
        _fileReferences = fileReferences;
        _storageMetrics = storageMetrics;
        _clipboard = clipboard;
        _shell = shell;
        _thumbnails = thumbnails;
        _dragStorageItems = dragStorageItems;
        _dispatcher = dispatcher;
        _logger = logger;
        _clipboard.ItemCaptured += OnItemCaptured;
        _clipboard.StatusChanged += OnClipboardStatusChanged;
    }

    public ObservableCollection<ItemCardViewModel> Items { get; } = [];

    public string CurrentSection
    {
        get => _currentSection;
        private set => SetProperty(ref _currentSection, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                _ = DebouncedReloadAsync();
            }
        }
    }

    public string PageTitle
    {
        get => _pageTitle;
        private set => SetProperty(ref _pageTitle, value);
    }

    public string PageDescription
    {
        get => _pageDescription;
        private set => SetProperty(ref _pageDescription, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set
        {
            if (SetProperty(ref _statusMessage, value))
            {
                OnPropertyChanged(nameof(HasStatusMessage));
            }
        }
    }

    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);

    public string ClipboardStatusText
    {
        get => _clipboardStatusText;
        private set => SetProperty(ref _clipboardStatusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool IsEmpty
    {
        get => _isEmpty;
        private set => SetProperty(ref _isEmpty, value);
    }

    public bool IsSettingsVisible
    {
        get => _isSettingsVisible;
        private set
        {
            if (SetProperty(ref _isSettingsVisible, value))
            {
                OnPropertyChanged(nameof(IsCollectionVisible));
            }
        }
    }

    public bool IsCollectionVisible => !IsSettingsVisible;

    public int ItemCount
    {
        get => _itemCount;
        private set
        {
            if (SetProperty(ref _itemCount, value))
            {
                OnPropertyChanged(nameof(ItemCountText));
            }
        }
    }

    public string ItemCountText => $"{ItemCount} 项";

    public ItemCardViewModel? SelectedItem
    {
        get => _selectedItem;
        set => SetProperty(ref _selectedItem, value);
    }

    public AppSettings Settings
    {
        get => _settings;
        private set
        {
            if (SetProperty(ref _settings, value))
            {
                OnPropertyChanged(nameof(IsClipboardPaused));
                OnPropertyChanged(nameof(CaptureImages));
                OnPropertyChanged(nameof(RetentionDays));
                OnPropertyChanged(nameof(RetentionItemCount));
                OnPropertyChanged(nameof(Theme));
                OnPropertyChanged(nameof(CloseBehavior));
            }
        }
    }

    public bool IsClipboardPaused => Settings.ClipboardPaused;

    public bool CaptureImages => Settings.CaptureImages;

    public int RetentionDays => Settings.RetentionDays;

    public int RetentionItemCount => Settings.RetentionItemCount;

    public ThemePreference Theme => Settings.Theme;

    public CloseBehavior CloseBehavior => Settings.CloseBehavior;

    public string StoragePath => _storageMetrics.RootPath;

    public string StorageSummary
    {
        get => _storageSummary;
        private set
        {
            if (SetProperty(ref _storageSummary, value))
            {
                OnPropertyChanged(nameof(StorageSummaryText));
            }
        }
    }

    public string StorageSummaryText => $"当前占用：{StorageSummary}";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Settings = await _settingsService.LoadAsync(cancellationToken);
        await _repository.InitializeAsync(cancellationToken);
        await _clipboard.InitializeAsync(cancellationToken);
        ClipboardStatusText = FormatClipboardStatus(_clipboard.Status);
        _ = RefreshStorageSummaryAsync(cancellationToken);
        await NavigateAsync(Settings.LaunchPage, cancellationToken);
    }

    public async Task NavigateAsync(string section, CancellationToken cancellationToken = default)
    {
        CurrentSection = section;
        IsSettingsVisible = string.Equals(section, "Settings", StringComparison.Ordinal);
        switch (section)
        {
            case "Clipboard":
                PageTitle = "Clipboard";
                PageDescription = "自动记录当前进程运行期间复制的文本和图片。";
                break;
            case "Pinned":
                PageTitle = "Pinned";
                PageDescription = "来自 Space 和 Clipboard 的固定项目，不会因保留期限自动清理。";
                break;
            case "Settings":
                PageTitle = "Settings";
                PageDescription = "本地存储、隐私、外观和窗口行为。";
                Items.Clear();
                ItemCount = 0;
                IsEmpty = true;
                return;
            default:
                CurrentSection = "Space";
                PageTitle = "Space";
                PageDescription = "把暂时无处归类的文件与文件夹拖到这里。";
                break;
        }

        await ReloadAsync(cancellationToken);
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        try
        {
            var hasGlobalSearch = !string.IsNullOrWhiteSpace(SearchText);
            var query = hasGlobalSearch
                ? new ItemQuery(Search: SearchText, Limit: 500)
                : CurrentSection switch
                {
                    "Clipboard" => new ItemQuery(Source: ItemSource.Clipboard, Limit: 500),
                    "Pinned" => new ItemQuery(PinnedOnly: true, Limit: 500),
                    _ => new ItemQuery(Source: ItemSource.Space, Limit: 500),
                };

            var items = await _repository.QueryAsync(query, cancellationToken);
            Items.Clear();
            foreach (var item in items)
            {
                var card = new ItemCardViewModel(item);
                Items.Add(card);
                _ = LoadThumbnailSafelyAsync(card, cancellationToken);
            }

            ItemCount = Items.Count;
            IsEmpty = Items.Count == 0;
            StatusMessage = hasGlobalSearch && Items.Count == 0 ? "没有找到匹配项目。" : string.Empty;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<int> AddPathsAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var accepted = 0;
        var rejected = 0;
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var candidate = await _fileReferences.InspectAsync(path, cancellationToken);
                await _repository.AddFileAsync(candidate, cancellationToken);
                accepted++;
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                rejected++;
                _logger.LogWarning(exception, "A dropped file reference was rejected.");
            }
        }

        StatusMessage = rejected == 0 ? $"已添加 {accepted} 项。" : $"已添加 {accepted} 项，另有 {rejected} 项无法加入。";
        await ReloadAsync(cancellationToken);
        return accepted;
    }

    public async Task TogglePinAsync(ItemCardViewModel card, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(card);
        await _repository.SetPinnedAsync(card.Id, !card.IsPinned, cancellationToken);
        var refreshed = await _repository.GetAsync(card.Id, cancellationToken);
        if (refreshed is not null)
        {
            card.Update(refreshed);
        }

        if (CurrentSection == "Pinned" && !card.IsPinned)
        {
            Items.Remove(card);
            ItemCount = Items.Count;
            IsEmpty = Items.Count == 0;
        }
    }

    public async Task RemoveAsync(ItemCardViewModel card, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(card);
        var payloadPath = await _repository.RemoveAsync(card.Id, cancellationToken);
        if (payloadPath is not null)
        {
            try
            {
                await _payloadStore.DeleteAsync(payloadPath, cancellationToken);
            }
            catch (IOException exception)
            {
                _logger.LogWarning(exception, "Deferred payload cleanup failed after record removal.");
            }
        }

        Items.Remove(card);
        ItemCount = Items.Count;
        IsEmpty = Items.Count == 0;
        StatusMessage = "已从 DropSpace 移除；原始文件未被修改。";
    }

    public async Task<bool> OpenAsync(ItemCardViewModel card, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(card);
        var item = await RefreshFileAvailabilityAsync(card, cancellationToken);
        var opened = await _shell.OpenAsync(item, cancellationToken);
        if (opened)
        {
            await _repository.MarkUsedAsync(item.Id, cancellationToken);
        }
        else
        {
            StatusMessage = item.File is null ? "此项目无法打开。" : "文件当前不可用，请重新定位。";
        }

        return opened;
    }

    public async Task CopyAsync(ItemCardViewModel card, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(card);
        await _shell.CopyAsync(card.Item, cancellationToken);
        await _repository.MarkUsedAsync(card.Id, cancellationToken);
        StatusMessage = "已复制。";
    }

    public async Task ShowInFolderAsync(ItemCardViewModel card, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (!await _shell.ShowInFolderAsync(card.Item, cancellationToken))
        {
            StatusMessage = "无法在文件夹中显示此项目。";
        }
    }

    public async Task ReplaceFileReferenceAsync(
        ItemCardViewModel card,
        string replacementPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(card);
        var candidate = await _fileReferences.InspectAsync(replacementPath, cancellationToken);
        await _repository.ReplaceFileReferenceAsync(card.Id, candidate, cancellationToken);
        var refreshed = await _repository.GetAsync(card.Id, cancellationToken);
        if (refreshed is not null)
        {
            card.Update(refreshed);
            card.DragStorageItem = null;
            await LoadThumbnailSafelyAsync(card, cancellationToken);
        }

        StatusMessage = "文件引用已更新。";
    }

    public async Task SetClipboardPausedAsync(bool paused, CancellationToken cancellationToken = default)
    {
        if (paused)
        {
            await _clipboard.PauseAsync(cancellationToken);
        }
        else
        {
            await _clipboard.ResumeAsync(cancellationToken);
        }

        Settings = await _settingsService.LoadAsync(cancellationToken);
    }

    public async Task UpdateSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        settings.Validate();
        await _settingsService.SaveAsync(settings, cancellationToken);
        await _clipboard.UpdateSettingsAsync(settings, cancellationToken);
        Settings = settings;
        StatusMessage = "设置已保存。";
    }

    public async Task<ClearResult> ClearClipboardAsync(ClearRange range, CancellationToken cancellationToken = default)
    {
        var fromUtc = GetClearFromUtc(range);
        var result = await _repository.ClearClipboardAsync(fromUtc, includePinned: false, cancellationToken);
        foreach (var path in result.PayloadPaths)
        {
            try
            {
                await _payloadStore.DeleteAsync(path, cancellationToken);
            }
            catch (IOException exception)
            {
                _logger.LogWarning(exception, "Deferred payload cleanup failed after clear-history.");
            }
        }

        StatusMessage = $"已清除 {result.RemovedCount} 项；固定项目已保留。";
        if (CurrentSection is "Clipboard" or "Pinned")
        {
            await ReloadAsync(cancellationToken);
        }

        return result;
    }

    public async Task<int> GetClearPreviewCountAsync(ClearRange range, CancellationToken cancellationToken = default)
    {
        var fromUtc = GetClearFromUtc(range);
        var items = await _repository.QueryAsync(
            new ItemQuery(Source: ItemSource.Clipboard, Limit: 100_000),
            cancellationToken);
        return items.Count(item => !item.IsPinned && (fromUtc is null || item.CreatedAtUtc >= fromUtc));
    }

    public async Task ExportImageAsync(
        ItemCardViewModel card,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (card.Item.Kind != ItemKind.Image || card.Item.Payload is null)
        {
            throw new InvalidOperationException("Only clipboard images can be exported.");
        }

        await _payloadStore.ExportAsync(card.Item.Payload.RelativePath, destinationPath, cancellationToken);
        StatusMessage = "图片已导出。";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _clipboard.ItemCaptured -= OnItemCaptured;
        _clipboard.StatusChanged -= OnClipboardStatusChanged;
        _queryCancellation?.Cancel();
        _queryCancellation?.Dispose();
        _disposed = true;
    }

    private async Task<DropItem> RefreshFileAvailabilityAsync(ItemCardViewModel card, CancellationToken cancellationToken)
    {
        var item = card.Item;
        if (item.File is null)
        {
            return item;
        }

        var availability = await _fileReferences.CheckAvailabilityAsync(item.File, cancellationToken);
        if (availability.Status != item.Status ||
            !string.Equals(availability.Reason, item.File.AvailabilityReason, StringComparison.Ordinal))
        {
            await _repository.UpdateFileStatusAsync(
                item.Id,
                availability.Status,
                availability.Reason,
                cancellationToken);
            item = (await _repository.GetAsync(item.Id, cancellationToken))!;
            card.Update(item);
        }

        return item;
    }

    private async Task DebouncedReloadAsync()
    {
        _queryCancellation?.Cancel();
        _queryCancellation?.Dispose();
        _queryCancellation = new CancellationTokenSource();
        var token = _queryCancellation.Token;
        try
        {
            await Task.Delay(220, token);
            if (!IsSettingsVisible)
            {
                await ReloadAsync(token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Search refresh failed.");
            StatusMessage = "搜索暂时不可用。";
        }
    }

    private async Task LoadThumbnailSafelyAsync(ItemCardViewModel card, CancellationToken cancellationToken)
    {
        try
        {
            if (card.Item.File is not null)
            {
                await RefreshFileAvailabilityAsync(card, cancellationToken);
            }

            if (card.Item.File is not null && card.Item.Status == ItemStatus.Available)
            {
                card.DragStorageItem = await _dragStorageItems.ResolveAsync(card.Item, cancellationToken);
            }

            card.Thumbnail = await _thumbnails.LoadAsync(card.Item, cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.LogInformation(exception, "Thumbnail load failed for item {ItemId}.", card.Id);
        }
    }

    private void OnItemCaptured(object? sender, DropItem item)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (CurrentSection == "Clipboard" && string.IsNullOrWhiteSpace(SearchText))
            {
                var existing = Items.FirstOrDefault(card => card.Id == item.Id);
                if (existing is not null)
                {
                    existing.Update(item);
                    Items.Move(Items.IndexOf(existing), 0);
                }
                else
                {
                    var card = new ItemCardViewModel(item);
                    Items.Insert(0, card);
                    _ = LoadThumbnailSafelyAsync(card, CancellationToken.None);
                }

                ItemCount = Items.Count;
                IsEmpty = Items.Count == 0;
            }
        });
    }

    private void OnClipboardStatusChanged(object? sender, ClipboardCaptureStatus status)
    {
        _dispatcher.TryEnqueue(() =>
        {
            ClipboardStatusText = FormatClipboardStatus(status);
            if (!string.IsNullOrWhiteSpace(status.Message))
            {
                StatusMessage = status.Message;
            }
        });
    }

    private static string FormatClipboardStatus(ClipboardCaptureStatus status) => status.State switch
    {
        ClipboardRecordingState.Recording => $"正在记录 · 已捕获 {status.CapturedItems} 项",
        ClipboardRecordingState.Paused => "已暂停记录",
        ClipboardRecordingState.Error => "记录发生错误",
        _ => string.Empty,
    };

    private static DateTimeOffset? GetClearFromUtc(ClearRange range) => range switch
    {
        ClearRange.LastHour => DateTimeOffset.UtcNow.AddHours(-1),
        ClearRange.Today => new DateTimeOffset(
            DateTime.Today,
            TimeZoneInfo.Local.GetUtcOffset(DateTime.Today)).ToUniversalTime(),
        _ => null,
    };

    private async Task RefreshStorageSummaryAsync(CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await _storageMetrics.GetByteLengthAsync(cancellationToken);
            if (bytes is null)
            {
                StorageSummary = "暂时无法计算";
                return;
            }

            StorageSummary = bytes.Value switch
            {
                < 1024 => $"{bytes.Value} B",
                < 1024 * 1024 => $"{bytes.Value / 1024d:0.0} KB",
                < 1024L * 1024 * 1024 => $"{bytes.Value / (1024d * 1024):0.0} MB",
                _ => $"{bytes.Value / (1024d * 1024 * 1024):0.00} GB",
            };
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StorageSummary = "暂时无法计算";
        }
    }
}
