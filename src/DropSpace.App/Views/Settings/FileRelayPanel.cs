using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DropSpace.App.Views.Settings;

/// <summary>Explicit opt-in directory controls. File discovery never opens this surface.</summary>
public sealed class FileRelayPanel : StackPanel
{
    private readonly NativeSettingsEditor _editor;
    private readonly IAppStringLocalizer _strings;
    private readonly nint _owner;
    private readonly StackPanel _directories = new() { Spacing = 12 };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap };
    private bool _loaded;
    private bool _busy;

    public FileRelayPanel(NativeSettingsEditor editor, IAppStringLocalizer strings, nint owner)
    {
        _editor = editor; _strings = strings; _owner = owner; Spacing = 16;
        Children.Add(new TextBlock { Text = strings.Get("RelayTitle"), FontSize = 22 });
        Children.Add(new TextBlock { Text = strings.Get("RelayDescription"), TextWrapping = TextWrapping.Wrap, MaxWidth = 680 });
        var choose = new Button { Content = strings.Get("RelayChooseDirectory") };
        AutomationProperties.SetAutomationId(choose, "RelayChooseDirectory");
        choose.Click += async (_, _) => await RunAsync(async () =>
        {
            var folder = await _editor.PickDownloadFolderAsync(_owner);
            if (folder is not null) await _editor.Recent.AddDirectoryAsync(folder);
        });
        Children.Add(choose); Children.Add(_error); Children.Add(_directories);
        Children.Add(new TextBlock { Text = strings.Get("RelayRetentionDescription"), TextWrapping = TextWrapping.Wrap, MaxWidth = 680, Opacity = .7 });
        Loaded += (_, _) => { _loaded = true; _editor.Recent.Changed += OnChanged; Refresh(); };
        Unloaded += (_, _) => { _loaded = false; _editor.Recent.Changed -= OnChanged; };
    }

    private void OnChanged(object? sender, EventArgs args) => DispatcherQueue.TryEnqueue(() =>
    {
        if (_loaded && !_busy) Refresh();
    });

    private async Task RunAsync(Func<Task> operation)
    {
        if (_busy) return;
        _busy = true; _error.Text = string.Empty;
        try { await operation(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        { _error.Text = _strings.Format("RelayError", error.Message); }
        finally { _busy = false; if (_loaded) Refresh(); }
    }

    private void Refresh()
    {
        _directories.Children.Clear();
        if (_editor.Recent.Directories.Count == 0)
            _directories.Children.Add(new TextBlock { Text = _strings.Get("RelayOff"), TextWrapping = TextWrapping.Wrap });
        foreach (var directory in _editor.Recent.Directories)
        {
            var row = new StackPanel { Spacing = 6 };
            row.Children.Add(new TextBlock { Text = directory.Path, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            row.Children.Add(new TextBlock
            {
                Text = directory.IsPaused ? _strings.Get("RelayPaused") : directory.IsAvailable ? _strings.Get("RelayWatching") :
                    _strings.Format("RelayUnavailable", directory.Reason switch
                    {
                        "DirectoryUnavailable" => _strings.Get("RelayReasonDirectoryUnavailable"),
                        "AccessDenied" => _strings.Get("RelayReasonAccessDenied"),
                        "DirectoryUnsupported" => _strings.Get("RelayReasonDirectoryUnsupported"),
                        "WatcherInterrupted" => _strings.Get("RelayReasonWatcherInterrupted"),
                        "EventsOverflow" => _strings.Get("RelayReasonEventsOverflow"),
                        _ => _strings.Get("ActionUnavailable"),
                    }),
                TextWrapping = TextWrapping.Wrap, Opacity = .7,
            });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var pause = new Button { Content = _strings.Get(directory.IsPaused ? "RelayResume" : "RelayPause") };
            AutomationProperties.SetName(pause, _strings.Get(directory.IsPaused ? "RelayResume" : "RelayPause") + " " + directory.Path);
            pause.Click += async (_, _) => await RunAsync(() => _editor.Recent.SetDirectoryPausedAsync(directory.Path, !directory.IsPaused));
            var remove = new Button { Content = _strings.Get("RelayRemoveDirectory") };
            AutomationProperties.SetName(remove, _strings.Get("RelayRemoveDirectory") + " " + directory.Path);
            remove.Click += async (_, _) => await RunAsync(() => _editor.Recent.RemoveDirectoryAsync(directory.Path));
            actions.Children.Add(pause); actions.Children.Add(remove); row.Children.Add(actions); _directories.Children.Add(row);
        }
    }
}
