using DropSpace.Core.Abstractions;
using DropSpace.Core.Lyrics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DropSpace.App.Views.Music;

/// <summary>Unmodified WinUI selection, keyboard focus, theme and high-contrast behavior.</summary>
public sealed class LyricsGlowModeControl : UserControl
{
    private readonly ComboBox _selector;
    private bool _syncing;
    public LyricsGlowModeControl(IAppStringLocalizer strings)
    {
        _selector = new ComboBox { HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 160,
            ItemsSource = new[] { strings.Get("LyricsGlowOff"), strings.Get("LyricsGlowAi"), strings.Get("LyricsGlowMusic") }, SelectedIndex = 0 };
        AutomationProperties.SetAutomationId(_selector, "LyricsGlowMode");
        AutomationProperties.SetName(_selector, strings.Get("LyricsGlowTitle"));
        AutomationProperties.SetHelpText(_selector, strings.Get("LyricsGlowKeyboardHelp"));
        _selector.SelectionChanged += (_, _) => { if (!_syncing) ModeChanged?.Invoke(this, EventArgs.Empty); };
        Content = _selector;
    }
    public event EventHandler? ModeChanged;
    public LyricsGlowMode Mode
    {
        get => (LyricsGlowMode)Math.Clamp(_selector.SelectedIndex, 0, 2);
        set
        {
            _syncing = true;
            try { _selector.SelectedIndex = Math.Clamp((int)value, 0, 2); }
            finally { _syncing = false; }
        }
    }
}
