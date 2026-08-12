using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DropSpace.Core.Collections;
using DropSpace.App.Services;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using DropSpace.Core.Updates;
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
    private readonly IStartupRegistrationService _startupRegistration;
    private readonly WindowsShareIntegrationService _windowsShareIntegration;
    private readonly ClipboardCaptureService _clipboard;
    private readonly ShellActionService _shell;
    private readonly ThumbnailService _thumbnails;
    private readonly DragStorageItemService _dragStorageItems;
    private readonly IUpdateService _updates;
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
    private int _spaceItemCount;
    private long _spaceRevision;
    private ItemCardViewModel? _selectedItem;
    private AppSettings _settings = new();
    private string _storageSummary = "正在计算…";
    private UpdateStatusSnapshot _updateStatus;
    private bool _disposed;

    public MainViewModel(
        IItemRepository repository,
        ISettingsService settingsService,
        IPayloadStore payloadStore,
        IFileReferenceService fileReferences,
        ILocalStorageMetrics storageMetrics,
        IStartupRegistrationService startupRegistration,
        WindowsShareIntegrationService windowsShareIntegration,
        ClipboardCaptureService clipboard,
        ShellActionService shell,
        ThumbnailService thumbnails,
        DragStorageItemService dragStorageItems,
        IUpdateService updates,
        DispatcherQueue dispatcher,
        ILogger<MainViewModel> logger)
    {
        _repository = repository;
        _settingsService = settingsService;
        _payloadStore = payloadStore;
        _fileReferences = fileReferences;
        _storageMetrics = storageMetrics;
        _startupRegistration = startupRegistration;
        _windowsShareIntegration = windowsShareIntegration;
        _clipboard = clipboard;
        _shell = shell;
        _thumbnails = thumbnails;
        _dragStorageItems = dragStorageItems;
        _updates = updates;
        _updateStatus = updates.Status;
        _dispatcher = dispatcher;
        _logger = logger;
        _clipboard.ItemCaptured += OnItemCaptured;
        _clipboard.StatusChanged += OnClipboardStatusChanged;
        _updates.StatusChanged += OnUpdateStatusChanged;
    }

    public ObservableCollection<ItemCardViewModel> Items { get; } = [];

    public event EventHandler<SpaceProjectionChangedEventArgs>? SpaceProjectionChanged;

    /// <summary>
    /// The visual overlay registers this transaction hook. UI preferences are preflighted and
    /// successfully applied before settings.json is replaced, preventing a bad visual mode from
    /// becoming a persistent startup crash loop.
    /// </summary>
    public Func<AppSettings, CancellationToken, Task>? UiSettingsPreflightAsync { get; set; }

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

    public int SpaceItemCount
    {
        get => _spaceItemCount;
        private set => SetProperty(ref _spaceItemCount, value);
    }

    public long SpaceRevision => Interlocked.Read(ref _spaceRevision);

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
                OnPropertyChanged(nameof(CaptureFiles));
                OnPropertyChanged(nameof(CaptureFolders));
                OnPropertyChanged(nameof(StartWithWindows));
                OnPropertyChanged(nameof(MaxImageMegabytes));
                OnPropertyChanged(nameof(MaxImageMegapixels));
                OnPropertyChanged(nameof(MaxClipboardFileMegabytes));
                OnPropertyChanged(nameof(MaxClipboardFileTotalMegabytes));
                OnPropertyChanged(nameof(MaxClipboardFileItems));
                OnPropertyChanged(nameof(RetentionDays));
                OnPropertyChanged(nameof(RetentionItemCount));
                OnPropertyChanged(nameof(Theme));
                OnPropertyChanged(nameof(CloseBehavior));
                OnPropertyChanged(nameof(OverlayDisplayMode));
                OnPropertyChanged(nameof(OverlayMotion));
                OnPropertyChanged(nameof(OverlayMonitor));
                OnPropertyChanged(nameof(FileDragWakeMode));
                OnPropertyChanged(nameof(AutoCheckForUpdates));
                OnPropertyChanged(nameof(AutoDownloadUpdates));
                OnPropertyChanged(nameof(AutoInstallUpdates));
                OnPropertyChanged(nameof(UpdateChannel));
                OnPropertyChanged(nameof(LastUpdateCheckText));
                OnPropertyChanged(nameof(LastUpdateCheckDisplayText));
            }
        }
    }

    public bool IsClipboardPaused => Settings.ClipboardPaused;

    public bool CaptureImages => Settings.CaptureImages;

    public bool CaptureFiles => Settings.CaptureFiles;

    public bool CaptureFolders => Settings.CaptureFolders;

    public bool StartWithWindows => Settings.StartWithWindows;

    public double MaxImageMegabytes => Settings.MaxImageBytes / (1024d * 1024);

    public double MaxImageMegapixels => Settings.MaxImagePixels / 1_000_000d;

    public double MaxClipboardFileMegabytes => Settings.MaxClipboardFileBytes / (1024d * 1024);

    public double MaxClipboardFileTotalMegabytes => Settings.MaxClipboardFileTotalBytes / (1024d * 1024);

    public int MaxClipboardFileItems => Settings.MaxClipboardFileItems;

    public int RetentionDays => Settings.RetentionDays;

    public int RetentionItemCount => Settings.RetentionItemCount;

    public ThemePreference Theme => Settings.Theme;

    public CloseBehavior CloseBehavior => Settings.CloseBehavior;

    public OverlayDisplayMode OverlayDisplayMode => Settings.OverlayDisplayMode;

    public OverlayMotionPreference OverlayMotion => Settings.OverlayMotion;

    public OverlayMonitorPreference OverlayMonitor => Settings.OverlayMonitor;

    public FileDragWakeMode FileDragWakeMode => Settings.FileDragWakeMode;

    public bool AutoCheckForUpdates => Settings.AutoCheckForUpdates;

    public bool AutoDownloadUpdates => Settings.AutoDownloadUpdates;

    public bool AutoInstallUpdates => Settings.AutoInstallUpdates;

    public UpdateChannel UpdateChannel => Settings.UpdateChannel;

    public string CurrentVersionText => _updates.CurrentVersion.ToString();

    public string CurrentVersionDisplayText => $"DropSpace {CurrentVersionText}";

    public UpdateStatusSnapshot UpdateStatus
    {
        get => _updateStatus;
        private set
        {
            if (SetProperty(ref _updateStatus, value))
            {
                OnPropertyChanged(nameof(UpdateStatusText));
                OnPropertyChanged(nameof(UpdateProgressText));
                OnPropertyChanged(nameof(CanCheckForUpdates));
                OnPropertyChanged(nameof(CanDownloadUpdate));
                OnPropertyChanged(nameof(CanInstallUpdate));
                OnPropertyChanged(nameof(CanOpenUpdateLocation));
                OnPropertyChanged(nameof(CanViewReleaseNotes));
                OnPropertyChanged(nameof(TrustedAutoInstallAvailable));
                OnPropertyChanged(nameof(DeploymentModeText));
                OnPropertyChanged(nameof(LastUpdateCheckText));
                OnPropertyChanged(nameof(LastUpdateCheckDisplayText));
            }
        }
    }

    public string UpdateStatusText => UpdateStatus.Message;

    public string UpdateProgressText => UpdateStatus.Progress is { } progress
        ? $"{progress.Percentage:0}% · {FormatBytes(progress.BytesReceived)} / {FormatBytes(progress.TotalBytes)}"
        : string.Empty;

    public bool CanCheckForUpdates => UpdateStatus.State is not UpdateState.Checking and not UpdateState.Downloading and not UpdateState.Installing;

    public bool CanDownloadUpdate => UpdateStatus.Candidate is not null &&
        UpdateStatus.DeploymentMode != DeploymentMode.Packaged &&
        UpdateStatus.State is UpdateState.UpdateAvailable or UpdateState.Failed;

    public bool CanInstallUpdate => UpdateStatus.State == UpdateState.ReadyToInstall &&
        UpdateStatus.DeploymentMode == DeploymentMode.Installer;

    public bool CanOpenUpdateLocation => UpdateStatus.State == UpdateState.ReadyToInstall &&
        UpdateStatus.DeploymentMode == DeploymentMode.Portable;

    public bool CanViewReleaseNotes => UpdateStatus.Candidate is not null;

    public bool TrustedAutoInstallAvailable => UpdateStatus.TrustedAutoInstallAvailable;

    public string DeploymentModeText => UpdateStatus.DeploymentMode switch
    {
        DeploymentMode.Installer => "安装版",
        DeploymentMode.Portable => "便携版（仅下载并验证，不会自动安装）",
        DeploymentMode.Packaged => "Windows 包（由 Windows 管理更新）",
        _ => "未知",
    };

    public string LastUpdateCheckText => Settings.LastUpdateCheckUtc is { } value
        ? value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
        : "尚未检查";

    public string LastUpdateCheckDisplayText => $"上次检查：{LastUpdateCheckText}";

    public bool HasWindowsShareIdentity => _windowsShareIntegration.HasPackageIdentity;

    public string WindowsShareIntegrationStatus => _windowsShareIntegration.StatusText;

    public Task<bool> OpenDropTraySettingsAsync() =>
        _windowsShareIntegration.OpenDropTraySettingsAsync();

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
        await _startupRegistration.SetEnabledAsync(Settings.StartWithWindows, cancellationToken);
        await _repository.InitializeAsync(cancellationToken);
        await RefreshSpaceItemCountAsync(cancellationToken);
        await _clipboard.InitializeAsync(cancellationToken);
        ClipboardStatusText = FormatClipboardStatus(_clipboard.Status);
        UpdateStatus = await _updates.RecoverPendingAsync(cancellationToken);
        _ = RefreshStorageSummaryAsync(cancellationToken);
        await NavigateAsync(Settings.LaunchPage, cancellationToken);
    }

    public async Task CheckForUpdatesAtStartupAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var status = await _updates.CheckAtStartupAsync(Settings, cancellationToken);
            await PersistLastUpdateCheckAsync(status, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "The process-lifetime automatic update check failed safely.");
        }
    }

    public async Task CheckForUpdatesManuallyAsync(CancellationToken cancellationToken = default)
    {
        var status = await _updates.CheckManuallyAsync(Settings, cancellationToken);
        await PersistLastUpdateCheckAsync(status, cancellationToken);
    }

    public Task DownloadUpdateAsync(CancellationToken cancellationToken = default) =>
        _updates.DownloadAsync(cancellationToken);

    public Task InstallUpdateAsync(CancellationToken cancellationToken = default) =>
        _updates.InstallAsync(unattended: false, cancellationToken);

    public Task<bool> OpenUpdateLocationAsync(CancellationToken cancellationToken = default)
    {
        var directory = UpdateStatus.Download?.FilePath is { } path ? Path.GetDirectoryName(path) : null;
        return directory is null
            ? Task.FromResult(false)
            : _shell.OpenFolderAsync(directory, cancellationToken);
    }

    public Task<bool> OpenUpdateReleaseNotesAsync(CancellationToken cancellationToken = default) =>
        UpdateStatus.Candidate is { } candidate
            ? _shell.OpenHttpsAsync(candidate.Release.HtmlUri, cancellationToken)
            : Task.FromResult(false);

    public void ShowUpdatedVersion(ReleaseVersion version) =>
        StatusMessage = $"DropSpace 已更新至 {version}。";

    public async Task NavigateAsync(string section, CancellationToken cancellationToken = default)
    {
        CurrentSection = section;
        IsSettingsVisible = string.Equals(section, "Settings", StringComparison.Ordinal);
        switch (section)
        {
            case "Clipboard":
                PageTitle = "Clipboard";
                PageDescription = "自动记录复制的文本、图片、文件与文件夹引用。";
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
            if (!hasGlobalSearch && CurrentSection == "Space")
            {
                SpaceItemCount = Items.Count;
            }

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
        await PublishSpaceProjectionChangedAsync(cancellationToken);
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
            var projectedCard = Items.FirstOrDefault(item => item.Id == refreshed.Id);
            if (projectedCard is not null && !ReferenceEquals(projectedCard, card))
            {
                projectedCard.Update(refreshed);
            }
        }

        if (CurrentSection == "Pinned" && !card.IsPinned)
        {
            ProjectionCollection.RemoveById(Items, item => item.Id, card.Id);

            ItemCount = Items.Count;
            IsEmpty = Items.Count == 0;
        }

        if (card.Item.Source == ItemSource.Space)
        {
            await PublishSpaceProjectionChangedAsync(cancellationToken);
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

        ProjectionCollection.RemoveById(Items, item => item.Id, card.Id);
        ItemCount = Items.Count;
        IsEmpty = Items.Count == 0;
        if (card.Item.Source == ItemSource.Space)
        {
            await PublishSpaceProjectionChangedAsync(cancellationToken);
        }

        StatusMessage = "已从 DropSpace 移除；原始文件未被修改。";
    }

    public async Task<IReadOnlyList<ItemCardViewModel>> GetRecentSpaceItemsAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        var items = await _repository.QueryAsync(
            new ItemQuery(Source: ItemSource.Space, Limit: limit),
            cancellationToken);
        var cards = items.Select(item => new ItemCardViewModel(item)).ToArray();
        await Task.WhenAll(cards.Select(card => LoadThumbnailSafelyAsync(card, cancellationToken)));
        return cards;
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

        if (card.Item.Source == ItemSource.Space)
        {
            await PublishSpaceProjectionChangedAsync(cancellationToken);
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
        var previous = Settings;
        var preflight = UiSettingsPreflightAsync;
        try
        {
            if (preflight is not null)
            {
                await preflight(settings, cancellationToken);
            }

            await _startupRegistration.SetEnabledAsync(settings.StartWithWindows, cancellationToken);
            await _clipboard.UpdateSettingsAsync(settings, cancellationToken);
            await _settingsService.SaveAsync(settings, cancellationToken);
            Settings = settings;
            StatusMessage = "设置已验证并保存。";
        }
        catch
        {
            await _startupRegistration.SetEnabledAsync(previous.StartWithWindows, CancellationToken.None);
            await _clipboard.UpdateSettingsAsync(previous, CancellationToken.None);
            if (preflight is not null)
            {
                try
                {
                    await preflight(previous, CancellationToken.None);
                }
                catch (Exception rollbackException)
                {
                    _logger.LogError(rollbackException, "UI settings rollback failed; the safe startup recovery remains available.");
                }
            }

            throw;
        }
    }

    public async Task<ClearResult> ClearClipboardAsync(ClearRange range, CancellationToken cancellationToken = default)
    {
        var fromUtc = GetClearFromUtc(range);
        var result = await _clipboard.ClearHistoryAsync(fromUtc, includePinned: false, cancellationToken);
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
        _updates.StatusChanged -= OnUpdateStatusChanged;
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
            return;
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
            return;
        }
        catch (Exception exception)
        {
            _logger.LogInformation(exception, "Thumbnail load failed for item {ItemId}.", card.Id);
        }
    }

    private async Task RefreshSpaceItemCountAsync(CancellationToken cancellationToken)
    {
        SpaceItemCount = await _repository.CountAsync(
            ItemSource.Space,
            cancellationToken: cancellationToken);
    }

    private async Task PublishSpaceProjectionChangedAsync(CancellationToken cancellationToken)
    {
        await RefreshSpaceItemCountAsync(cancellationToken);
        var revision = Interlocked.Increment(ref _spaceRevision);
        SpaceProjectionChanged?.Invoke(
            this,
            new SpaceProjectionChangedEventArgs(revision, SpaceItemCount));
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

    private void OnUpdateStatusChanged(object? sender, UpdateStatusSnapshot status)
    {
        if (_dispatcher.HasThreadAccess)
        {
            UpdateStatus = status;
        }
        else
        {
            _dispatcher.TryEnqueue(() => UpdateStatus = status);
        }
    }

    private async Task PersistLastUpdateCheckAsync(
        UpdateStatusSnapshot status,
        CancellationToken cancellationToken)
    {
        if (status.LastCheckedAtUtc is not { } checkedAt)
        {
            return;
        }

        var updated = Settings with { LastUpdateCheckUtc = checkedAt.ToUniversalTime() };
        await _settingsService.SaveAsync(updated, cancellationToken);
        if (_dispatcher.HasThreadAccess)
        {
            Settings = updated;
        }
        else
        {
            _dispatcher.TryEnqueue(() => Settings = updated);
        }
    }

    private static string FormatClipboardStatus(ClipboardCaptureStatus status) => status.State switch
    {
        ClipboardRecordingState.Recording => $"正在记录 · 已捕获 {status.CapturedItems} 项",
        ClipboardRecordingState.Paused => "已暂停记录",
        ClipboardRecordingState.Error => "记录发生错误",
        _ => string.Empty,
    };

    private static string FormatBytes(long value) => value switch
    {
        < 1024 => $"{value} B",
        < 1024 * 1024 => $"{value / 1024d:0.0} KB",
        < 1024L * 1024 * 1024 => $"{value / (1024d * 1024):0.0} MB",
        _ => $"{value / (1024d * 1024 * 1024):0.00} GB",
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
            return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StorageSummary = "暂时无法计算";
        }
    }
}

public sealed record SpaceProjectionChangedEventArgs(long Revision, int ItemCount);
