using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.ComponentModel;
using DropSpace.App.Services;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;

namespace DropSpace.App.ViewModels;

public sealed class ClipboardIslandViewModel : ObservableObject, IAsyncDisposable
{
    private readonly MainViewModel _main;
    private readonly ClipboardCaptureService _capture;
    private readonly IAppStringLocalizer _strings;
    private readonly ILogger<ClipboardIslandViewModel> _logger;
    private readonly DispatcherQueue _dispatcher;
    private readonly HashSet<object> _owners = [];
    private readonly Channel<bool> _refresh = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private bool _visible, _disposed;
    private string _error = string.Empty;
    public ClipboardIslandViewModel(MainViewModel main, ClipboardCaptureService capture, IAppStringLocalizer strings, ILogger<ClipboardIslandViewModel> logger, DispatcherQueue dispatcher)
    {
        _main = main; _capture = capture; _strings = strings; _logger = logger; _dispatcher = dispatcher;
        _main.PropertyChanged += OnMainChanged; _capture.ItemCaptured += OnCaptured; _worker = RunAsync();
    }
    public ObservableCollection<ItemCardViewModel> Items { get; } = [];
    public string Error { get => _error; private set => SetProperty(ref _error, value); }
    public bool Empty => Items.Count == 0;
    public string PauseLabel => _strings.Get(_main.IsClipboardPaused ? "ClipboardIslandResume" : "ClipboardIslandPause");
    public void SetVisible(object owner, bool visible)
    {
        if (_disposed || !(visible ? _owners.Add(owner) : _owners.Remove(owner))) return;
        Volatile.Write(ref _visible, _owners.Count > 0);
        if (_visible) _refresh.Writer.TryWrite(true);
    }
    public Task CopyAsync(ItemCardViewModel card) => ExecuteAsync(() => _main.CopyAsync(card));
    public Task PinAsync(ItemCardViewModel card) => ExecuteAsync(() => _main.TogglePinAsync(card));
    public Task RemoveAsync(ItemCardViewModel card) => ExecuteAsync(() => _main.RemoveAsync(card));
    public Task TogglePauseAsync() => ExecuteAsync(() => _main.SetClipboardPausedAsync(!_main.IsClipboardPaused));
    public Task OpenMainAsync() => _main.NavigateAsync("Clipboard");
    private async Task ExecuteAsync(Func<Task> action)
    {
        try { Error = string.Empty; await action(); _refresh.Writer.TryWrite(true); }
        catch (Exception exception) { _logger.LogWarning("Clipboard island action failed ({Category}).", exception.GetType().Name); Error = _strings.Get("ClipboardIslandActionFailed"); }
    }
    private void OnCaptured(object? sender, DropItem item) { if (Volatile.Read(ref _visible)) _refresh.Writer.TryWrite(true); }
    private void OnMainChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(MainViewModel.Settings)) OnPropertyChanged(nameof(PauseLabel));
        if (_visible && args.PropertyName is nameof(MainViewModel.StatusMessage) or nameof(MainViewModel.Settings)) _refresh.Writer.TryWrite(true);
    }
    private async Task RunAsync()
    {
        try
        {
            await foreach (var _ in _refresh.Reader.ReadAllAsync(_stop.Token))
            {
                if (!_visible) continue;
                try
                {
                    var items = await _main.GetRecentSourceItemsAsync(ItemSource.Clipboard, 20, _stop.Token);
                    await _dispatcher.EnqueueAsync(() =>
                    {
                        if (!_disposed && _visible) { Items.Clear(); foreach (var item in items) Items.Add(item); OnPropertyChanged(nameof(Empty)); }
                        return Task.CompletedTask;
                    });
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogWarning("Clipboard island refresh failed ({Category}).", exception.GetType().Name);
                    await _dispatcher.EnqueueAsync(() => { if (!_disposed && _visible) Error = _strings.Get("ClipboardIslandActionFailed"); return Task.CompletedTask; });
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true; _main.PropertyChanged -= OnMainChanged; _capture.ItemCaptured -= OnCaptured;
        _stop.Cancel(); _refresh.Writer.TryComplete(); await _worker; _stop.Dispose();
    }
}
