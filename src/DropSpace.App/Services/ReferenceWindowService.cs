using DropSpace.App.Views;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using DropSpace.Core.Preview;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DropSpace.App.Services;

/// <summary>
/// Owns one explicit, session-only reference window. Passive refresh never shows or activates it.
/// Only an item identity and the ordinary bounded preview are retained; no content item is copied.
/// </summary>
public sealed class ReferenceWindowService : IDisposable
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".webp",
        ".txt", ".log", ".csv", ".tsv", ".xml", ".yaml", ".yml", ".ini", ".cfg", ".conf", ".env",
        ".cs", ".cpp", ".c", ".h", ".hpp", ".js", ".mjs", ".ts", ".tsx", ".jsx", ".java", ".kt",
        ".swift", ".rs", ".go", ".py", ".rb", ".php", ".sh", ".ps1", ".sql", ".html", ".css",
        ".json", ".md", ".markdown", ".tex", ".toml",
    };
    private readonly IItemRepository _repository;
    private readonly IRecentItemsService _recent;
    private readonly QuickPreviewService _previews;
    private readonly ShellActionService _shell;
    private readonly ClipboardCaptureService _clipboard;
    private readonly IAppStringLocalizer _strings;
    private readonly AppLanguageService _language;
    private readonly ILogger<ReferenceWindowService> _logger;
    private readonly DispatcherQueue _dispatcher;
    private ReferenceWindow? _window;
    private DispatcherQueueTimer? _timer;
    private IDisposable? _protection;
    private CancellationTokenSource? _load;
    private Guid? _itemId;
    private DropItem? _displayedItem;
    private int _generation;
    private bool _refreshing, _copying, _disposed;

    public ReferenceWindowService(IItemRepository repository, IRecentItemsService recent,
        QuickPreviewService previews, ShellActionService shell, ClipboardCaptureService clipboard,
        IAppStringLocalizer strings, AppLanguageService language, ILogger<ReferenceWindowService> logger)
    {
        _repository = repository; _recent = recent; _previews = previews; _shell = shell;
        _clipboard = clipboard; _strings = strings; _language = language; _logger = logger;
        _dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("Reference windows must be owned by the UI dispatcher.");
        _recent.Changed += OnItemsChanged;
        _language.Changed += OnLanguageChanged;
    }

    public Guid? CurrentItemId => _itemId;
    public object GetState() => new { itemId = _itemId, isOpen = _window is not null,
        hasContent = _window?.HasContent ?? false, alwaysOnTop = _window?.IsAlwaysOnTop ?? false,
        zoom = _window?.ZoomFactor, bounds = _window is { } window ? new
        { x=window.AppWindow.Position.X,y=window.AppWindow.Position.Y,width=window.AppWindow.Size.Width,height=window.AppWindow.Size.Height } : null };
    public void Close() => _window?.Close();
    public void SetAlwaysOnTop(bool enabled) => RequireWindow().SetAlwaysOnTop(enabled);
    public void SetZoom(double scale) => RequireWindow().SetZoom(scale);
    public void FitImage() => RequireWindow().FitImage();
    public void SetBounds(int x,int y,int width,int height) => RequireWindow().AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x,y,width,height));
    public void ScrollTo(double horizontal,double vertical) => RequireWindow().ScrollTo(horizontal,vertical);
    private ReferenceWindow RequireWindow()
    {
        if(!_dispatcher.HasThreadAccess)throw new InvalidOperationException("Reference presentation belongs to the UI dispatcher.");
        return _window ?? throw new InvalidOperationException("No reference window is open.");
    }
    internal ReferenceWindow? DiagnosticWindow => _window;

    public static bool CanOpen(DropItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.Kind is ItemKind.Text or ItemKind.Code or ItemKind.Color or ItemKind.Image ||
            (item.File?.EntryKind == FileEntryKind.File &&
             SupportedExtensions.Contains(item.File.Extension ?? Path.GetExtension(item.File.OriginalPath)));
    }

    public async Task OpenAsync(DropItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_dispatcher.HasThreadAccess) throw new InvalidOperationException("Open the reference from its UI owner.");
        if (!CanOpen(item)) return;
        cancellationToken.ThrowIfCancellationRequested();

        // Acquire the next lease first: replacing an object cannot make it eligible for expiry mid-open.
        var nextProtection = _recent.Protect(item.Id);
        CancelLoad();
        _protection?.Dispose(); _protection = nextProtection;
        _itemId = item.Id; _displayedItem = null;
        if (_window is null)
        {
            _window = new ReferenceWindow(_strings);
            _window.Closed += OnWindowClosed; _window.CopyRequested += OnCopyRequested;
            _timer = _dispatcher.CreateTimer(); _timer.Interval = TimeSpan.FromSeconds(2);
            _timer.Tick += OnTimer; _timer.Start();
        }
        _window.SetItemTitle(item.Title); _window.ShowStatus("ReferenceLoading");
        // This is the only activation path, directly following the user's Island action.
        _window.Activate();
        await RefreshCurrentAsync(force: true, cancellationToken);
    }

    private void OnItemsChanged(object? sender, EventArgs args) => QueueRefresh();
    private void OnTimer(DispatcherQueueTimer sender, object args) => QueueRefresh();
    private void QueueRefresh()
    {
        if (_disposed) return;
        _dispatcher.TryEnqueue(async () =>
        {
            if (_disposed || _window is null || _refreshing) return;
            await RefreshCurrentAsync(force: false);
        });
    }

    private void OnLanguageChanged(object? sender, EventArgs args) =>
        _dispatcher.TryEnqueue(() => { if (!_disposed) _window?.RefreshLanguage(); });

    private async Task RefreshCurrentAsync(bool force, CancellationToken cancellationToken = default)
    {
        if (_window is not { } window || _itemId is not { } id || _disposed) return;
        if (_refreshing && !force) return;
        CancelLoad();
        var generation = _generation;
        using var load = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _load = load; _refreshing = true;
        try
        {
            await _recent.RefreshAsync(id, load.Token);
            var item = await _repository.GetAsync(id, load.Token);
            if (!IsCurrent(window, id, generation)) return;
            if (item is null || item.Status != ItemStatus.Available)
            {
                _displayedItem = item; window.ShowStatus("ReferenceUnavailable"); return;
            }
            if (!force && _displayedItem is { } previous && previous.Revision == item.Revision &&
                previous.Status == item.Status && window.HasContent) return;

            var resetPosition = _displayedItem is null;
            window.SetItemTitle(item.Title);
            var preview = await _previews.LoadAsync(item, targetPixelWidth: 2048, inline: false, cancellationToken: load.Token);
            if (!IsCurrent(window, id, generation)) return;
            if (preview.Kind == PreviewKind.Image && preview.Bytes is { Length: > 0 } bytes)
                await window.ShowImageAsync(bytes, resetPosition, load.Token);
            else if (preview.Kind is PreviewKind.Text or PreviewKind.Code or PreviewKind.Json or PreviewKind.Markdown &&
                     preview.Text is { } text)
                window.ShowText(text, resetPosition);
            else window.ShowStatus("ReferenceUnavailable");
            if (IsCurrent(window, id, generation)) _displayedItem = item;
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogWarning("Reference preview failed for {ItemId} ({Category}).", id, exception.GetType().Name);
            if (IsCurrent(window, id, generation)) window.ShowStatus("ReferenceUnavailable");
        }
        finally
        {
            if (_generation == generation) { _refreshing = false; _load = null; }
        }
    }

    private bool IsCurrent(ReferenceWindow window, Guid id, int generation) =>
        !_disposed && _window == window && _itemId == id && _generation == generation;

    private async void OnCopyRequested(object? sender, EventArgs args)
    {
        if (_copying || _window is not { } window || _itemId is not { } id) return;
        _copying = true;
        try
        {
            await RefreshCurrentAsync(force: true);
            if (_window != window || _itemId != id || !window.HasContent || _displayedItem is not { } item) return;
            if (window.CurrentText is { } text) await _clipboard.CopyTextAsync(text);
            else await _shell.CopyAsync(item);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _logger.LogWarning("Reference copy failed for {ItemId} ({Category}).", id, exception.GetType().Name);
            if (_window == window && _itemId == id) window.ShowCopyFailure();
        }
        finally { _copying = false; }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (sender is ReferenceWindow window)
        { window.Closed -= OnWindowClosed; window.CopyRequested -= OnCopyRequested; }
        _window = null; CancelLoad();
        if (_timer is not null) { _timer.Stop(); _timer.Tick -= OnTimer; _timer = null; }
        _protection?.Dispose(); _protection = null;
        _itemId = null; _displayedItem = null;
    }

    private void CancelLoad()
    {
        _generation++; _load?.Cancel(); _load = null; _refreshing = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _recent.Changed -= OnItemsChanged; _language.Changed -= OnLanguageChanged;
        _window?.Close(); CancelLoad();
        _protection?.Dispose(); _protection = null;
    }
}
