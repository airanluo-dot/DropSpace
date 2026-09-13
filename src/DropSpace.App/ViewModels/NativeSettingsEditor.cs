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
    private readonly SemaphoreSlim _save = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private string _error = string.Empty;
    public NativeSettingsEditor(MainViewModel main, IAppStringLocalizer strings, ILogger<NativeSettingsEditor> logger, NativeFolderPickerService folders)
    { _main = main; _strings = strings; _logger = logger; _folders = folders; main.PropertyChanged += OnChanged; }
    public AppSettings Settings => _main.Settings;
    public string Error { get => _error; private set => SetProperty(ref _error, value); }
    public async Task<bool> UpdateAsync(Func<AppSettings, AppSettings> change)
    {
        var entered = false;
        try
        {
            await _save.WaitAsync(_stop.Token); entered = true;
            Error = string.Empty;
            await _main.UpdateSettingsAsync(change(Settings), _stop.Token);
            return true;
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return false; }
        catch (Exception exception)
        {
            _logger.LogWarning("Settings edit failed ({Category}).", exception.GetType().Name);
            Error = _strings.Get("NativeSettingsSaveFailed");
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
        { _logger.LogWarning("Lyrics folder picker failed ({Category}).", exception.GetType().Name); Error = _strings.Get("NativeSettingsSaveFailed"); }
    }
    private void OnChanged(object? sender, PropertyChangedEventArgs args)
    { if (args.PropertyName == nameof(MainViewModel.Settings)) OnPropertyChanged(nameof(Settings)); }
    public async ValueTask DisposeAsync()
    { _main.PropertyChanged -= OnChanged; _stop.Cancel(); await _save.WaitAsync(); _save.Release(); }
}
