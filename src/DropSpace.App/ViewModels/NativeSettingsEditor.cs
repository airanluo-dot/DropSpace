using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DropSpace.App.Services;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using Microsoft.Extensions.Logging;

namespace DropSpace.App.ViewModels;

/// <summary>Serializes edits against the latest settings and uses the existing transactional store.</summary>
public sealed class NativeSettingsEditor : ObservableObject, IAsyncDisposable
{
    private readonly MainViewModel _main;
    private readonly IAppStringLocalizer _strings;
    private readonly ILogger<NativeSettingsEditor> _logger;
    private readonly NativeFolderPickerService _folders;
    private readonly Services.Notifications.WindowsNotificationActivityService _notifications;
    private readonly SemaphoreSlim _save = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private AppUiMessage _error = AppUiMessage.Empty;
    private CancellationTokenSource? _downloadDelay;
    private Task _downloadSave = Task.CompletedTask;
    private (int? Connections, long? Rate, int? ConcurrentDownloads)? _pendingLimits;
    private int _savingDownloadLimits;
    public bool HasPendingDownloadLimits => _pendingLimits is not null || _savingDownloadLimits > 0;
    public DropSpace.Infrastructure.Downloads.DownloadManager Downloads { get; }
    public NativeSettingsEditor(MainViewModel main, IAppStringLocalizer strings, ILogger<NativeSettingsEditor> logger, NativeFolderPickerService folders, Services.Notifications.WindowsNotificationActivityService notifications, DropSpace.Infrastructure.Downloads.DownloadManager downloads)
    { _main = main; _strings = strings; _logger = logger; _folders = folders; _notifications = notifications; Downloads = downloads; main.PropertyChanged += OnChanged; }
    public Task<string?> PickDownloadFolderAsync(nint windowHandle) => _folders.PickAsync(windowHandle);
    public void OpenDownloadFolder(string directory) => NativeFolderPickerService.OpenDirectory(directory);
    public string DefaultDownloadDirectory => string.IsNullOrEmpty(Settings.DefaultDownloadDirectory) ? NativeFolderPickerService.GetDownloadsDirectory() : Settings.DefaultDownloadDirectory;
    public void QueueDownloadLimits(int? connections, long? rate, int? concurrentDownloads)
    {
        var pending = _pendingLimits;
        _pendingLimits = (connections ?? pending?.Connections, rate ?? pending?.Rate,
            concurrentDownloads ?? pending?.ConcurrentDownloads);
        _downloadDelay?.Cancel();
        _downloadDelay = new();
        _downloadSave = SaveDownloadLimitsAfterDelayAsync(_downloadDelay);
    }
    private async Task SaveDownloadLimitsAfterDelayAsync(CancellationTokenSource delay)
    {
        try { await Task.Delay(400, delay.Token); await FlushDownloadLimitsAsync(); }
        catch (OperationCanceledException) when (delay.IsCancellationRequested) { }
        finally { delay.Dispose(); if (ReferenceEquals(_downloadDelay, delay)) _downloadDelay = null; }
    }
    public async Task FlushDownloadLimitsAsync()
    {
        _downloadDelay?.Cancel();
        if (_pendingLimits is not { } limits) return;
        _pendingLimits = null;
        ++_savingDownloadLimits;
        try
        {
            await UpdateAsync(settings => settings with
            {
                MaxDownloadConnections = limits.Connections ?? settings.MaxDownloadConnections,
                DownloadSpeedLimitBytesPerSecond = limits.Rate ?? settings.DownloadSpeedLimitBytesPerSecond,
                MaxConcurrentDownloads = limits.ConcurrentDownloads ?? settings.MaxConcurrentDownloads
            });
        }
        finally { --_savingDownloadLimits; OnPropertyChanged(nameof(Settings)); }
    }
    private readonly Dictionary<string, Func<AppSettings, AppSettings>> _pendingEdits = [];
    private readonly Dictionary<string, int> _savingEdits = [];
    private CancellationTokenSource? _editDelay;
    private Task _editSave = Task.CompletedTask;
    public bool HasPendingEdit(string key) => _pendingEdits.ContainsKey(key) || _savingEdits.ContainsKey(key);
    public void QueueEdit(string key, Func<AppSettings, AppSettings> change)
    {
        _pendingEdits[key] = change;
        _editDelay?.Cancel();
        _editDelay = new();
        _editSave = SaveEditsAfterDelayAsync(_editDelay);
    }
    private async Task SaveEditsAfterDelayAsync(CancellationTokenSource delay)
    {
        try { await Task.Delay(350, delay.Token); await FlushEditsAsync(); }
        catch (OperationCanceledException) when (delay.IsCancellationRequested) { }
        finally { if (ReferenceEquals(_editDelay, delay)) _editDelay = null; delay.Dispose(); }
    }
    public async Task FlushEditsAsync()
    {
        _editDelay?.Cancel();
        if (_pendingEdits.Count == 0) return;
        var edits = _pendingEdits.ToArray(); _pendingEdits.Clear();
        foreach (var (key, _) in edits) _savingEdits[key] = _savingEdits.GetValueOrDefault(key) + 1;
        try { await UpdateAsync(settings => edits.Aggregate(settings, (current, edit) => edit.Value(current))); }
        finally
        {
            foreach (var (key, _) in edits)
            {
                if (_savingEdits[key] > 1) --_savingEdits[key]; else _savingEdits.Remove(key);
            }
            // A settings transaction can notify while its predecessor is still
            // saving. Refresh once ownership retires, including a real failure
            // rollback, but keep newer queued or in-flight values protected.
            OnPropertyChanged(nameof(Settings));
        }
    }
    public AppSettings Settings => _main.Settings;
    public string Error => _error.Render(_strings);
    private AppUiMessage ErrorMessage { set => SetProperty(ref _error, value, nameof(Error)); }
    public async Task<bool> CheckNotificationAccessAsync(bool enabled)
    {
        if (!enabled) return true;
        try
        {
            var access = await _notifications.RequestAccessAsync(_stop.Token);
            if (access == DropSpace.Core.SystemActivities.NotificationAccessState.Allowed) return true;
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return false; }
        catch (Exception exception) { _logger.LogWarning("Notification permission failed ({Category}).", exception.GetType().Name); }
        ErrorMessage = AppUiMessage.Resource("NotificationAccessUnavailable");
        return false;
    }
    public async Task<bool> UpdateAsync(Func<AppSettings, AppSettings> change)
    {
        var entered = false;
        try
        {
            await _save.WaitAsync(_stop.Token); entered = true;
            ErrorMessage = AppUiMessage.Empty;
            await _main.UpdateSettingsAsync(change(Settings), _stop.Token);
            return true;
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return false; }
        catch (Exception exception)
        {
            _logger.LogWarning("Settings edit failed ({Category}).", exception.GetType().Name);
            ErrorMessage = AppUiMessage.Resource("NativeSettingsSaveFailed");
            OnPropertyChanged(nameof(Settings)); return false;
        }
        finally { if (entered) _save.Release(); }
    }
    public async Task PickLyricsFolderAsync(nint windowHandle)
    {
        try
        {
            if (await _folders.PickAsync(windowHandle) is { } path)
                await UpdateAsync(settings => settings with { Lyrics = settings.Lyrics with { LocalLrcDirectory = path } });
        }
        catch (Exception exception)
        { _logger.LogWarning("Lyrics folder picker failed ({Category}).", exception.GetType().Name); ErrorMessage = AppUiMessage.Resource("NativeSettingsSaveFailed"); }
    }
    private void OnChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainViewModel.Settings)) OnPropertyChanged(nameof(Settings));
        if (args.PropertyName == nameof(MainViewModel.Language)) OnPropertyChanged(nameof(Error));
    }
    public async ValueTask DisposeAsync()
    { await FlushEditsAsync(); await _editSave; await FlushDownloadLimitsAsync(); await _downloadSave; _main.PropertyChanged -= OnChanged; _stop.Cancel(); await _save.WaitAsync(); _save.Release(); }
}
