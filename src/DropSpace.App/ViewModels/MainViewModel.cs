using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using DropSpace.Core.Collections;
using DropSpace.App.Services;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Actions;
using DropSpace.Core.Models;
using DropSpace.Core.Overlay;
using DropSpace.Core.Preview;
using DropSpace.Core.Transfer;
using DropSpace.Core.Updates;
using DropSpace.Core.Undo;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using PlacementMode = DropSpace.Core.Models.OverlayPlacementMode;

namespace DropSpace.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable, IAsyncDisposable
{
    private readonly IItemRepository _repository;
    private readonly IItemActionRegistry _actions;
    private readonly UndoCoordinator _undo;
    private readonly ISettingsService _settingsService;
    private readonly IPayloadStore _payloadStore;
    private readonly IFileReferenceService _fileReferences;
    private readonly ILocalStorageMetrics _storageMetrics;
    private readonly IStartupRegistrationService _startupRegistration;
    private readonly WindowsShareIntegrationService _windowsShareIntegration;
    private readonly MonitorLayoutService _monitorLayout;
    private readonly DragSessionDetector _dragSessionDetector;
    private readonly GlobalQuickPanelHotkeyService _quickPanelHotkey;
    private readonly ClipboardCaptureService _clipboard;
    private readonly DeviceHandoffService _deviceHandoff;
    private readonly CrossDeviceClipboardService _crossDeviceClipboard;
    private readonly ShellActionService _shell;
    private readonly ThumbnailService _thumbnails;
    private readonly DragStorageItemService _dragStorageItems;
    private readonly IUpdateService _updates;
    private readonly DispatcherQueue _dispatcher;
    private readonly IAppStringLocalizer _strings;
    private readonly ILogger<MainViewModel> _logger;
    private CancellationTokenSource? _queryCancellation;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly object _backgroundTaskGate = new();
    private readonly HashSet<Task> _backgroundTasks = [];
    private int _asyncResourcesDisposed;
    private string _currentSection = "Space";
    private string _searchText = string.Empty;
    private string _pageTitle = string.Empty;
    private string _pageDescription = string.Empty;
    private string _statusMessage = string.Empty;
    private string _clipboardStatusText = string.Empty;
    private bool _isBusy;
    private bool _isEmpty = true;
    private bool _isSettingsVisible;
    private int _itemCount;
    private int _spaceItemCount;
    private long _spaceRevision;
    private ItemCardViewModel? _selectedItem;
    private AppSettings _settings = new();
    private string _storageSummary = string.Empty;
    private UpdateStatusSnapshot _updateStatus;
    private UndoOperationKind? _lastUndoKind;
    private bool _undoRequested;
    private bool _disposed;

    public MainViewModel(
        IItemRepository repository,
        IItemActionRegistry actions,
        UndoCoordinator undo,
        ISettingsService settingsService,
        IPayloadStore payloadStore,
        IFileReferenceService fileReferences,
        ILocalStorageMetrics storageMetrics,
        IStartupRegistrationService startupRegistration,
        WindowsShareIntegrationService windowsShareIntegration,
        MonitorLayoutService monitorLayout,
        DragSessionDetector dragSessionDetector,
        GlobalQuickPanelHotkeyService quickPanelHotkey,
        ClipboardCaptureService clipboard,
        DeviceHandoffService deviceHandoff,
        CrossDeviceClipboardService crossDeviceClipboard,
        ShellActionService shell,
        ThumbnailService thumbnails,
        DragStorageItemService dragStorageItems,
        IUpdateService updates,
        DispatcherQueue dispatcher,
        IAppStringLocalizer strings,
        ILogger<MainViewModel> logger)
    {
        _repository = repository;
        _actions = actions;
        _undo = undo;
        _settingsService = settingsService;
        _payloadStore = payloadStore;
        _fileReferences = fileReferences;
        _storageMetrics = storageMetrics;
        _startupRegistration = startupRegistration;
        _windowsShareIntegration = windowsShareIntegration;
        _monitorLayout = monitorLayout;
        _dragSessionDetector = dragSessionDetector;
        _quickPanelHotkey = quickPanelHotkey;
        _clipboard = clipboard;
        _deviceHandoff = deviceHandoff;
        _crossDeviceClipboard = crossDeviceClipboard;
        _shell = shell;
        _thumbnails = thumbnails;
        _dragStorageItems = dragStorageItems;
        _updates = updates;
        _updateStatus = updates.Status;
        _dispatcher = dispatcher;
        _strings = strings;
        _logger = logger;
        _pageTitle = _strings.Get("PageTitleSpace");
        _pageDescription = _strings.Get("PageDescriptionSpace");
        _clipboardStatusText = _strings.Get("ClipboardStarting");
        _storageSummary = _strings.Get("StorageCalculating");
        _clipboard.ItemCaptured += OnItemCaptured;
        _clipboard.StatusChanged += OnClipboardStatusChanged;
        _updates.StatusChanged += OnUpdateStatusChanged;
        _undo.StateChanged += OnUndoStateChanged;
    }

