using System.ComponentModel;
using System.Collections.ObjectModel;
using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Downloads;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace DropSpace.App.Views.Settings;

/// <summary>Only owns presentation. Task lifetime and debounced settings saves belong to app services.</summary>
public sealed class DownloadPanel : UserControl
{
    private readonly NativeSettingsEditor _editor;
    private readonly IAppStringLocalizer _strings;
    private readonly nint _window;
    private readonly TextBox _url = new();
    private readonly TextBox _directory = new();
    private readonly TextBox _name = new();
    private readonly TextBox _defaultDirectory = new();
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ListView _tasks;
    private readonly ObservableCollection<TaskRow> _rows = [];
    private readonly Dictionary<Guid, TaskRow> _rowsById = [];
    private int _historyLimit = 50;
    private readonly Slider _connections = new() { Minimum = 1, Maximum = 256, StepFrequency = 1, SmallChange = 1 };
    private readonly NumberBox _connectionsNumber = new() { Minimum = 1, Maximum = 256, SmallChange = 1, Width = 100, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly Slider _speed = new() { Minimum = 0, Maximum = 1024, StepFrequency = 1, SmallChange = 1 };
    private readonly NumberBox _speedNumber = new() { Minimum = 0, Maximum = 1024, SmallChange = 1, Width = 100, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly TextBlock _speedLabel = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _concurrentDownloads = new() { ItemsSource = new[] { 1, 2, 3 }, MinWidth = 100, HorizontalAlignment = HorizontalAlignment.Left };
    private bool _syncing;
    private volatile bool _loaded;
    private volatile bool _active;
    private bool CanRefresh => _loaded && _active;
    private readonly HashSet<uint> _limitPointers = [];
    private string _lastDefaultDirectory;
    private bool _defaultDirectoryDirty;
    private int _defaultDirectoryEditVersion;
    private int _savingDirectories;

    public DownloadPanel(NativeSettingsEditor editor, IAppStringLocalizer strings, nint windowHandle)
    {
        _editor = editor; _strings = strings; _window = windowHandle;
        _tasks = new ListView { SelectionMode = ListViewSelectionMode.None, MaxHeight = 600, IsItemClickEnabled = false };
        _tasks.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><Grid /></DataTemplate>");
        _tasks.ContainerContentChanging += (_, args) =>
        {
            if (args.ItemContainer.ContentTemplateRoot is not Grid host) return;
            var card = host.Children.OfType<TaskCard>().FirstOrDefault();
            if (args.InRecycleQueue) { card?.Unbind(); return; }
            if (args.Item is not TaskRow row) return;
            if (card is null) { card = new TaskCard(this, row.Snapshot); host.Children.Add(card); }
            card.Bind(row);
            args.ItemContainer.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            args.Handled = true;
        };
        _lastDefaultDirectory = editor.DefaultDownloadDirectory;
        var body = new StackPanel { Spacing = 18 };
        var form = new StackPanel { Spacing = 12 };
        form.Children.Add(Heading("DownloadCustom"));
        form.Children.Add(Row("DownloadUrl", _url));
        form.Children.Add(Row("DownloadSaveTo", FolderRow(_directory, false)));
        form.Children.Add(Row("DownloadFileName", _name));
        _name.PlaceholderText = strings.Get("DownloadAutoName");
        var buttons = new DownloadActionPanel();
        var start = Button("DownloadStart", async () =>
        {
            await editor.Downloads.EnqueueAsync(_url.Text, _directory.Text, _name.Text);
            _url.Text = ""; _name.Text = ""; _directory.Text = editor.DefaultDownloadDirectory;
            Render();
        });
        buttons.Children.Add(start);
        buttons.Children.Add(Button("DownloadOpenFolder", () => { editor.OpenDownloadFolder(_directory.Text); return Task.CompletedTask; }));
        form.Children.Add(buttons); form.Children.Add(_error); form.Children.Add(_tasks);
        _tasks.ItemsSource = _rows;
        form.Children.Add(Button("DownloadMoreHistory", () => { _historyLimit += 50; Render(); return Task.CompletedTask; }));
        body.Children.Add(Card(form));
        var settings = new StackPanel { Spacing = 12 };
        settings.Children.Add(Heading("DownloadSettings"));
        settings.Children.Add(Row("DownloadConcurrentCount", _concurrentDownloads));
        settings.Children.Add(Row("DownloadConnections", SliderRow(_connections, _connectionsNumber)));
        settings.Children.Add(new TextBlock { Text = strings.Get("DownloadConnectionsHint"), TextWrapping = TextWrapping.Wrap, Opacity = .72 });
        settings.Children.Add(Row("DownloadSpeed", SliderRow(_speed, _speedNumber)));
        settings.Children.Add(_speedLabel);
        settings.Children.Add(Row("DownloadDefaultDirectory", FolderRow(_defaultDirectory, true)));
        body.Children.Add(Card(settings)); Content = body;
        NameControl(_url, "DownloadUrl"); NameControl(_directory, "DownloadSaveTo"); NameControl(_name, "DownloadFileName");
        NameControl(_defaultDirectory, "DownloadDefaultDirectory");
        NameControl(_concurrentDownloads, "DownloadConcurrentCount");
        NameControl(_connections, "DownloadConnections"); NameControl(_connectionsNumber, "DownloadConnections");
        NameControl(_speed, "DownloadSpeed"); NameControl(_speedNumber, "DownloadSpeed");
        _directory.Text = editor.DefaultDownloadDirectory;
        RefreshSettings(force: true);
        _concurrentDownloads.SelectionChanged += (_, _) =>
        {
            if (_concurrentDownloads.SelectedIndex >= 0)
                LimitsChanged(null, null, _concurrentDownloads.SelectedIndex + 1);
        };
        _connections.ValueChanged += (_, args) => LimitsChanged(args.NewValue, null);
        _speed.ValueChanged += (_, args) => LimitsChanged(null, args.NewValue);
        _connectionsNumber.ValueChanged += (_, args) => LimitsChanged(args.NewValue, null);
        _speedNumber.ValueChanged += (_, args) => LimitsChanged(null, args.NewValue);
        foreach (var slider in new[] { _connections, _speed })
        {
            slider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, args) =>
            {
                var point = args.GetCurrentPoint(slider);
                if (point.IsInContact || point.Properties.IsLeftButtonPressed) _limitPointers.Add(args.Pointer.PointerId);
            }), true);
            slider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(FinishLimitPointer), true);
            slider.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(FinishLimitPointer), true);
            slider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(FinishLimitPointer), true);
            slider.LostFocus += async (_, _) => { if (_limitPointers.Count == 0) await editor.FlushDownloadLimitsAsync(); };
        }
        _connectionsNumber.LostFocus += async (_, _) => await editor.FlushDownloadLimitsAsync();
        _speedNumber.LostFocus += async (_, _) => await editor.FlushDownloadLimitsAsync();
        _defaultDirectory.TextChanged += (_, _) =>
        {
            if (_syncing) return;
            _defaultDirectoryDirty = true;
            ++_defaultDirectoryEditVersion;
        };
        _defaultDirectory.LostFocus += async (_, _) => await SaveDirectoryAsync();
        Loaded += (_, _) =>
        {
            if (_loaded) return; _loaded = true;
            editor.PropertyChanged += SettingsChanged; editor.Downloads.Changed += DownloadsChanged;
            editor.Downloads.TaskChanged += TaskChanged;
            if (CanRefresh) { RefreshSettings(); Render(); }
        };
        Unloaded += async (_, _) =>
        {
            _limitPointers.Clear();
            _loaded = false; editor.PropertyChanged -= SettingsChanged;
            editor.Downloads.Changed -= DownloadsChanged; editor.Downloads.TaskChanged -= TaskChanged;
            await editor.FlushDownloadLimitsAsync();
            await SaveDirectoryAsync();
        };
    }
    internal void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        if (CanRefresh) { RefreshSettings(); Render(); }
    }
    private async void FinishLimitPointer(object sender, PointerRoutedEventArgs args)
    {
        if (!_limitPointers.Remove(args.Pointer.PointerId)) return;
        await _editor.FlushDownloadLimitsAsync();
        RefreshSettings();
    }
    private FrameworkElement FolderRow(TextBox input, bool saveDefault)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.Children.Add(input);
        var select = Button("DownloadChoose", async () =>
        {
            if (await _editor.PickDownloadFolderAsync(_window) is { } path)
            { input.Text = path; if (saveDefault) await SaveDirectoryAsync(); }
        });
        Grid.SetColumn(select, 1); grid.Children.Add(select); return grid;
    }
    private async Task SaveDirectoryAsync()
    {
        if (_syncing) return;
        if (!_defaultDirectoryDirty) { RefreshSettings(); return; }
        var value = _defaultDirectory.Text.Trim();
        if (value == _editor.DefaultDownloadDirectory) { _defaultDirectoryDirty = false; RefreshSettings(); return; }
        var editVersion = _defaultDirectoryEditVersion;
        ++_savingDirectories;
        try { await _editor.UpdateAsync(settings => settings with { DefaultDownloadDirectory = value }); }
        finally
        {
            --_savingDirectories;
            if (editVersion == _defaultDirectoryEditVersion) _defaultDirectoryDirty = false;
            RefreshSettings();
        }
    }
    private void LimitsChanged(double? connections, double? speed, int? concurrentDownloads = null)
    {
        if (_syncing || connections is { } c && !double.IsFinite(c) || speed is { } s && !double.IsFinite(s)) return;
        _syncing = true;
        if (connections is { } count) _connections.Value = _connectionsNumber.Value = Math.Clamp(Math.Round(count), 1, 256);
        if (speed is { } rate) _speed.Value = _speedNumber.Value = Math.Clamp(Math.Round(rate), 0, 1024);
        if (concurrentDownloads is { } tasks) _concurrentDownloads.SelectedIndex = Math.Clamp(tasks, 1, 3) - 1;
        UpdateSpeedLabel();
        _syncing = false;
        // Integer MiB/s throughout: zero = unlimited, one step = 1,048,576 bytes/s.
        _editor.QueueDownloadLimits(connections is not null ? (int)_connections.Value : null,
            speed is not null ? (long)_speed.Value * 1_048_576 : null,
            concurrentDownloads is not null ? _concurrentDownloads.SelectedIndex + 1 : null);
    }
    private void SettingsChanged(object? sender, PropertyChangedEventArgs args)
    { if (args.PropertyName == nameof(NativeSettingsEditor.Settings)) RefreshSettings(); }
    private void RefreshSettings(bool force = false)
    {
        if (!force && !CanRefresh) return;
        _syncing = true;
        var directory = _editor.DefaultDownloadDirectory;
        if (_directory.Text == _lastDefaultDirectory) _directory.Text = directory;
        _lastDefaultDirectory = directory;
        if (_limitPointers.Count == 0 && !_editor.HasPendingDownloadLimits)
        {
            _connections.Value = _connectionsNumber.Value = _editor.Settings.MaxDownloadConnections;
            _concurrentDownloads.SelectedIndex = _editor.Settings.MaxConcurrentDownloads - 1;
            _speed.Value = _speedNumber.Value = _editor.Settings.DownloadSpeedLimitBytesPerSecond / 1_048_576d;
        }
        if (!_defaultDirectoryDirty && _savingDirectories == 0 && _defaultDirectory.FocusState == FocusState.Unfocused)
            _defaultDirectory.Text = directory;
        UpdateSpeedLabel(); _syncing = false;
    }
    private void UpdateSpeedLabel() => _speedLabel.Text = _speed.Value == 0 ? _strings.Get("DownloadUnlimited") : string.Create(_strings.Culture, $"{_speed.Value:0} MiB/s");
    private void Render()
    {
        if (!CanRefresh) return;
        var tasks = _editor.Downloads.GetVisibleTasks(_historyLimit);
        var visible = new HashSet<Guid>();
        for (var index = 0; index < tasks.Count; index++)
        {
            var item = tasks[index];
            visible.Add(item.Id); UpdateRow(item);
            var oldIndex = _rows.IndexOf(_rowsById[item.Id]);
            if (oldIndex != index) _rows.Move(oldIndex, index);
        }
        foreach (var row in _rows.Where(row => !visible.Contains(row.Snapshot.Id)).ToArray())
        { _rows.Remove(row); _rowsById.Remove(row.Snapshot.Id); }
        if (_editor.Downloads.RecoveryError is not null) _error.Text = _strings.Get("DownloadRecoveryWarning");
    }
    private void DownloadsChanged(object? sender, EventArgs args)
    {
        if (!CanRefresh) return;
        DispatcherQueue.TryEnqueue(() => { if (CanRefresh) Render(); });
    }
    private void TaskChanged(object? sender, DownloadTaskSnapshot item)
    {
        if (!CanRefresh) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!CanRefresh) return;
            if (!_rowsById.TryGetValue(item.Id, out var row) || row.Snapshot.State != item.State) Render();
            else row.Update(item);
        });
    }
    private void UpdateRow(DownloadTaskSnapshot item)
    {
        if (_rowsById.TryGetValue(item.Id, out var row)) row.Update(item);
        else { row = new TaskRow(item); _rowsById.Add(item.Id, row); _rows.Add(row); }
    }
    private sealed class TaskRow(DownloadTaskSnapshot snapshot)
    {
        public DownloadTaskSnapshot Snapshot { get; private set; } = snapshot;
        public event Action<DownloadTaskSnapshot>? Changed;
        public void Update(DownloadTaskSnapshot item)
        {
            if (item.UpdatedAt < Snapshot.UpdatedAt || item == Snapshot) return;
            Snapshot = item; Changed?.Invoke(item);
        }
    }
    private TextBlock Heading(string key) => new() { Text = _strings.Get(key), FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private void NameControl(DependencyObject control, string key) { AutomationProperties.SetName(control, _strings.Get(key)); AutomationProperties.SetAutomationId(control, key); }
    private Button Button(string key, Func<Task> action)
    {
        var button = DownloadActionPanel.Create(_strings.Get(key)); NameControl(button, key);
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false; _error.Text = "";
            try { await action(); }
            catch (Exception error) when (error is not OutOfMemoryException)
            { _error.Text = _strings.Get("DownloadError"); }
            finally { button.IsEnabled = true; }
        };
        return button;
    }
    private FrameworkElement Row(string key, FrameworkElement control)
    {
        var row = new StackPanel { Spacing = 4 };
        row.Children.Add(new TextBlock { Text = _strings.Get(key), TextWrapping = TextWrapping.Wrap }); row.Children.Add(control); return row;
    }
    private static Grid SliderRow(Slider slider, NumberBox number)
    {
        var grid = new Grid { ColumnSpacing = 12 }; grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new() { Width = new GridLength(100) });
        grid.Children.Add(slider); Grid.SetColumn(number, 1); grid.Children.Add(number); return grid;
    }
    private static Border Card(UIElement body) => new()
    {
        Padding = new(16), CornerRadius = new(8), BorderThickness = new(1),
        Style = (Style)Application.Current.Resources["DropSpaceCardStyle"],
        Child = body,
    };
    private sealed class TaskCard : UserControl
    {
        private readonly DownloadPanel _owner;
        private readonly TextBlock _name = new() { TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        private readonly TextBlock _detail = new() { TextWrapping = TextWrapping.Wrap };
        private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100 };
        private readonly Button _pause, _resume, _cancel, _retry, _remove;
        private TaskRow? _row;
        private DownloadTaskSnapshot _item;
        public TaskCard(DownloadPanel owner, DownloadTaskSnapshot item)
        {
            _owner = owner; _item = item;
            var body = new StackPanel { Spacing = 8 }; body.Children.Add(_name); body.Children.Add(_detail); body.Children.Add(_progress);
            // A wrapping panel keeps actions reachable at large text scales and compact widths.
            var actions = new DownloadActionPanel();
            _pause = owner.Button("DownloadPause", () => owner._editor.Downloads.PauseAsync(_item.Id));
            _resume = owner.Button("DownloadResume", () => owner._editor.Downloads.ResumeAsync(_item.Id));
            _cancel = owner.Button("DownloadCancel", () => owner._editor.Downloads.CancelAsync(_item.Id));
            _retry = owner.Button("DownloadRetry", () => owner._editor.Downloads.ResumeAsync(_item.Id));
            _remove = owner.Button("DownloadRemoveHistory", () => owner._editor.Downloads.RemoveHistoryAsync(_item.Id));
            foreach (var button in new[] { _pause, _resume, _cancel, _retry, _remove, owner.Button("DownloadOpenFolder", () =>
                { owner._editor.OpenDownloadFolder(Path.GetDirectoryName(_item.OutputPath)!); return Task.CompletedTask; }) })
            { actions.Children.Add(button); }
            body.Children.Add(actions); Content = Card(body);
        }
        public void Unbind() { if (_row is not null) _row.Changed -= Update; _row = null; }
        public void Bind(TaskRow row) { Unbind(); _row = row; row.Changed += Update; Update(row.Snapshot); }
        public void Update(DownloadTaskSnapshot item)
        {
            _item = item; _name.Text = Path.GetFileName(item.OutputPath);
            var total = item.TotalBytes is { } size ? string.Create(_owner._strings.Culture, $"{size / 1_048_576d:0.00} MiB") : _owner._strings.Get("DownloadUnknownSize");
            _detail.Text = _owner._strings.Get("DownloadState" + item.State) + " · " +
                string.Create(_owner._strings.Culture, $"{item.DownloadedBytes / 1_048_576d:0.00} / {total} · {item.BytesPerSecond / 1_048_576d:0.00} MiB/s · ") +
                _owner._strings.Format("DownloadActiveConnections", item.ActiveConnections);
            _progress.IsIndeterminate = item.TotalBytes is null && item.State is DownloadTaskState.Queued or DownloadTaskState.DownloadingFile;
            _progress.Value = item.TotalBytes is > 0 ? Math.Clamp(item.DownloadedBytes * 100d / item.TotalBytes.Value, 0, 100) : 0;
            _pause.Visibility = item.State is DownloadTaskState.Queued or DownloadTaskState.DownloadingFile ? Visibility.Visible : Visibility.Collapsed;
            _resume.Visibility = item.State == DownloadTaskState.Paused ? Visibility.Visible : Visibility.Collapsed;
            _retry.Visibility = item.State == DownloadTaskState.Failed ? Visibility.Visible : Visibility.Collapsed;
            _cancel.Visibility = item.State is DownloadTaskState.Completed or DownloadTaskState.Cancelled ? Visibility.Collapsed : Visibility.Visible;
            _remove.Visibility = item.State is DownloadTaskState.Completed or DownloadTaskState.Cancelled ? Visibility.Visible : Visibility.Collapsed;
            if (item.State == DownloadTaskState.Failed && item.ErrorCode is not null)
                _detail.Text += " · " + _owner._strings.Get("DownloadError");
            AutomationProperties.SetName(_progress, _name.Text);
        }
    }
}
