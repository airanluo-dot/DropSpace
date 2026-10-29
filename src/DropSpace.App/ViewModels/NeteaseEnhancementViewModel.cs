using CommunityToolkit.Mvvm.ComponentModel;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Media;
using Microsoft.UI.Dispatching;

namespace DropSpace.App.ViewModels;

public sealed class NeteaseEnhancementViewModel : ObservableObject, IDisposable
{
    private readonly INeteaseEnhancementService _service;
    private readonly IAppStringLocalizer _strings;
    private readonly DispatcherQueue _dispatcher;
    private NeteaseEnhancementState _state;
    private bool _disposed;
    private bool _requestPending;
    private bool _openingChecked;
    private bool _checkingUpdate;
    private string? _installedVersion, _latestVersion;
    private string? _updateMessageKey;
    private readonly CancellationTokenSource _updateStop = new();
    private readonly Func<string?> _readInstalled;
    private readonly Func<CancellationToken, Task<string?>> _readLatest;
    private CancellationTokenSource? _operationStop;

    public NeteaseEnhancementViewModel(INeteaseEnhancementService service, IAppStringLocalizer strings, DispatcherQueue dispatcher)
        : this(service, strings, dispatcher, Services.NeteaseEnhancement.NeteaseUpdateProbe.ReadInstalledVersion,
            Services.NeteaseEnhancement.NeteaseUpdateProbe.ReadLatestVersionAsync) { }

    internal NeteaseEnhancementViewModel(INeteaseEnhancementService service, IAppStringLocalizer strings,
        DispatcherQueue dispatcher, Func<string?> readInstalled, Func<CancellationToken, Task<string?>> readLatest)
    {
        _readInstalled = readInstalled; _readLatest = readLatest;
        _service = service; _strings = strings; _dispatcher = dispatcher;
        _state = service.Current;
        service.Changed += OnChanged;
    }

    public bool IsBusy => _requestPending || _state.IsBusy;
    public bool IsEnhanced => _state.Stage is NeteaseEnhancementStage.Installed or NeteaseEnhancementStage.Enhanced;
    public bool IsManaged => _state.IsManaged;
    public string InstalledVersion => _installedVersion is { } version && IsEnhanced
        ? _strings.Format("NeteaseInstalledVersion", version) : string.Empty;
    public bool HasUpdate => IsEnhanced && Version.TryParse(_installedVersion, out var installed)
        && Version.TryParse(_latestVersion, out var latest) && latest > installed;
    public string UpdateStatus => HasUpdate ? _strings.Format("NeteaseUpdateAvailable", _latestVersion!)
        : _updateMessageKey is { } key ? _strings.Get(key) : string.Empty;
    public bool IsCheckingUpdate => _checkingUpdate;

    public Task CheckOnFirstOpenAsync()
    {
        if (_openingChecked || _disposed) return Task.CompletedTask;
        _openingChecked = true;
        return CheckUpdateAsync(silent: true);
    }

    public async Task CheckUpdateAsync(bool silent = false)
    {
        if (_checkingUpdate || _disposed) return;
        _checkingUpdate = true;
        _updateMessageKey = silent ? null : "NeteaseUpdateChecking";
        OnPropertyChanged(string.Empty);
        try
        {
            await _service.InspectAsync(_updateStop.Token);
            _state = _service.Current;
            _installedVersion = await Task.Run(_readInstalled);
            if (_installedVersion is null) { _updateMessageKey = null; return; }
            _latestVersion = await _readLatest(_updateStop.Token);
            _updateMessageKey = silent ? null : _latestVersion is null ? "NeteaseUpdateUnavailable" : "NeteaseUpdateCurrent";
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            _updateMessageKey = silent ? null : "NeteaseUpdateUnavailable";
        }
        finally
        {
            _checkingUpdate = false;
            if (!_disposed) OnPropertyChanged(string.Empty);
        }
    }
    public bool CanCancel => _operationStop is { IsCancellationRequested: false };
    public bool IsCancelableOperation => _operationStop is not null;
    public string Status => _operationStop is { IsCancellationRequested: true } ? _strings.Get("DlcCanceling") : _strings.Get(_state.Stage switch
    {
        NeteaseEnhancementStage.Detecting => "NeteaseEnhancementDetecting",
        NeteaseEnhancementStage.Preparing => "NeteaseEnhancementPreparing",
        NeteaseEnhancementStage.Installing => "NeteaseEnhancementInstalling",
        NeteaseEnhancementStage.Restarting => "NeteaseEnhancementRestarting",
        NeteaseEnhancementStage.Verifying => "NeteaseEnhancementVerifying",
        NeteaseEnhancementStage.Installed => "NeteaseEnhancementInstalled",
        NeteaseEnhancementStage.Enhanced => "NeteaseEnhancementEnhanced",
        NeteaseEnhancementStage.Removing => "NeteaseEnhancementRemoving",
        NeteaseEnhancementStage.Removed => "NeteaseEnhancementRemoved",
        NeteaseEnhancementStage.Failed => "NeteaseEnhancementFailed",
        _ => "NeteaseEnhancementNotInstalled",
    });
    public string Error => _state.ErrorCode is null ? string.Empty : _strings.Get(_state.ErrorCode switch
    {
        "NotFound" => "NeteaseEnhancementErrorNotFound",
        "Verification" => "NeteaseEnhancementErrorVerification",
        "Permission" => "NeteaseEnhancementErrorPermission",
        "Conflict" => "NeteaseEnhancementErrorConflict",
        "Integrity" => "NeteaseEnhancementErrorIntegrity",
        "Network" => "NeteaseEnhancementErrorNetwork",
        "Runtime" => "NeteaseEnhancementErrorRuntime",
        "Rollback" => "NeteaseEnhancementErrorRollback",
        "Canceled" => "NeteaseEnhancementErrorCanceled",
        _ => "NeteaseEnhancementErrorUnexpected",
    });
    public Task InspectAsync() => ExecuteAsync(() => _service.InspectAsync());
    public Task EnhanceAsync(bool reinstall = false) => ExecuteAsync(async () =>
    {
        using var stop = new CancellationTokenSource();
        _operationStop = stop;
        OnPropertyChanged(string.Empty);
        try { await _service.EnhanceAsync(reinstall, stop.Token); }
        finally { _operationStop = null; }
    });
    public Task RemoveAsync() => ExecuteAsync(() => _service.RemoveAsync());
    public void Cancel()
    {
        _operationStop?.Cancel();
        OnPropertyChanged(string.Empty);
    }

    private async Task ExecuteAsync(Func<Task> action)
    {
        if (_requestPending) return;
        _requestPending = true;
        OnPropertyChanged(string.Empty);
        try { await action(); }
        finally
        {
            _requestPending = false;
            _state = _service.Current;
            _installedVersion = _readInstalled();
            if (!_disposed) OnPropertyChanged(string.Empty);
        }
    }

    private void OnChanged(object? sender, NeteaseEnhancementState state)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (_disposed) return;
            // A queued stage notification must not overwrite a newer completed operation.
            _state = _service.Current;
            if (!_state.IsBusy) _installedVersion = _readInstalled();
            OnPropertyChanged(string.Empty);
        });
    }

    public void Dispose()
    {
        _disposed = true;
        _updateStop.Cancel();
        _service.Changed -= OnChanged;
    }
}
