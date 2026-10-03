using System.ComponentModel;
using System.Runtime.CompilerServices;
using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VirtualKey = Windows.System.VirtualKey;

namespace DropSpace.App.Views.Music;

/// <summary>One lyric size controls both lines; decimal editing and pointer previews never wait for disk.</summary>
public sealed class LyricsFontSizeControl : UserControl
{
    private static readonly ConditionalWeakTable<NativeSettingsEditor, LyricsFontSizeEditSession> Sessions = new();
    private readonly NativeSettingsEditor _editor;
    private readonly IAppStringLocalizer _strings;
    private readonly LyricsFontSizeEditSession _session;
    private readonly Slider _slider = new()
    {
        Minimum = LyricsFontSizeEditSession.Minimum, Maximum = LyricsFontSizeEditSession.Maximum,
        // 16,000 subpixel steps feel continuous even at 200% scale; keyboard changes remain deliberate.
        StepFrequency = 0.001, SmallChange = 0.1, LargeChange = 1,
        SnapsTo = SliderSnapsTo.StepValues, IsThumbToolTipEnabled = false,
        VerticalAlignment = VerticalAlignment.Center,
    };
    // A native numeric-input TextBox preserves typed decimal precision and intermediate text without
    // NumberBox's built-in formatter/coercion silently rounding or clamping a user's input.
    private readonly TextBox _number = new() { Width = 112, MaxLength = 32, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _original = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _translation = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.72 };
    private readonly TextBlock _feedback = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly DispatcherQueueTimer _debounce;
    private bool _loaded, _updating, _editingText, _invalidText, _restored;
    private uint? _pointer;

