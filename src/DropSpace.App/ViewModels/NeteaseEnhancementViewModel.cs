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
    private CancellationTokenSource? _operationStop;

    public NeteaseEnhancementViewModel(INeteaseEnhancementService service, IAppStringLocalizer strings, DispatcherQueue dispatcher)
    {
        _service = service; _strings = strings; _dispatcher = dispatcher;
        _state = service.Current;
        service.Changed += OnChanged;
    }

    public bool IsBusy => _requestPending || _state.IsBusy;
    public bool IsEnhanced => _state.Stage is NeteaseEnhancementStage.Installed or NeteaseEnhancementStage.Enhanced;
    public bool IsManaged => _state.IsManaged;
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
            OnPropertyChanged(string.Empty);
        });
    }

    public void Dispose()
    {
        _disposed = true;
        _service.Changed -= OnChanged;
    }
}
