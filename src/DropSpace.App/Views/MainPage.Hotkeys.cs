using DropSpace.Core.Models;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace DropSpace.App.Views;

public sealed partial class MainPage
{
    private readonly Dictionary<string, Button> _shortcutButtons = [];
    private TextBlock? _hotkeyStatus;
    private bool _recordingShortcut;

    private void BuildKeyboardShortcutsPage(StackPanel page)
    {
        void Heading(string key) => page.Children.Add(new TextBlock { Text = _strings.Get(key), FontSize = 20 });
        void Row(string id, string label, string? fixedGesture = null)
        {
            var row = new Grid { ColumnSpacing = 16 };
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = _strings.Get(label), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            FrameworkElement control;
            if (fixedGesture is null)
            {
                var button = new Button { MinWidth = 170 };
                AutomationProperties.SetName(button, _strings.Get(label));
                AutomationProperties.SetAutomationId(button, "Shortcut-" + id);
                button.Click += async (_, _) => await RecordShortcutAsync(id, label);
                _shortcutButtons.Add(id, button); control = button;
            }
            else control = new TextBlock { Text = fixedGesture, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(control, 1); row.Children.Add(control); page.Children.Add(row);
        }
        Heading("ShortcutGlobal");
        Row("QuickPanel", "ShortcutOpenIsland");
        _hotkeyStatus = new TextBlock { TextWrapping = TextWrapping.Wrap };
        page.Children.Add(_hotkeyStatus);
        Heading("ShortcutMainWindow");
        foreach (var id in KeyboardShortcutPolicy.Defaults.Keys) Row(id, "Shortcut" + id);
        Heading("ShortcutContext");
        Row("ItemOpen", "ShortcutItemOpen", "Enter");
        Row("ItemRemove", "ShortcutItemRemove", "Delete");
        Row("SearchClear", "ShortcutSearchClear", "Esc");
        Row("IslandClose", "ShortcutIslandClose", "Esc / Alt+F4");
        Row("EditCommit", "ShortcutEditCommit", "Enter / Esc");
        Row("PlacementStep", "ShortcutPlacementStep", "Shift+← / → / ↑ / ↓");
        Row("Navigate", "ShortcutNavigate", "Tab / Shift+Tab / ← / → / ↑ / ↓");
        Row("TextEdit", "ShortcutTextEdit", "Ctrl+A / C / X / V / Z");
        RefreshKeyboardShortcuts();
    }

    private void RefreshKeyboardShortcuts()
    {
        if (_recordingShortcut) return;
        foreach (var (id, button) in _shortcutButtons)
            button.Content = id == "QuickPanel" ? _viewModel.QuickPanelHotkey : KeyboardShortcutPolicy.Get(_viewModel.Settings, id);
        if (_hotkeyStatus is not null)
        {
            _hotkeyStatus.Text = _hotkey.IsRegistered ? "" : _strings.Get("HotkeyUnavailable");
            _hotkeyStatus.Visibility = _hotkey.IsRegistered ? Visibility.Collapsed : Visibility.Visible;
        }
        KeyboardAccelerators.Clear();
        foreach (var id in KeyboardShortcutPolicy.Defaults.Keys)
        {
            var parts = SettingsValidationPolicy.CanonicalizeHotkey(KeyboardShortcutPolicy.Get(_viewModel.Settings, id))!.Split('+');
            var modifiers = VirtualKeyModifiers.None;
            foreach (var part in parts[..^1]) modifiers |= part.ToUpperInvariant() switch
            { "CTRL" => VirtualKeyModifiers.Control, "ALT" => VirtualKeyModifiers.Menu, "SHIFT" => VirtualKeyModifiers.Shift, "WIN" => VirtualKeyModifiers.Windows, _ => VirtualKeyModifiers.None };
            var accelerator = new KeyboardAccelerator { Key = (VirtualKey)KeyboardShortcutPolicy.KeyCode(parts[^1]), Modifiers = modifiers };
            accelerator.Invoked += (sender, args) =>
            {
                if (_recordingShortcut) { args.Handled = true; return; }
                switch (id)
                {
                    case "Space": OnSpaceAccelerator(sender, args); break;
                    case "Clipboard": OnClipboardAccelerator(sender, args); break;
                    case "Pinned": OnPinnedAccelerator(sender, args); break;
                    case "Search": case "SearchAlternate": OnSearchAccelerator(sender, args); break;
                    case "Settings": case "SettingsAlternate": OnSettingsAccelerator(sender, args); break;
                    case "Undo": OnUndoAccelerator(sender, args); break;
                }
            };
            KeyboardAccelerators.Add(accelerator);
        }
    }

    private async Task RecordShortcutAsync(string id, string label)
    {
        await RunAsync(async () =>
        {
            await _dialogGate.WaitAsync(_dialogLifetime.Token);
            _recordingShortcut = true;
            try
            {
                await _hotkey.StopAsync(_dialogLifetime.Token);
                foreach (var accelerator in KeyboardAccelerators) accelerator.IsEnabled = false;
                string? candidate = null;
                var recorded = new Button { Content = _strings.Get("HotkeyRecordPrompt"), HorizontalAlignment = HorizontalAlignment.Stretch };
                var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
                var content = new StackPanel { Spacing = 12 };
                content.Children.Add(recorded); content.Children.Add(status);
                var dialog = new ContentDialog
                {
                    XamlRoot = XamlRoot, Title = _strings.Get(label), Content = content,
                    PrimaryButtonText = _strings.Get("HotkeySave"), CloseButtonText = _strings.Get("CommonCancel"),
                    IsPrimaryButtonEnabled = false, DefaultButton = ContentDialogButton.None,
                };
                AppSettings CandidateSettings(string gesture) => id == "QuickPanel"
                    ? _viewModel.Settings with { QuickPanelHotkey = gesture }
                    : _viewModel.Settings with { KeyboardShortcuts = new(_viewModel.Settings.KeyboardShortcuts) { [id] = gesture } };
                recorded.PreviewKeyDown += (_, e) =>
                {
                    if (e.Key is VirtualKey.Tab or VirtualKey.Escape) return;
                    e.Handled = true;
                    static bool Down(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
                    if (e.Key is VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu or VirtualKey.LeftWindows or VirtualKey.RightWindows) return;
                    var key = KeyboardShortcutPolicy.KeyName((uint)e.Key);
                    var parts = new List<string>();
                    if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows)) parts.Add("Win");
                    if (Down(VirtualKey.Control)) parts.Add("Ctrl");
                    if (Down(VirtualKey.Menu)) parts.Add("Alt");
                    if (Down(VirtualKey.Shift)) parts.Add("Shift");
                    candidate = key is not null && parts.Count > 0 ? string.Join('+', parts.Append(key)) : null;
                    recorded.Content = candidate ?? _strings.Get("HotkeyRecordPrompt");
                    var valid = candidate is not null;
                    if (valid)
                    {
                        try { KeyboardShortcutPolicy.Validate(CandidateSettings(candidate!), includeGlobal: true); }
                        catch (ArgumentException) { valid = false; }
                        if (valid && id == "QuickPanel") valid = _hotkey.CanRegister(candidate!);
                    }
                    dialog.IsPrimaryButtonEnabled = valid;
                    status.Text = candidate is null ? _strings.Get("HotkeyRecordPrompt") : valid ? "" : _strings.Get("HotkeyUnavailable");
                };
                dialog.Opened += (_, _) => recorded.Focus(FocusState.Keyboard);
                dialog.PrimaryButtonClick += async (_, e) =>
                {
                    var deferral = e.GetDeferral();
                    try
                    {
                        if (candidate is null) { e.Cancel = true; return; }
                        var next = CandidateSettings(candidate);
                        KeyboardShortcutPolicy.Validate(next, includeGlobal: true);
                        if (id == "QuickPanel" && !await _hotkey.TryStartAsync(candidate, _dialogLifetime.Token))
                        { e.Cancel = true; status.Text = _strings.Get("HotkeyUnavailable"); return; }
                        await _viewModel.UpdateSettingsAsync(next);
                    }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    { e.Cancel = true; status.Text = _strings.Get("HotkeyUnavailable"); await _hotkey.StopAsync(); }
                    finally { deferral.Complete(); }
                };
                await ShowOwnedDialogAsync(dialog);
            }
            finally
            {
                try { await _hotkey.StartAsync(_viewModel.QuickPanelHotkey); }
                finally { _recordingShortcut = false; _dialogGate.Release(); RefreshKeyboardShortcuts(); }
            }
        });
    }
}
