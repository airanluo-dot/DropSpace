using System.ComponentModel;
using System.Threading.Channels;
using CommunityToolkit.Mvvm.ComponentModel;
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
    public WidgetViewModel(MainViewModel main, NativeWidgetDataService data, DispatcherQueue dispatcher)
    {
        _main = main; _data = data; _dispatcher = dispatcher;
        main.PropertyChanged += OnSettingsChanged; data.Changed += OnDataChanged;
        _worker = RunAsync();
    }
    public WidgetLayout Layout => _main.Settings.Widgets.Layout;
    public bool Enabled => _main.Settings.Widgets.Enabled;
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
        _visibility.Writer.TryWrite(Enabled && _visibleOwners.Count > 0);
    }
    private void OnDataChanged(object? sender, WidgetDataSnapshot snapshot) => _dispatcher.TryEnqueue(() => { if (!_disposed) Snapshot = snapshot; });
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true; _main.PropertyChanged -= OnSettingsChanged; _data.Changed -= OnDataChanged;
        _visibility.Writer.TryComplete(); await _worker.ConfigureAwait(false);
        await _data.SetVisibleAsync(false).ConfigureAwait(false);
    }
}
