from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str):
    raw = (ROOT / path).read_bytes()
    newline = "\r\n" if raw.count(b"\r\n") > raw.count(b"\n") // 2 else "\n"
    return raw.decode("utf-8").replace("\r\n", "\n"), newline


def write(path: str, text: str, newline: str):
    if newline == "\r\n":
        text = text.replace("\n", "\r\n")
    (ROOT / path).write_bytes(text.encode("utf-8"))


def replace_once(path: str, old: str, new: str):
    text, newline = read(path)
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{path}: expected one match, found {count}: {old[:120]!r}")
    write(path, text.replace(old, new, 1), newline)


def replace_between(path: str, start: str, end: str, replacement: str):
    text, newline = read(path)
    start_index = text.find(start)
    end_index = text.find(end, start_index + len(start))
    if start_index < 0 or end_index < 0:
        raise RuntimeError(f"{path}: marker block not found")
    if text.find(start, start_index + 1) >= 0:
        raise RuntimeError(f"{path}: start marker was not unique")
    write(path, text[:start_index] + replacement + text[end_index:], newline)


vm = "src/DropSpace.App/ViewModels/MainViewModel.cs"

replace_once(
    vm,
    """    private readonly StagedFileImportService _stagedFiles;\n    private readonly SemaphoreSlim _settingsChangeGate = new(1, 1);\n    private readonly IItemActionRegistry _actions;\n    private readonly UndoCoordinator _undo;\n    private readonly ISettingsService _settingsService;\n""",
    """    private readonly StagedFileImportService _stagedFiles;\n    private readonly IItemActionRegistry _actions;\n    private readonly UndoCoordinator _undo;\n    private readonly SettingsApplicationCoordinator _settingsCoordinator;\n""",
)
replace_once(
    vm,
    """    private readonly ILocalStorageMetrics _storageMetrics;\n    private readonly IStartupRegistrationService _startupRegistration;\n    private readonly WindowsShareIntegrationService _windowsShareIntegration;\n""",
    """    private readonly ILocalStorageMetrics _storageMetrics;\n    private readonly WindowsShareIntegrationService _windowsShareIntegration;\n""",
)
replace_once(
    vm,
    """    private readonly MonitorLayoutService _monitorLayout;\n    private readonly DragSessionDetector _dragSessionDetector;\n    private readonly GlobalQuickPanelHotkeyService _quickPanelHotkey;\n    private readonly ClipboardCaptureService _clipboard;\n    private readonly DeviceHandoffService _deviceHandoff;\n    private readonly CrossDeviceClipboardService _crossDeviceClipboard;\n""",
    """    private readonly MonitorLayoutService _monitorLayout;\n    private readonly DragSessionDetector _dragSessionDetector;\n    private readonly ClipboardCaptureService _clipboard;\n""",
)
replace_once(
    vm,
    """        IItemActionRegistry actions,\n        UndoCoordinator undo,\n        ISettingsService settingsService,\n        IPayloadStore payloadStore,\n        IFileReferenceService fileReferences,\n        ILocalStorageMetrics storageMetrics,\n        IStartupRegistrationService startupRegistration,\n        WindowsShareIntegrationService windowsShareIntegration,\n        MonitorLayoutService monitorLayout,\n        DragSessionDetector dragSessionDetector,\n        GlobalQuickPanelHotkeyService quickPanelHotkey,\n        ClipboardCaptureService clipboard,\n        DeviceHandoffService deviceHandoff,\n        CrossDeviceClipboardService crossDeviceClipboard,\n""",
    """        IItemActionRegistry actions,\n        UndoCoordinator undo,\n        SettingsApplicationCoordinator settingsCoordinator,\n        IPayloadStore payloadStore,\n        IFileReferenceService fileReferences,\n        ILocalStorageMetrics storageMetrics,\n        WindowsShareIntegrationService windowsShareIntegration,\n        MonitorLayoutService monitorLayout,\n        DragSessionDetector dragSessionDetector,\n        ClipboardCaptureService clipboard,\n""",
)
replace_once(
    vm,
    """        _actions = actions;\n        _undo = undo;\n        _settingsService = settingsService;\n        _payloadStore = payloadStore;\n        _fileReferences = fileReferences;\n        _storageMetrics = storageMetrics;\n        _startupRegistration = startupRegistration;\n        _windowsShareIntegration = windowsShareIntegration;\n        _monitorLayout = monitorLayout;\n        _dragSessionDetector = dragSessionDetector;\n        _quickPanelHotkey = quickPanelHotkey;\n        _clipboard = clipboard;\n        _deviceHandoff = deviceHandoff;\n        _crossDeviceClipboard = crossDeviceClipboard;\n""",
    """        _actions = actions;\n        _undo = undo;\n        _settingsCoordinator = settingsCoordinator;\n        _payloadStore = payloadStore;\n        _fileReferences = fileReferences;\n        _storageMetrics = storageMetrics;\n        _windowsShareIntegration = windowsShareIntegration;\n        _monitorLayout = monitorLayout;\n        _dragSessionDetector = dragSessionDetector;\n        _clipboard = clipboard;\n""",
)