    public LyricsFontSizeControl(NativeSettingsEditor editor, IAppStringLocalizer strings)
    {
        _editor = editor; _strings = strings;
        _session = Sessions.GetValue(editor, owner => new LyricsFontSizeEditSession(owner.Settings.Lyrics.FontSize,
            (value, isLatest) => owner.UpdateAsync(settings => isLatest()
                ? settings with { Lyrics = settings.Lyrics with { FontSize = value } } : settings)));
        _debounce = DispatcherQueue.CreateTimer();
        _debounce.Interval = TimeSpan.FromMilliseconds(300);
        _debounce.IsRepeating = false;
        _debounce.Tick += OnDebounce;
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock { Text = strings.Get("LyricsFontSize"), FontWeight = FontWeights.SemiBold });
        body.Children.Add(new TextBlock { Text = strings.Get("LyricsFontSizeHelp"), TextWrapping = TextWrapping.Wrap, Opacity = 0.72 });
        AutomationProperties.SetName(_slider, strings.Get("LyricsFontSize"));
        AutomationProperties.SetAutomationId(_slider, "LyricsFontSizeSlider");
        AutomationProperties.SetHelpText(_slider, strings.Get("LyricsFontSizeKeyboardHelp"));
        AutomationProperties.SetName(_number, strings.Get("LyricsFontSizeNumber"));
        AutomationProperties.SetAutomationId(_number, "LyricsFontSizeNumber");
        AutomationProperties.SetHelpText(_number, strings.Get("LyricsFontSizeHelp"));
        _number.InputScope = new InputScope { Names = { new InputScopeName(InputScopeNameValue.Number) } };
        var inputs = new Grid { ColumnSpacing = 16 };
        inputs.ColumnDefinitions.Add(new ColumnDefinition());
        inputs.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputs.Children.Add(_slider);
        Grid.SetColumn(_number, 1); inputs.Children.Add(_number);
        body.Children.Add(inputs);
        var range = new Grid();
        range.Children.Add(new TextBlock { Text = LyricsFontSizeEditSession.Minimum.ToString(strings.Culture), Opacity = 0.72 });
        range.Children.Add(new TextBlock { Text = LyricsFontSizeEditSession.Maximum.ToString(strings.Culture), Opacity = 0.72,
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 128, 0) });
        body.Children.Add(range);
        AutomationProperties.SetLiveSetting(_feedback, AutomationLiveSetting.Polite);
        body.Children.Add(_feedback);
        _original.Text = strings.Get("LyricsOriginalFontPreview");
        _translation.Text = strings.Get("LyricsTranslationFontPreview");
        body.Children.Add(new StackPanel { Spacing = 4, Children = { _original, _translation } });
        var reset = new Button { Content = strings.Get("LyricsResetFonts"), HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetAutomationId(reset, "LyricsFontSizeReset");
        reset.Click += OnReset;
        body.Children.Add(reset);
        Content = new Border { Padding = new Thickness(14), CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"], Child = body };
        _slider.ValueChanged += OnSliderChanged;
        _slider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPointerPressed), true);
        _slider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnPointerFinished), true);
        _slider.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(OnPointerFinished), true);
        _slider.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler(OnPointerFinished), true);
        _number.GotFocus += (_, _) => _editingText = true;
        _number.TextChanged += OnTextChanged;
        _number.LostFocus += (_, _) => FinishText();
        _number.KeyDown += OnNumberKeyDown;
        Loaded += OnLoaded; Unloaded += OnUnloaded;
        Refresh();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_loaded) return;
        _loaded = true;
        _editor.PropertyChanged += OnSettings;
        _session.Changed += OnSessionChanged;
        _session.RefreshFromStore(_editor.Settings.Lyrics.FontSize);
        Refresh();
        if (_session.HasChanges) ScheduleSave();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _debounce.Stop();
        _loaded = false;
        _editor.PropertyChanged -= OnSettings;
        _session.Changed -= OnSessionChanged;
        _pointer = null;
        _editingText = false;
        // Shared ownership outlives this page: navigation flushes the last valid preview without
        // blocking the dispatcher or allowing an old page's completion to overwrite newer input.
        _ = _session.FlushAsync();
    }

    private void OnSettings(object? sender, PropertyChangedEventArgs args)
    {
        if (_loaded && args.PropertyName == nameof(NativeSettingsEditor.Settings))
            _session.RefreshFromStore(_editor.Settings.Lyrics.FontSize);
    }
    private void OnSessionChanged(object? sender, EventArgs args) { if (_loaded) Refresh(); }

    private void OnSliderChanged(object sender, RangeBaseValueChangedEventArgs args)
    {
        if (_updating || !_loaded || !LyricsFontSizeEditSession.IsValid(args.NewValue)) return;
        _editingText = _invalidText = _restored = false;
        _session.SetValue(args.NewValue);
        Refresh();
        ScheduleSave();
    }

    private void OnTextChanged(object sender, TextChangedEventArgs args)
    {
        if (_updating || !_loaded) return;
        _editingText = true;
        _restored = false;
        _invalidText = !LyricsFontSizeEditSession.TryParse(_number.Text, _strings.Culture, out var value);
        if (!_invalidText) { _session.SetValue(value); ScheduleSave(); }
        else _debounce.Stop();
        Refresh();
    }

    private void FinishText()
    {
        if (!_loaded || _updating) return;
        _restored = !LyricsFontSizeEditSession.TryParse(_number.Text, _strings.Culture, out var value);
        if (!_restored) _session.SetValue(value);
        _editingText = _invalidText = false;
        Refresh();
        Flush();
    }
    private void OnNumberKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Enter) { FinishText(); args.Handled = true; }
        else if (args.Key == VirtualKey.Escape)
        {
            _editingText = _invalidText = _restored = false;
            Refresh(); Flush(); args.Handled = true;
        }
    }
    private void OnReset(object sender, RoutedEventArgs args)
    {
        _editingText = _invalidText = _restored = false;
        _session.SetValue(LyricsFontSizeEditSession.Default);
        Refresh(); Flush();
    }
    private void OnPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(_slider);
        if (!_loaded || _pointer is not null || (!point.IsInContact && !point.Properties.IsLeftButtonPressed)) return;
        _pointer = args.Pointer.PointerId;
        _debounce.Stop();
    }
    private void OnPointerFinished(object sender, PointerRoutedEventArgs args)
    {
        if (_pointer != args.Pointer.PointerId) return;
        _pointer = null;
        Flush();
    }
    private void ScheduleSave()
    {
        _debounce.Stop();
        if (_loaded && _pointer is null) _debounce.Start();
    }
    private void OnDebounce(DispatcherQueueTimer sender, object args) { if (_loaded && _pointer is null) Flush(); }
    private void Flush() { _debounce.Stop(); _ = _session.FlushAsync(); }

    private void Refresh()
    {
        _updating = true;
        try
        {
            var value = _session.Value;
            _slider.Value = value;
            if (!_editingText) _number.Text = LyricsFontSizeEditSession.Format(value, _strings.Culture);
            _original.FontSize = value;
            _translation.FontSize = value * LyricsFontSizeEditSession.TranslationRatio;
            _feedback.Text = _invalidText ? _strings.Get("LyricsFontSizeInvalid") : _restored ? _strings.Get("LyricsFontSizeRestored") :
                _session.SaveFailed ? _strings.Get("NativeSettingsSaveFailed") : string.Empty;
            _feedback.Visibility = string.IsNullOrEmpty(_feedback.Text) ? Visibility.Collapsed : Visibility.Visible;
        }
        finally { _updating = false; }
    }
}
