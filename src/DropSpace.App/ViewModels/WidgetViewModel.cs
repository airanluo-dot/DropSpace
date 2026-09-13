using System.ComponentModel;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DropSpace.Core.Abstractions;
using Microsoft.Extensions.Logging;
using DropSpace.App.Services;
using DropSpace.App.Services.Widgets;
using DropSpace.Core.Widgets;
using Microsoft.UI.Dispatching;

namespace DropSpace.App.ViewModels;

/// <summary>Reference-counted visible surfaces own sampling; widget data never requests presence.</summary>
public sealed class WidgetViewModel : ObservableObject, IAsyncDisposable
{
    private readonly MainViewModel _main;
    private readonly NativeWidgetDataService _data;
    private readonly DispatcherQueue _dispatcher;
    private readonly HashSet<object> _visibleOwners = [];
    private readonly Channel<bool> _visibility = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Task _worker;
    private WidgetDataSnapshot? _snapshot;
    private bool _disposed;
    private readonly System.Diagnostics.Stopwatch _stopwatch = new();
    private readonly IAppStringLocalizer _strings;
    public WidgetViewModel(MainViewModel main, NativeWidgetDataService data, DispatcherQueue dispatcher, IAppStringLocalizer strings, ILogger<WidgetViewModel> logger)
    {
        _main = main; _data = data; _dispatcher = dispatcher;
        _strings = strings;
        StopwatchToggleCommand = new RelayCommand(() => { if (_stopwatch.IsRunning) _stopwatch.Stop(); else _stopwatch.Start(); OnPropertyChanged(nameof(StopwatchText)); });
        StopwatchResetCommand = new RelayCommand(() => { _stopwatch.Reset(); OnPropertyChanged(nameof(StopwatchText)); });
        ClipboardPauseCommand = new AsyncRelayCommand(async () =>
        {
            try { await _main.SetClipboardPausedAsync(!_main.IsClipboardPaused); }
            catch (Exception exception) { logger.LogWarning("Widget clipboard action failed ({Category}).", exception.GetType().Name); }
        });
        main.PropertyChanged += OnSettingsChanged; data.Changed += OnDataChanged;
        _worker = RunAsync();
    }
    public WidgetLayout Layout => _main.Settings.Widgets.Layout;
    public bool Enabled => _main.Settings.Widgets.Enabled;
    public string StopwatchText => $"{(int)_stopwatch.Elapsed.TotalMinutes:00}:{_stopwatch.Elapsed.Seconds:00}";
    public string StopwatchActionLabel => _strings.Get(_stopwatch.IsRunning ? "StopwatchPause" : "StopwatchStart");
    public string ClipboardPauseLabel => _strings.Get(_main.IsClipboardPaused ? "ClipboardIslandResume" : "ClipboardIslandPause");
    public bool ClipboardPaused => _main.IsClipboardPaused;
    public IRelayCommand StopwatchToggleCommand { get; }
    public IRelayCommand StopwatchResetCommand { get; }
    public IAsyncRelayCommand ClipboardPauseCommand { get; }
    public Task OpenSettingsAsync() => _main.NavigateAsync("Settings");
    public WidgetDataSnapshot? Snapshot { get => _snapshot; private set => SetProperty(ref _snapshot, value); }
    public void SetVisible(object owner, bool visible)
    {
        if (_disposed) return;
        if (!(visible ? _visibleOwners.Add(owner) : _visibleOwners.Remove(owner))) return;
        _visibility.Writer.TryWrite(Enabled && _visibleOwners.Count > 0);
    }
    private async Task RunAsync()
    {
        await foreach (var visible in _visibility.Reader.ReadAllAsync().ConfigureAwait(false))
            await _data.SetVisibleAsync(visible).ConfigureAwait(false);
    }
    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(MainViewModel.Settings)) return;
        OnPropertyChanged(nameof(Layout)); OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(ClipboardPauseLabel));
        _visibility.Writer.TryWrite(Enabled && _visibleOwners.Count > 0);
    }
    private void OnDataChanged(object? sender, WidgetDataSnapshot snapshot) => _dispatcher.TryEnqueue(() => { if (!_disposed) { Snapshot = snapshot; OnPropertyChanged(nameof(StopwatchText)); } });
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true; _main.PropertyChanged -= OnSettingsChanged; _data.Changed -= OnDataChanged;
        _visibility.Writer.TryComplete(); await _worker.ConfigureAwait(false);
        await _data.SetVisibleAsync(false).ConfigureAwait(false);
    }
}