    public ObservableCollection<ItemCardViewModel> Items { get; } = [];

    public event EventHandler<SpaceProjectionChangedEventArgs>? SpaceProjectionChanged;

    public event EventHandler<string>? OverlayPlacementEditRequested;

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
                TrackBackgroundTask(DebouncedReloadAsync(), "search refresh");
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

    public UndoState? UndoState => _undo.State;

    public bool HasUndo => UndoState is not null;

    public string UndoMessage => UndoState switch
    {
        null => string.Empty,
        { MessageResourceKey: "UndoRemovedItem" } => _strings.Get("UndoRemovedItem"),
        { MessageResourceKey: var key } state => _strings.Format(key, state.ItemCount),
    };

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

    public string ItemCountText => _strings.Format("ItemCount", ItemCount);

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
                OnPropertyChanged(nameof(EnableDeviceHandoff));
                OnPropertyChanged(nameof(EnableCrossDeviceClipboard));
                OnPropertyChanged(nameof(EnableNearbySharing));
                OnPropertyChanged(nameof(EnableInternetSharing));
                OnPropertyChanged(nameof(DefaultClipboardSyncMode));
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
                OnPropertyChanged(nameof(OverlayMotion));
                OnPropertyChanged(nameof(OverlayMonitor));
                OnPropertyChanged(nameof(FileDragWakeMode));
                OnPropertyChanged(nameof(OverlayPlacementMode));
                OnPropertyChanged(nameof(QuickPanelHotkey));
                OnPropertyChanged(nameof(SmartDragExcludedProcessesText));
                OnPropertyChanged(nameof(AutoCheckForUpdates));
                OnPropertyChanged(nameof(AutoDownloadUpdates));
                OnPropertyChanged(nameof(AutoInstallUpdates));
                OnPropertyChanged(nameof(UpdateChannel));
                OnPropertyChanged(nameof(Language));
                OnPropertyChanged(nameof(LastUpdateCheckText));
                OnPropertyChanged(nameof(LastUpdateCheckDisplayText));
                foreach (var card in Items)
                {
                    RefreshPrimaryQuickActions(card);
                }
            }
        }
    }

    public bool IsClipboardPaused => Settings.ClipboardPaused;

    public bool CaptureImages => Settings.CaptureImages;

    public bool CaptureFiles => Settings.CaptureFiles;

    public bool CaptureFolders => Settings.CaptureFolders;

    public bool EnableDeviceHandoff => Settings.EnableDeviceHandoff;

    public bool EnableCrossDeviceClipboard => Settings.EnableCrossDeviceClipboard;

    public bool EnableNearbySharing => Settings.EnableNearbySharing;

    public bool EnableInternetSharing => Settings.EnableInternetSharing;

    public ClipboardSyncMode DefaultClipboardSyncMode => Settings.DefaultClipboardSyncMode;

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

    public OverlayMotionPreference OverlayMotion => Settings.OverlayMotion;

    public OverlayMonitorPreference OverlayMonitor => Settings.OverlayMonitor;

    public FileDragWakeMode FileDragWakeMode => Settings.FileDragWakeMode;

    // Retained for existing view-model consumers; the active mode is now resolved per monitor.
    public OverlayPlacementMode OverlayPlacementMode => PlacementMode.Automatic;

    public string QuickPanelHotkey => Settings.QuickPanelHotkey;

    public string SmartDragExcludedProcessesText => string.Join(", ", Settings.SmartDragExcludedProcesses);

    public IReadOnlyList<OverlayMonitorChoice> AvailableOverlayMonitors => _monitorLayout.GetMonitors()
        .Select((monitor, index) => new OverlayMonitorChoice(
            monitor.Id,
            monitor.IsPrimary
                ? _strings.Format("OverlayMonitorPrimaryChoice", index + 1)
                : _strings.Format("OverlayMonitorChoice", index + 1)))
        .ToArray();

    public OverlayMonitorPlacement GetOverlayPlacement(
        string monitorId,
        AppSettings? settings = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(monitorId);
        settings ??= Settings;
        var monitor = _monitorLayout.GetMonitors().FirstOrDefault(candidate =>
            string.Equals(candidate.Id, monitorId, StringComparison.Ordinal));
        if (monitor is { IsPersistent: false })
        {
            return new OverlayMonitorPlacement(PlacementMode.Automatic, 0, 0);
        }

        if (settings.OverlayPlacements.TryGetValue(monitorId, out var placement))
        {
            return placement;
        }

        if (monitor is null)
        {
            return new OverlayMonitorPlacement(PlacementMode.Automatic, 0, 0);
        }

        return new OverlayMonitorPlacement(
            PlacementMode.Automatic,
            monitor.EffectiveWorkWidth / monitor.Scale / 2,
            OverlayPlacementPolicy.GetTopOffsetDips(settings.FileDragWakeMode, monitor.Scale));
    }

    public Task SetCustomOverlayPlacementAsync(
        string monitorId,
        double x,
        double y,
        CancellationToken cancellationToken = default)
    {
        if (!CanPersistPlacement(monitorId))
        {
            return Task.CompletedTask;
        }

        var placements = new Dictionary<string, OverlayMonitorPlacement>(
            Settings.OverlayPlacements,
            StringComparer.Ordinal);
        placements[monitorId] = new OverlayMonitorPlacement(PlacementMode.Custom, x, y);
        return UpdateSettingsAsync(Settings with
        {
            OverlayPlacements = placements,
        }, cancellationToken);
    }

    public Task SetOverlayPlacementModeAsync(
        string monitorId,
        OverlayPlacementMode mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(monitorId);
        var placements = new Dictionary<string, OverlayMonitorPlacement>(
            Settings.OverlayPlacements,
            StringComparer.Ordinal);
        if (mode == PlacementMode.Automatic)
        {
            placements.Remove(monitorId);
        }
        else
        {
            if (!CanPersistPlacement(monitorId))
            {
                return Task.CompletedTask;
            }

            var current = GetOverlayPlacement(monitorId);
            placements[monitorId] = current.Mode == PlacementMode.Custom
                ? current
                : new OverlayMonitorPlacement(PlacementMode.Custom, current.X, current.Y);
        }

        return UpdateSettingsAsync(Settings with { OverlayPlacements = placements }, cancellationToken);
    }

    public bool CanPersistOverlayPlacement(string monitorId)
    {
        var monitor = _monitorLayout.GetMonitors().FirstOrDefault(candidate =>
            string.Equals(candidate.Id, monitorId, StringComparison.Ordinal));
        if (monitor is { IsPersistent: false })
        {
            _logger.LogWarning(
                "Display identity {MonitorId} is a runtime fallback; custom placement edit rejected because the identity is not persistent.",
                monitorId);
            return false;
        }

        return monitor is not null;
    }

    private bool CanPersistPlacement(string monitorId) => CanPersistOverlayPlacement(monitorId);

    public Task ResetOverlayPlacementAsync(
        string monitorId,
        CancellationToken cancellationToken = default)
    {
        var placements = new Dictionary<string, OverlayMonitorPlacement>(
            Settings.OverlayPlacements,
            StringComparer.Ordinal);
        placements.Remove(monitorId);
        return UpdateSettingsAsync(Settings with { OverlayPlacements = placements }, cancellationToken);
    }

    public void RequestOverlayPlacementEdit(string monitorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(monitorId);
        OverlayPlacementEditRequested?.Invoke(this, monitorId);
    }

    public bool AutoCheckForUpdates => Settings.AutoCheckForUpdates;

    public bool AutoDownloadUpdates => Settings.AutoDownloadUpdates;

    public bool AutoInstallUpdates => Settings.AutoInstallUpdates;

    public UpdateChannel UpdateChannel => Settings.UpdateChannel;

    public AppLanguagePreference Language => Settings.Language;

    public string CurrentVersionText => _updates.CurrentVersion.ToString();

    public string CurrentVersionDisplayText => _strings.Format("CurrentVersion", CurrentVersionText);

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

    public string UpdateStatusText => string.IsNullOrWhiteSpace(UpdateStatus.Message)
        ? _strings.Get("UpdateNotChecked")
        : UpdateStatus.Message;

    public string UpdateProgressText => UpdateStatus.Progress is { } progress
        ? _strings.Format("UpdateProgress", progress.Percentage, FormatBytes(progress.BytesReceived), FormatBytes(progress.TotalBytes))
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
        DeploymentMode.Installer => _strings.Get("DeploymentInstaller"),
        DeploymentMode.Portable => _strings.Get("DeploymentPortable"),
        DeploymentMode.Packaged => _strings.Get("DeploymentPackaged"),
        _ => _strings.Get("DeploymentUnknown"),
    };

    public string LastUpdateCheckText => Settings.LastUpdateCheckUtc is { } value
        ? value.ToLocalTime().ToString(_strings.Get("LastUpdateCheckDateFormat"), _strings.Culture)
        : _strings.Get("UpdateNotChecked");

    public string LastUpdateCheckDisplayText => _strings.Format("LastUpdateCheck", LastUpdateCheckText);

    public bool HasWindowsShareIdentity => _windowsShareIntegration.HasPackageIdentity;

    public string WindowsShareIntegrationStatus => _windowsShareIntegration.StatusText;

    public Task<bool> OpenDropTraySettingsAsync() =>
        _windowsShareIntegration.OpenDropTraySettingsAsync();

    public void CopyDragCompatibilityReport()
    {
        var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
        package.SetText(_dragSessionDetector.CreateCompatibilityReport());
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
        StatusMessage = _strings.Get("DragCompatibilityReportCopied");
    }

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

    public string StorageSummaryText => _strings.Format("StorageSummary", StorageSummary);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Settings = await _settingsService.LoadAsync(cancellationToken);
        var migratedSettings = MigrateLegacyOverlayPlacements(Settings);
        if (!ReferenceEquals(migratedSettings, Settings))
        {
            await _settingsService.SaveAsync(migratedSettings, cancellationToken);
            Settings = migratedSettings;
        }
        await _startupRegistration.SetEnabledAsync(Settings.StartWithWindows, cancellationToken);
        await _repository.InitializeAsync(cancellationToken);
        await _undo.RecoverStaleAsync(cancellationToken);
        await RefreshSpaceItemCountAsync(cancellationToken);
        await _clipboard.InitializeAsync(cancellationToken);
        ClipboardStatusText = FormatClipboardStatus(_clipboard.Status);
        UpdateStatus = await _updates.RecoverPendingAsync(cancellationToken);
        TrackBackgroundTask(RefreshStorageSummaryAsync(cancellationToken), "storage summary refresh");
        await NavigateAsync(Settings.LaunchPage, cancellationToken);
    }

    private AppSettings MigrateLegacyOverlayPlacements(AppSettings settings)
    {
        if (settings.OverlayPlacementMode == PlacementMode.Automatic &&
            settings.CustomOverlayPlacements.Count == 0)
        {
            return settings;
        }

        var placements = new Dictionary<string, OverlayMonitorPlacement>(
            settings.OverlayPlacements,
            StringComparer.Ordinal);
        foreach (var monitor in _monitorLayout.GetMonitors())
        {
            if (!monitor.IsPersistent)
            {
                continue;
            }

            var legacyId = monitor.Handle.ToInt64().ToString("X", System.Globalization.CultureInfo.InvariantCulture);
            if (settings.CustomOverlayPlacements.TryGetValue(legacyId, out var legacyPlacement))
            {
                placements[monitor.Id] = new OverlayMonitorPlacement(
                    PlacementMode.Custom,
                    legacyPlacement.X,
                    legacyPlacement.Y);
            }
        }

        return settings with
        {
            Version = AppSettings.CurrentVersion,
            OverlayPlacementMode = PlacementMode.Automatic,
            CustomOverlayPlacements = [],
            OverlayPlacements = placements,
        };
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
        StatusMessage = _strings.Format("AppUpdated", version);

    public async Task NavigateAsync(string section, CancellationToken cancellationToken = default)
    {
        CurrentSection = section;
        IsSettingsVisible = string.Equals(section, "Settings", StringComparison.Ordinal);
        switch (section)
        {
            case "Clipboard":
                PageTitle = _strings.Get("PageTitleClipboard");
                PageDescription = _strings.Get("PageDescriptionClipboard");
                break;
            case "Pinned":
                PageTitle = _strings.Get("PageTitlePinned");
                PageDescription = _strings.Get("PageDescriptionPinned");
                break;
            case "Settings":
                PageTitle = _strings.Get("PageTitleSettings");
                PageDescription = _strings.Get("PageDescriptionSettings");
                Items.Clear();
                ItemCount = 0;
                IsEmpty = true;
                return;
            default:
                CurrentSection = "Space";
                PageTitle = _strings.Get("PageTitleSpace");
                PageDescription = _strings.Get("PageDescriptionSpace");
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
                var card = new ItemCardViewModel(item, _strings);
                RefreshPrimaryQuickActions(card);
                Items.Add(card);
                TrackBackgroundTask(LoadThumbnailSafelyAsync(card, cancellationToken), "thumbnail load");
            }
            ApplyBatchProjectionState();

            ItemCount = Items.Count;
            IsEmpty = Items.Count == 0;
            if (!hasGlobalSearch && CurrentSection == "Space")
            {
                SpaceItemCount = Items.Count;
            }

            StatusMessage = hasGlobalSearch && Items.Count == 0 ? _strings.Get("SearchNoMatches") : string.Empty;
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<int> AddPathsAsync(IEnumerable<string> paths, CancellationToken cancellationToken = default)
        => await AddPathsBatchAsync(paths, null, "file-drop", cancellationToken);

    public async Task<int> AddPathsBatchAsync(
        IEnumerable<string> paths,
        long? dropSessionId,
        string acquisitionKind,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrWhiteSpace(acquisitionKind);
        var uniquePaths = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var batchId = Guid.NewGuid();
        var accepted = 0;
        var rejected = 0;
        for (var index = 0; index < uniquePaths.Length; index++)
        {
            var path = uniquePaths[index];
            try
            {
                var candidate = await _fileReferences.InspectAsync(path, cancellationToken);
                var metadata = JsonSerializer.Serialize(new DropBatchMetadata(
                    batchId,
                    dropSessionId,
                    index,
                    uniquePaths.Length,
                    acquisitionKind));
                await _repository.AddSpaceFileAsync(candidate, metadata, cancellationToken);
                accepted++;
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                rejected++;
                _logger.LogWarning(exception, "A dropped file reference was rejected.");
            }
        }

        StatusMessage = rejected == 0
            ? _strings.Format("ItemsAdded", accepted)
            : _strings.Format("ItemsAddedWithRejected", accepted, rejected);
        await ReloadAsync(cancellationToken);
        await PublishSpaceProjectionChangedAsync(cancellationToken);
        return accepted;
    }

    public async Task<int> AddOwnedPathsBatchAsync(
        IEnumerable<string> stagingPaths,
        long? dropSessionId,
        string acquisitionKind,
        long maximumFileBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stagingPaths);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumFileBytes);
        var paths = stagingPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var batchId = Guid.NewGuid();
        var accepted = 0;
        var rejected = 0;
        for (var index = 0; index < paths.Length; index++)
        {
            var stagingPath = paths[index];
            PayloadRecord? payload = null;
            try
            {
                var stagedCandidate = await _fileReferences.InspectAsync(stagingPath, cancellationToken);
                await using var input = new FileStream(
                    stagingPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    81_920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                payload = await _payloadStore.WriteFileAsync(
                    "files",
                    stagedCandidate.Extension,
                    input,
                    maximumFileBytes,
                    cancellationToken);
                var ownedPath = _payloadStore.ResolvePath(payload.RelativePath);
                var ownedCandidate = stagedCandidate with
                {
                    OriginalPath = ownedPath,
                    NormalizedPath = Path.GetFullPath(ownedPath),
                };
                var metadata = JsonSerializer.Serialize(new DropBatchMetadata(
                    batchId,
                    dropSessionId,
                    index,
                    paths.Length,
                    acquisitionKind));
                await _repository.AddOwnedSpaceFileAsync(ownedCandidate, payload, metadata, cancellationToken);
                payload = null;
                accepted++;
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                rejected++;
                _logger.LogWarning(exception, "An app-owned staged item was rejected without logging its filename or path.");
            }
            finally
            {
                if (payload is not null)
                {
                    await _payloadStore.DeleteAsync(payload.RelativePath, CancellationToken.None);
                }
                try
                {
                    if (File.Exists(stagingPath))
                    {
                        File.Delete(stagingPath);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(exception, "A consumed staging file could not be removed immediately.");
                }
            }
        }

        StatusMessage = rejected == 0
            ? _strings.Format("ItemsAdded", accepted)
            : _strings.Format("ItemsAddedWithRejected", accepted, rejected);
        await ReloadAsync(cancellationToken);
        await PublishSpaceProjectionChangedAsync(cancellationToken);
        return accepted;
    }

    public async Task<DropItem> AddTextToSpaceAsync(
        string text,
        string acquisitionKind,
        long? dropSessionId = null,
        CancellationToken cancellationToken = default)
    {
        if (text.Length > Settings.MaxTextCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(text), "Text exceeds the configured local limit.");
        }

        var metadata = JsonSerializer.Serialize(new DropBatchMetadata(
            Guid.NewGuid(),
            dropSessionId,
            0,
            1,
            acquisitionKind));
        var item = await _repository.AddSpaceTextAsync(
            DropSpace.Core.Policies.ContentClassifier.CreateTextCandidate(text),
            metadata,
            cancellationToken);
        await ReloadAsync(cancellationToken);
        await PublishSpaceProjectionChangedAsync(cancellationToken);
        return item;
    }

    public async Task TogglePinAsync(ItemCardViewModel card, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(card);
        await _undo.FinalizeActiveAsync(cancellationToken);
        var previousState = card.IsPinned;
        var nextState = !previousState;
        await _repository.SetPinnedAsync(card.Id, nextState, cancellationToken);
        await _undo.RegisterPinChangeAsync(
            new Dictionary<Guid, bool> { [card.Id] = previousState },
            nextState ? "UndoPinned" : "UndoUnpinned",
            cancellationToken);
        var refreshed = await _repository.GetAsync(card.Id, cancellationToken);
        if (refreshed is not null)
        {
            card.Update(refreshed);
            RefreshPrimaryQuickActions(card);
            var projectedCard = Items.FirstOrDefault(item => item.Id == refreshed.Id);
            if (projectedCard is not null && !ReferenceEquals(projectedCard, card))
            {
                projectedCard.Update(refreshed);
                RefreshPrimaryQuickActions(projectedCard);
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

    public async Task ToggleBatchPinAsync(ItemCardViewModel card, CancellationToken cancellationToken = default)
    {
        if (card.DropBatchId is not { } batchId)
        {
            await TogglePinAsync(card, cancellationToken);
            return;
        }
        var members = await _repository.QueryDropBatchAsync(batchId, cancellationToken);
        var pin = members.Any(item => !item.IsPinned);
        var previousStates = members.ToDictionary(item => item.Id, item => item.IsPinned);
        await _undo.FinalizeActiveAsync(cancellationToken);
        foreach (var member in members)
        {
            await _repository.SetPinnedAsync(member.Id, pin, cancellationToken);
        }
        await _undo.RegisterPinChangeAsync(
            previousStates,
            pin ? "UndoPinned" : "UndoUnpinned",
            cancellationToken);
        await ReloadAsync(cancellationToken);
        await PublishSpaceProjectionChangedAsync(cancellationToken);
    }

    public async Task RemoveBatchAsync(ItemCardViewModel card, CancellationToken cancellationToken = default)
    {
        if (card.DropBatchId is not { } batchId)
        {
            await RemoveAsync(card, cancellationToken);
            return;
        }
        var members = await _repository.QueryDropBatchAsync(batchId, cancellationToken);
        if (members.Count == 0)
        {
            return;
        }
        await _undo.BeginRemovalAsync(
            members.Select(member => member.Id).ToArray(),
            UndoOperationKind.RemoveBatch,
            "UndoRemovedItems",
            cancellationToken);
        await ReloadAsync(cancellationToken);
        await PublishSpaceProjectionChangedAsync(cancellationToken);
    }

    public void ToggleBatchExpanded(ItemCardViewModel card)
    {
        if (card.DropBatchId is not { } batchId)
        {
            return;
        }
        var members = Items.Where(item => item.DropBatchId == batchId).ToArray();
        var expanded = !card.IsBatchExpanded;
        foreach (var member in members)
        {
            member.IsBatchExpanded = expanded;
            member.IsBatchMemberVisible = member.IsBatchHeader || expanded;
        }
    }

    private void ApplyBatchProjectionState()
    {
        foreach (var group in Items.Where(item => item.IsGrouped && item.DropBatchId is not null)
                     .GroupBy(item => item.DropBatchId))
        {
            var first = true;
            foreach (var card in group.OrderBy(item => item.BatchMetadata?.ItemIndex ?? int.MaxValue))
            {
                card.IsBatchHeader = first;
                card.IsBatchMemberVisible = true;
                first = false;
            }
        }
    }

    public async Task RemoveAsync(ItemCardViewModel card, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(card);
        await _undo.BeginRemovalAsync(
            [card.Id],
            UndoOperationKind.RemoveItem,
            "UndoRemovedItem",
            cancellationToken);

        ProjectionCollection.RemoveById(Items, item => item.Id, card.Id);
        ItemCount = Items.Count;
        IsEmpty = Items.Count == 0;
        if (card.Item.Source == ItemSource.Space)
        {
            await PublishSpaceProjectionChangedAsync(cancellationToken);
        }

        StatusMessage = _strings.Get("ItemRemoved");
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
        var cards = items.Select(item =>
        {
            var card = new ItemCardViewModel(item, _strings);
            RefreshPrimaryQuickActions(card);
            return card;
        }).ToArray();
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
            StatusMessage = item.File is null
                ? _strings.Get("ItemCannotOpen")
                : _strings.Get("FileUnavailableRelocate");
        }

        return opened;
    }

    public async Task CopyAsync(ItemCardViewModel card, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(card);
        await _shell.CopyAsync(card.Item, cancellationToken);
        await _repository.MarkUsedAsync(card.Id, cancellationToken);
        StatusMessage = _strings.Get("ItemCopied");
    }

    public async Task ShowInFolderAsync(ItemCardViewModel card, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (!await _shell.ShowInFolderAsync(card.Item, cancellationToken))
        {
            StatusMessage = _strings.Get("ItemCannotShowInFolder");
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
            RefreshPrimaryQuickActions(card);
            card.DragStorageItem = null;
            await LoadThumbnailSafelyAsync(card, cancellationToken);
        }

        if (card.Item.Source == ItemSource.Space)
        {
            await PublishSpaceProjectionChangedAsync(cancellationToken);
        }

        StatusMessage = _strings.Get("FileReferenceUpdated");
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
            if (!string.Equals(settings.QuickPanelHotkey, previous.QuickPanelHotkey, StringComparison.OrdinalIgnoreCase) &&
                !_quickPanelHotkey.CanRegister(settings.QuickPanelHotkey))
            {
                throw new InvalidOperationException("The requested Quick Panel hotkey is already registered by another application.");
            }
            if (preflight is not null)
            {
                await preflight(settings, cancellationToken);
            }

            await _startupRegistration.SetEnabledAsync(settings.StartWithWindows, cancellationToken);
            await _clipboard.UpdateSettingsAsync(settings, cancellationToken);
            await _deviceHandoff.UpdateSettingsAsync(settings, cancellationToken);
            await _crossDeviceClipboard.UpdateSettingsAsync(settings, cancellationToken);
            await _settingsService.SaveAsync(settings, cancellationToken);
            Settings = settings;
            StatusMessage = settings.Language == previous.Language
                ? _strings.Get("SettingsSaved")
                : _strings.Get("LanguageChangeRestartRequired");
        }
        catch
        {
            await _startupRegistration.SetEnabledAsync(previous.StartWithWindows, CancellationToken.None);
            await _clipboard.UpdateSettingsAsync(previous, CancellationToken.None);
            await _deviceHandoff.UpdateSettingsAsync(previous, CancellationToken.None);
            await _crossDeviceClipboard.UpdateSettingsAsync(previous, CancellationToken.None);
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
        var state = await _undo.BeginClipboardClearAsync(
            fromUtc,
            includePinned: false,
            "UndoClearedItems",
            cancellationToken);
        await _clipboard.ResetCaptureSequenceAsync(cancellationToken);
        var result = new ClearResult(state?.ItemCount ?? 0, Array.Empty<string>());

        StatusMessage = _strings.Format("ItemsCleared", result.RemovedCount);
        if (CurrentSection is "Clipboard" or "Pinned")
        {
            await ReloadAsync(cancellationToken);
        }

        return result;
    }

    public async Task<bool> UndoAsync(CancellationToken cancellationToken = default)
    {
        _undoRequested = true;
        bool undone;
        try
        {
            undone = await _undo.UndoAsync(cancellationToken);
        }
        finally
        {
            _undoRequested = false;
        }
        if (!undone)
        {
            return false;
        }

        StatusMessage = _strings.Get("UndoCompleted");
        if (!IsSettingsVisible)
        {
            await ReloadAsync(cancellationToken);
        }

        await PublishSpaceProjectionChangedAsync(cancellationToken);
        return true;
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
            throw new InvalidOperationException(_strings.Get("ExportOnlyClipboardImages"));
        }

        await _payloadStore.ExportAsync(card.Item.Payload.RelativePath, destinationPath, cancellationToken);
        StatusMessage = _strings.Get("ImageExported");
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
        _undo.StateChanged -= OnUndoStateChanged;
        _queryCancellation?.Cancel();
        _lifetimeCancellation.Cancel();
        _disposed = true;
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();

        while (true)
        {
            Task[] tasks;
            lock (_backgroundTaskGate)
            {
                tasks = _backgroundTasks.ToArray();
            }

            if (tasks.Length == 0)
            {
                break;
            }

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // User-requested shutdown cancellation is expected for owned background work.
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Owned MainViewModel background work failed during shutdown.");
            }
        }

        if (Interlocked.Exchange(ref _asyncResourcesDisposed, 1) == 0)
        {
            _queryCancellation?.Dispose();
            _lifetimeCancellation.Dispose();
        }
    }

    private void TrackBackgroundTask(Task task, string operation)
    {
        lock (_backgroundTaskGate)
        {
            _backgroundTasks.Add(task);
        }

        task.ContinueWith(
            completed =>
            {
                lock (_backgroundTaskGate)
                {
                    _backgroundTasks.Remove(task);
                }

                if (completed.IsFaulted)
                {
                    _logger.LogWarning(
                        completed.Exception?.GetBaseException(),
                        "Owned MainViewModel {Operation} failed.",
                        operation);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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
            RefreshPrimaryQuickActions(card);
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
            StatusMessage = _strings.Get("SearchUnavailable");
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
                    RefreshPrimaryQuickActions(existing);
                    Items.Move(Items.IndexOf(existing), 0);
                }
                else
                {
                    var card = new ItemCardViewModel(item, _strings);
                    RefreshPrimaryQuickActions(card);
                    Items.Insert(0, card);
                    TrackBackgroundTask(LoadThumbnailSafelyAsync(card, _lifetimeCancellation.Token), "thumbnail load");
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

    private void OnUndoStateChanged(object? sender, EventArgs args)
    {
        var state = _undo.State;
        var wasRemoval = _lastUndoKind is UndoOperationKind.RemoveItem or
            UndoOperationKind.RemoveBatch or UndoOperationKind.ClearClipboard;
        _lastUndoKind = state?.Kind;

        void Apply()
        {
            OnPropertyChanged(nameof(UndoState));
            OnPropertyChanged(nameof(HasUndo));
            OnPropertyChanged(nameof(UndoMessage));
            if (state is null && wasRemoval && !_undoRequested)
            {
                TrackBackgroundTask(RefreshAfterUndoFinalizationAsync(_lifetimeCancellation.Token), "undo projection refresh");
            }
        }

        if (_dispatcher.HasThreadAccess)
        {
            Apply();
        }
        else
        {
            _dispatcher.TryEnqueue(Apply);
        }
    }

    private async Task RefreshAfterUndoFinalizationAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!IsSettingsVisible)
            {
                await ReloadAsync(cancellationToken);
            }

            await PublishSpaceProjectionChangedAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The owned refresh was superseded or the view model is shutting down.
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "The item projection could not refresh after Undo finalization.");
        }
    }

    public QuickActionPartition EvaluateQuickActions(ItemCardViewModel card)
    {
        ArgumentNullException.ThrowIfNull(card);
        var selection = new ItemSelectionSnapshot([DropItemSnapshot.FromItem(card.Item)]);
        return QuickActionPreferencePolicy.Partition(
            _actions.Evaluate(selection),
            selection,
            Settings.QuickActionPreferences);
    }

    public async Task<ItemActionResult> ExecuteQuickActionAsync(
        ItemCardViewModel card,
        ItemActionId actionId,
        CancellationToken cancellationToken = default)
        => await ExecuteQuickActionAsync(
            card,
            actionId,
            actionContext: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);

    public async Task<ItemActionResult> ExecuteQuickActionAsync(
        ItemCardViewModel card,
        ItemActionId actionId,
        ItemActionContext? actionContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(card);
        var selection = new ItemSelectionSnapshot([DropItemSnapshot.FromItem(card.Item)]);
        var capability = _actions.Evaluate(selection)
            .FirstOrDefault(candidate => candidate.Descriptor.Id == actionId && candidate.IsAvailable);
        if (capability is null)
        {
            return ItemActionResult.Failure("action-unavailable", "ActionUnavailable");
        }

        var context = actionContext is null
            ? new ItemActionContext(selection, CancellationToken: cancellationToken)
            : actionContext with
            {
                // The card is the authority for availability; never execute a context for a
                // stale or different selection supplied by a view.
                Selection = selection,
                CancellationToken = cancellationToken,
            };
        var result = await _actions.ExecuteAsync(actionId, context, cancellationToken);
        if (!string.IsNullOrWhiteSpace(result.MessageResourceKey))
        {
            StatusMessage = _strings.Get(result.MessageResourceKey);
        }

        return result;
    }

    public void RefreshPrimaryQuickActions(ItemCardViewModel card)
    {
        ArgumentNullException.ThrowIfNull(card);
        var partition = EvaluateQuickActions(card);
        card.PrimaryQuickActions.Clear();
        foreach (var capability in partition.Primary)
        {
            card.PrimaryQuickActions.Add(new QuickActionButtonViewModel(card, capability, _strings));
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

    private string FormatClipboardStatus(ClipboardCaptureStatus status) => status.State switch
    {
        ClipboardRecordingState.Recording => _strings.Format("ClipboardRecording", status.CapturedItems),
        ClipboardRecordingState.Paused => _strings.Get("ClipboardPausedStatus"),
        ClipboardRecordingState.Error => _strings.Get("ClipboardErrorStatus"),
        _ => string.Empty,
    };

    private string FormatBytes(long value) => value switch
    {
        < 1024 => _strings.Format("Bytes", value),
        < 1024 * 1024 => _strings.Format("Kilobytes", value / 1024d),
        < 1024L * 1024 * 1024 => _strings.Format("Megabytes", value / (1024d * 1024)),
        _ => _strings.Format("Gigabytes", value / (1024d * 1024 * 1024)),
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
                StorageSummary = _strings.Get("StorageUnavailable");
                return;
            }

            StorageSummary = FormatBytes(bytes.Value);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StorageSummary = _strings.Get("StorageUnavailable");
        }
    }
}

public sealed record OverlayMonitorChoice(string Id, string DisplayName);

public sealed record SpaceProjectionChangedEventArgs(long Revision, int ItemCount);
