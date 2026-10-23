using System.ComponentModel;
using DropSpace.App.Services;
using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using DropSpace.Core.Preview;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace DropSpace.App.Views.Island;

public sealed partial class QuickRetrievalView : UserControl
{
    private IslandRetrievalViewModel? _viewModel;
    private IAppStringLocalizer? _strings;
    private bool _composing, _dragging, _active;
    private ItemCardViewModel? _observedCard;
    private bool _loadingPreview;
    private long _compositionEndedAt, _previewGeneration;
    public event EventHandler? BackRequested;
    public event EventHandler? ItemTaken;
    public bool IsComposing => _composing || Environment.TickCount64 - _compositionEndedAt < 80;

    public QuickRetrievalView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, new KeyEventHandler(OnKeyDown), true);
        Unloaded += (_, _) => SetActive(false);
    }

    public void Initialize(IslandRetrievalViewModel viewModel, IAppStringLocalizer strings)
    {
        _viewModel = viewModel; _strings = strings; DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelChanged;
        ObserveSelectedCard(); RefreshLanguage();
    }
    public void RefreshLanguage() => XamlResourceOverride.ApplyTree(this);
    public void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        if (!active)
        {
            ++_previewGeneration;
            if (_composing) { _composing = false; _viewModel?.EndInteraction(); }
            // A native drag retains its freeze until DragItemsCompleted.
        }
        _viewModel?.SetVisible(this, active);
    }
    public bool FocusSearch() => QueryBox.Focus(FocusState.Keyboard);
    internal TextBox DiagnosticSearchBox => QueryBox;
    internal ListView DiagnosticResults => Results;
    internal void DiagnosticSetComposition(bool composing)
    {
        if (composing) BeginComposition(); else EndComposition();
    }
    internal void DiagnosticMoveSelection(int delta) { if (!IsComposing) MoveSelection(delta); }
    internal Task DiagnosticCopyAsync() => CopyAsync(false);
    internal DataPackage? DiagnosticBuildDragData()
    {
        var data = new DataPackage();
        return _viewModel?.SelectedItem is { } selected && TryPopulateDragData(data, [selected]) ? data : null;
    }
    public void ResetPreview() { ++_previewGeneration; PreviewPanel.Visibility = Visibility.Collapsed; PreviewImage.Source = null; PreviewText.Text = string.Empty; }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(IslandRetrievalViewModel.Query) or nameof(IslandRetrievalViewModel.Filter))
        { ResetPreview(); return; }
        if (args.PropertyName != nameof(IslandRetrievalViewModel.SelectedItem)) return;
        ResetPreview(); ObserveSelectedCard();
    }
    private void ObserveSelectedCard()
    {
        if (_observedCard is not null) _observedCard.PropertyChanged -= OnCardChanged;
        _observedCard = _viewModel?.SelectedItem;
        if (_observedCard is not null) _observedCard.PropertyChanged += OnCardChanged;
    }
    private void OnCardChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(ItemCardViewModel.Item) && _active && !_loadingPreview && PreviewPanel.Visibility == Visibility.Visible)
            OnPreview(this, new RoutedEventArgs());
    }
    public void Shutdown()
    {
        SetActive(false);
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelChanged;
        if (_observedCard is not null) _observedCard.PropertyChanged -= OnCardChanged;
        _observedCard = null;
    }

    private void OnCompositionStarted(TextBox sender, TextCompositionStartedEventArgs args)
        => BeginComposition();
    private void OnCompositionEnded(TextBox sender, TextCompositionEndedEventArgs args)
        => EndComposition();
    private void BeginComposition() { _composing = true; _viewModel?.BeginInteraction(); }
    private void EndComposition() { _composing = false; _compositionEndedAt = Environment.TickCount64; _viewModel?.EndInteraction(); }
    private async void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (!_active || IsComposing || _viewModel is not { } view) return;
        if (args.Key == VirtualKey.Escape)
        {
            args.Handled = true;
            if (PreviewPanel.Visibility == Visibility.Visible) ResetPreview();
            else if (QueryBox.Text.Length > 0) QueryBox.Text = string.Empty;
            else BackRequested?.Invoke(this, EventArgs.Empty);
        }
        else if (args.Key is VirtualKey.Up or VirtualKey.Down &&
                 (ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), QueryBox) || IsResultsFocused()))
        {
            // A focused ListView already owns arrow navigation. Observe handled
            // events for TextBox/IME only; never advance a list selection twice.
            if (args.Handled && IsResultsFocused()) return;
            args.Handled = true;
            MoveSelection(args.Key == VirtualKey.Down ? 1 : -1);
        }
        else if (args.Key == VirtualKey.Enter &&
                 (ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), QueryBox) || IsResultsFocused()))
        { args.Handled = true; await CopyAsync(false); }
    }
    private void MoveSelection(int delta)
    {
        if (_viewModel is not { } view || view.Items.Count == 0) return;
        var current = view.SelectedItem is { } selected ? view.Items.IndexOf(selected) : -1;
        view.SelectedItem = view.Items[Math.Clamp(current + delta, 0, view.Items.Count - 1)];
        Results.ScrollIntoView(view.SelectedItem);
    }
    private bool IsResultsFocused()
    {
        var current = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        while (current is not null)
        { if (ReferenceEquals(current, Results)) return true; current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current); }
        return false;
    }
    private void OnBack(object sender, RoutedEventArgs args)
    { if (PreviewPanel.Visibility == Visibility.Visible) ResetPreview(); else BackRequested?.Invoke(this, EventArgs.Empty); }
    private void SelectFilter(int filter)
    {
        ResetPreview();
        AllFilter.IsChecked = filter == 0; RecentFilter.IsChecked = filter == 1; PinnedFilter.IsChecked = filter == 2;
        if (_viewModel is not null) _viewModel.Filter = filter;
    }
    private void OnAll(object sender, RoutedEventArgs args) => SelectFilter(0);
    private void OnRecent(object sender, RoutedEventArgs args) => SelectFilter(1);
    private void OnPinned(object sender, RoutedEventArgs args) => SelectFilter(2);
    private async Task CopyAsync(bool pathOnly)
    {
        if (_viewModel is not { } view) return;
        var generation = _previewGeneration;
        var selectedId = view.SelectedItem?.Id;
        if (await view.CopyAsync(pathOnly) && _active && generation == _previewGeneration && selectedId == view.SelectedItem?.Id)
            ItemTaken?.Invoke(this, EventArgs.Empty);
    }
    private async void OnCopy(object sender, RoutedEventArgs args) => await CopyAsync(false);
    private async void OnCopyPath(object sender, RoutedEventArgs args) => await CopyAsync(true);
    private async void OnKeep(object sender, RoutedEventArgs args) { if (_viewModel is { } view) await view.KeepAsync(); }
    private async void OnRemove(object sender, RoutedEventArgs args) { ResetPreview(); if (_viewModel is { } view) await view.RemoveAsync(); }
    private async void OnReference(object sender, RoutedEventArgs args) { if (_viewModel is { } view) await view.OpenReferenceAsync(); }
    private async void OnPreview(object sender, RoutedEventArgs args)
    {
        if (_viewModel is not { } view) return;
        var generation = ++_previewGeneration;
        var selectedId = view.SelectedItem?.Id;
        _loadingPreview = true;
        PreviewDescriptor? descriptor;
        try { descriptor = await view.PreviewAsync(); }
        finally { _loadingPreview = false; }
        if (!_active || generation != _previewGeneration || selectedId != view.SelectedItem?.Id) return;
        if (descriptor is null)
        {
            PreviewImage.Source = null;
            PreviewText.Text = _strings?.Get("RetrievalUnavailable") ?? string.Empty;
            PreviewPanel.Visibility = Visibility.Visible;
            return;
        }
        PreviewImage.Source = null;
        PreviewText.Text = descriptor.Text ?? _strings?.Get("PreviewUnavailable") ?? string.Empty;
        if (descriptor.Kind == PreviewKind.Image && descriptor.Bytes is { Length: > 0 } bytes)
        {
            try
            {
                using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                using (var writer = new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0)))
                { writer.WriteBytes(bytes); await writer.StoreAsync(); writer.DetachStream(); }
                stream.Seek(0); var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream);
                if (!_active || generation != _previewGeneration || selectedId != view.SelectedItem?.Id) return;
                PreviewImage.Source = bitmap; PreviewText.Text = string.Empty;
            }
            catch (Exception exception) when (exception is IOException or System.Runtime.InteropServices.COMException or ArgumentException)
            { PreviewText.Text = _strings?.Get("PreviewUnavailable") ?? string.Empty; }
        }
        PreviewPanel.Visibility = Visibility.Visible;
    }
    private void OnDragStarting(object sender, DragItemsStartingEventArgs args)
    {
        if (_viewModel is not { } view) { args.Cancel = true; return; }
        var cards = args.Items.OfType<ItemCardViewModel>().ToArray();
        if (!TryPopulateDragData(args.Data, cards)) { args.Cancel = true; return; }
        _dragging = true; view.BeginInteraction();
    }
    private bool TryPopulateDragData(DataPackage data, IReadOnlyList<ItemCardViewModel> cards)
    {
        if (cards.Count != 1 || cards[0].Item.Status != ItemStatus.Available) return false;
        var card = cards[0];
        // Recheck the exact snapshot at the gesture. A metadata-only existence
        // check would allow a still-open writer or an overwrite before its FSW event.
        // This short read lease denies concurrent writers; it never mutates the file.
        if (card.Item.File is { } file && !CanDragCurrentFile(file))
        { _viewModel?.RequestRefresh(); return false; }
        if (card.DragStorageItem is { } storage) data.SetStorageItems([storage], readOnly: true);
        else if (card.Item.Text?.InlineText is { } text) data.SetText(text);
        else return false;
        data.RequestedOperation = DataPackageOperation.Copy;
        return true;
    }
    private static bool CanDragCurrentFile(FileReference file)
    {
        if (file.EntryKind == FileEntryKind.Folder) return Directory.Exists(file.OriginalPath);
        try
        {
            using var lease = new FileStream(file.OriginalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return (file.KnownSize is null || lease.Length == file.KnownSize) &&
                (file.KnownModifiedAtUtc is null || File.GetLastWriteTimeUtc(file.OriginalPath) == file.KnownModifiedAtUtc.Value.UtcDateTime);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        { return false; }
    }
    private void OnDragCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    { if (_dragging) { _dragging = false; _viewModel?.EndInteraction(); } }
}
