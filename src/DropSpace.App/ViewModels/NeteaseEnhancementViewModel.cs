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

    public NeteaseEnhancementViewModel(INeteaseEnhancementService service, IAppStringLocalizer strings, DispatcherQueue dispatcher)
    {
        _service = service; _strings = strings; _dispatcher = dispatcher;
        _state = service.Current;
        service.Changed += OnChanged;
    }

    public bool IsBusy => _state.IsBusy;
    public bool IsEnhanced => _state.Stage == NeteaseEnhancementStage.Enhanced;
    public bool IsManaged => _state.IsManaged;
    public string Status => _strings.Get(_state.Stage switch
    {
        NeteaseEnhancementStage.Detecting => "NeteaseEnhancementDetecting",
        NeteaseEnhancementStage.Preparing => "NeteaseEnhancementPreparing",
        NeteaseEnhancementStage.Installing => "NeteaseEnhancementInstalling",
        NeteaseEnhancementStage.Restarting => "NeteaseEnhancementRestarting",
        NeteaseEnhancementStage.Verifying => "NeteaseEnhancementVerifying",
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
    public Task InspectAsync() => _service.InspectAsync();
    public Task EnhanceAsync(bool reinstall = false) => _service.EnhanceAsync(reinstall);
    public Task RemoveAsync() => _service.RemoveAsync();

    private void OnChanged(object? sender, NeteaseEnhancementState state)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (_disposed) return;
            _state = state;
            OnPropertyChanged(string.Empty);
        });
    }

    public void Dispose()
    {
        _disposed = true;
        _service.Changed -= OnChanged;
    }
}