replace_once(
    vm,
    """        Settings = await _settingsService.LoadAsync(cancellationToken);\n        var migratedSettings = MigrateLegacyOverlayPlacements(Settings);\n        if (!ReferenceEquals(migratedSettings, Settings))\n        {\n            await _settingsService.SaveAsync(migratedSettings, cancellationToken);\n            Settings = migratedSettings;\n        }\n        await _startupRegistration.SetEnabledAsync(Settings.StartWithWindows, cancellationToken);\n""",
    """        Settings = await _settingsCoordinator.LoadAsync(cancellationToken);\n        var migratedSettings = MigrateLegacyOverlayPlacements(Settings);\n        if (!ReferenceEquals(migratedSettings, Settings))\n        {\n            await _settingsCoordinator.SaveAsync(migratedSettings, cancellationToken);\n            Settings = migratedSettings;\n        }\n        await _settingsCoordinator.EnsureStartupStateAsync(Settings, cancellationToken);\n""",
)

replacement = """    public async Task SetClipboardPausedAsync(bool paused, CancellationToken cancellationToken = default)\n    {\n        Settings = await _settingsCoordinator.SetClipboardPausedAsync(paused, cancellationToken);\n    }\n\n    public async Task UpdateSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)\n    {\n        ArgumentNullException.ThrowIfNull(settings);\n        var previous = Settings;\n        var previousStatus = StatusMessage;\n        try\n        {\n            Settings = await _settingsCoordinator.UpdateAsync(\n                previous,\n                settings,\n                UiSettingsPreflightAsync,\n                cancellationToken);\n            StatusMessage = Settings.Language == previous.Language\n                ? _strings.Get(\"SettingsSaved\")\n                : _strings.Get(\"LanguageChangeRestartRequired\");\n        }\n        catch\n        {\n            Settings = await _settingsCoordinator.RecoverPersistedStateAsync(previous);\n            StatusMessage = previousStatus;\n            throw;\n        }\n    }\n\n"""
replace_between(
    vm,
    "    public async Task SetClipboardPausedAsync(bool paused, CancellationToken cancellationToken = default)\n",
    "    public async Task<ClearResult> ClearClipboardAsync(ClearRange range, CancellationToken cancellationToken = default)\n",
    replacement,
)

persist_replacement = """    private async Task PersistLastUpdateCheckAsync(\n        UpdateStatusSnapshot status,\n        CancellationToken cancellationToken)\n    {\n        if (status.LastCheckedAtUtc is not { } checkedAt)\n        {\n            return;\n        }\n\n        var updated = await _settingsCoordinator.UpdateLastCheckAsync(Settings, checkedAt, cancellationToken);\n        Task ApplyAsync()\n        {\n            Settings = updated;\n            return Task.CompletedTask;\n        }\n\n        if (_dispatcher.HasThreadAccess) await ApplyAsync();\n        else await _dispatcher.EnqueueAsync(ApplyAsync);\n    }\n\n"""
replace_between(
    vm,
    "    private async Task PersistLastUpdateCheckAsync(\n",
    "    private string FormatClipboardStatus(ClipboardCaptureStatus status) => status.State switch\n",
    persist_replacement,
)

replace_once(
    "src/DropSpace.App/App.xaml.cs",
    """        services.AddSingleton<SystemVisualPreferenceService>();\n        services.AddSingleton<ItemProjectionService>();\n        services.AddSingleton<MainViewModel>();\n""",
    """        services.AddSingleton<SystemVisualPreferenceService>();\n        services.AddSingleton<ItemProjectionService>();\n        services.AddSingleton<SettingsApplicationCoordinator>();\n        services.AddSingleton<MainViewModel>();\n""",
)

# Fail if any removed orchestration dependency leaked through the ViewModel.
text, _ = read(vm)
for forbidden in [
    "_settingsChangeGate",
    "_settingsService",
    "_startupRegistration",
    "_quickPanelHotkey",
    "_deviceHandoff",
    "_crossDeviceClipboard",
    "ReconcileSettingsStateAsync",
    "UpdateSettingsCoreAsync",
]:
    if forbidden in text:
        raise RuntimeError(f"MainViewModel still owns settings orchestration: {forbidden}")

print("MainViewModel settings orchestration extracted")
