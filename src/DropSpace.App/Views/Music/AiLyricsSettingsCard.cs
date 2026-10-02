using System.ComponentModel;
using System.Diagnostics;
using DropSpace.App.Services.Media;
using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DropSpace.App.Views.Music;

/// <summary>Only an explicit download confirmation can fetch a model. Playback never does.</summary>
public sealed class AiLyricsSettingsCard : UserControl
{
    private readonly NativeSettingsEditor _editor;
    private readonly AiLyricsService _service;
    private readonly Func<CancellationToken, Task> _clearLyricsCache;
    private readonly IAppStringLocalizer _strings;
    private readonly ToggleSwitch _enabled = new();
    private readonly ToggleSwitch _gpuAcceleration = new();
    private readonly ComboBox _models = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock _details = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.72 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap };
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed };
    private readonly Button _download;
    private readonly Button _cancel;
    private readonly Button _resume;
    private readonly Button _delete;
    private readonly Button _clearCache;
    private readonly StackPanel _legacyModels = new() { Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly Dictionary<string, Button> _legacyDeleteButtons = new(StringComparer.Ordinal);
    private readonly LyricsGlowModeControl _glow;
    private readonly HashSet<string> _installed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _removable = new(StringComparer.Ordinal);
    private readonly HashSet<string> _interrupted = new(StringComparer.Ordinal);
    private CancellationTokenSource? _lifetime;
    private CancellationTokenSource? _downloadStop;
    private Task _operationTask = Task.CompletedTask;
    private Task _inspectionTask = Task.CompletedTask;
    private Task _glowTask = Task.CompletedTask;
    private LyricsGlowMode? _desiredGlow;
    private int _glowRevision;
    private bool _savingGlow;
    private int _generation;
    private bool _syncing, _busy, _inspecting, _downloading;
    private string? _downloadModelId;
    private string _message = string.Empty;
    private string _errorMessage = string.Empty;

    public AiLyricsSettingsCard(NativeSettingsEditor editor, AiLyricsService service, IAppStringLocalizer strings, Func<CancellationToken, Task>? clearLyricsCache = null)
    {
        _editor = editor; _service = service; _strings = strings;
        _clearLyricsCache = clearLyricsCache ?? service.ClearCacheAsync;
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock { Text = strings.Get("AiLyricsTitle"), FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = strings.Get("AiLyricsDescription"), TextWrapping = TextWrapping.Wrap });
        _enabled.Header = strings.Get("AiLyricsEnabled");
        AutomationProperties.SetName(_enabled, strings.Get("AiLyricsEnabled"));
        AutomationProperties.SetAutomationId(_enabled, "AiLyricsEnabled");
        _enabled.Toggled += OnEnabled;
        body.Children.Add(_enabled);
        _gpuAcceleration.Header = strings.Get("AiLyricsGpuAcceleration");
        AutomationProperties.SetName(_gpuAcceleration, strings.Get("AiLyricsGpuAcceleration"));
        AutomationProperties.SetAutomationId(_gpuAcceleration, "AiLyricsGpuAcceleration");
        AutomationProperties.SetHelpText(_gpuAcceleration, strings.Get("AiLyricsGpuAccelerationHelp"));
        _gpuAcceleration.Toggled += OnGpuAcceleration;
        body.Children.Add(_gpuAcceleration);
        body.Children.Add(new TextBlock { Text = strings.Get("AiLyricsGpuAccelerationHelp"), TextWrapping = TextWrapping.Wrap, Opacity = 0.72 });
        body.Children.Add(new TextBlock { Text = strings.Get("AiLyricsModel"), FontWeight = FontWeights.SemiBold });
        foreach (var model in AiLyricsModelCatalog.All)
            _models.Items.Add(new ComboBoxItem { Content = ModelLabel(model), Tag = model });
        _models.SelectionChanged += OnModelSelected;
        AutomationProperties.SetName(_models, strings.Get("AiLyricsModel"));
        AutomationProperties.SetAutomationId(_models, "AiLyricsModel");
        body.Children.Add(_models);
        body.Children.Add(_details);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Polite);
        body.Children.Add(_status);
        body.Children.Add(_error);
        AutomationProperties.SetName(_progress, strings.Get("AiLyricsDownloadProgress"));
        body.Children.Add(_progress);
        var actions = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Left };
        _download = new Button { Content = strings.Get("AiLyricsDownload") };
        _cancel = new Button { Content = strings.Get("AiLyricsCancelDownload"), Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(_download, "AiLyricsDownload");
        AutomationProperties.SetAutomationId(_cancel, "AiLyricsCancelDownload");
        _download.Click += OnDownload;
        _cancel.Click += OnCancel;
        _resume = new Button { Content = strings.Get("AiLyricsResumeTranslation"), Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(_resume, "AiLyricsResumeTranslation");
        _resume.Click += (_, _) => { _service.ResumeTranslation(); Refresh(); };
        _delete = new Button { Content = strings.Get("AiLyricsDeleteModel") };
        _clearCache = new Button { Content = strings.Get("AiLyricsClearCache") };
        AutomationProperties.SetAutomationId(_delete, "AiLyricsDeleteModel");
        AutomationProperties.SetAutomationId(_clearCache, "AiLyricsClearCache");
        _delete.Click += OnDelete;
        _clearCache.Click += OnClearCache;
        actions.Children.Add(_download); actions.Children.Add(_cancel); actions.Children.Add(_resume);
        actions.Children.Add(_delete); actions.Children.Add(_clearCache);
        body.Children.Add(actions);
        _legacyModels.Children.Add(new TextBlock { Text = strings.Get("AiLyricsLegacyModels"), FontWeight = FontWeights.SemiBold });
        _legacyModels.Children.Add(new TextBlock { Text = strings.Get("AiLyricsLegacyModelsHelp"), TextWrapping = TextWrapping.Wrap, Opacity = 0.72 });
        foreach (var model in AiLyricsModelCatalog.Legacy)
        {
            var remove = new Button { Content = strings.Format("AiLyricsDeleteLegacyModel", ModelLabel(model)), HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetName(remove, strings.Format("AiLyricsDeleteLegacyModel", ModelLabel(model)));
            AutomationProperties.SetAutomationId(remove, "AiLyricsDeleteLegacy-" + model.Id);
            remove.Click += (_, _) => DeleteModel(model);
            _legacyDeleteButtons.Add(model.Id, remove);
            _legacyModels.Children.Add(remove);
        }
        body.Children.Add(_legacyModels);
        body.Children.Add(new TextBlock { Text = strings.Get(AiLyricsModelCatalog.All.Count > 1 ? "AiLyricsDownloadHelp" : "AiLyricsDownloadHelpSingle"), TextWrapping = TextWrapping.Wrap, Opacity = 0.72 });
        body.Children.Add(new TextBlock { Text = strings.Get("LyricsGlowTitle"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
        _glow = new LyricsGlowModeControl(strings);
        _glow.ModeChanged += OnGlowChanged;
        body.Children.Add(_glow);
        body.Children.Add(new TextBlock { Text = strings.Get("LyricsGlowDescription"), TextWrapping = TextWrapping.Wrap, Opacity = 0.72 });
        Content = new Border { Padding = new Thickness(16), CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1), Child = body };
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        Refresh();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_lifetime is not null) return;
        _lifetime = new CancellationTokenSource();
        ++_generation;
        _editor.PropertyChanged += OnSettings;
        _service.ModelDownloaded += OnModelDownloaded;
        _service.TranslationStateChanged += OnTranslationStateChanged;
        _inspectionTask = InspectAsync(_generation, _lifetime.Token);
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _editor.PropertyChanged -= OnSettings;
        _service.ModelDownloaded -= OnModelDownloaded;
        _service.TranslationStateChanged -= OnTranslationStateChanged;
        var lifetime = _lifetime;
        _lifetime = null;
        ++_generation;
        // ContentDialogLifetime dismisses the consent dialog and invalidates even a simultaneous Yes.
        if (_downloadModelId is { } interruptedModel) _interrupted.Add(interruptedModel);
        lifetime?.Cancel();
        _downloadStop?.Cancel();
        _downloadStop = null;
        _busy = _inspecting = _downloading = _savingGlow = false;
        _desiredGlow = null;
        ++_glowRevision;
        _downloadModelId = null;
        _message = _errorMessage = string.Empty;
        _ = ObserveRetirementAsync(_operationTask, _inspectionTask, _glowTask, lifetime);
    }

    private static async Task ObserveRetirementAsync(Task operation, Task inspection, Task glow, CancellationTokenSource? lifetime)
    {
        try { await Task.WhenAll(operation, inspection, glow).ConfigureAwait(false); }
        catch (Exception exception) { Debug.WriteLine($"AI lyrics UI retirement: {exception.GetType().Name}"); }
        finally { lifetime?.Dispose(); }
    }

    private void OnSettings(object? sender, PropertyChangedEventArgs args)
    {
        if (_lifetime is not null && args.PropertyName == nameof(NativeSettingsEditor.Settings)) Refresh();
    }

    private void OnTranslationStateChanged(object? sender, EventArgs args)
    {
        var generation = _generation;
        try { DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsCurrent(generation)) return;
            if (_service.TranslationState != AiLyricsTranslationState.Ready) _message = string.Empty;
            Refresh();
        }); }
        catch (Exception exception) { Debug.WriteLine($"AI lyrics state dispatcher retired: {exception.GetType().Name}"); }
    }

    private void OnModelDownloaded(object? sender, EventArgs args)
    {
        var generation = _generation;
        // Service events can arrive from a worker thread. Never touch detached controls there.
        try
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!IsCurrent(generation) || _busy || _inspecting) return;
                _inspectionTask = InspectAsync(generation, _lifetime!.Token);
            });
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"AI lyrics dispatcher retired: {exception.GetType().Name}");
        }
    }

    private async Task InspectAsync(int generation, CancellationToken token)
    {
        _inspecting = true;
        try
        {
            Refresh();
            var installed = new HashSet<string>(StringComparer.Ordinal);
            var removable = new HashSet<string>(StringComparer.Ordinal);
            foreach (var model in AiLyricsModelCatalog.All)
            {
                if (await _service.GetInstalledPathAsync(model.Id, token) is not null) installed.Add(model.Id);
                if (_service.HasModelArtifacts(model.Id)) removable.Add(model.Id);
            }
            // Older packages can be removed, but never selected, enabled, or downloaded.
            foreach (var model in AiLyricsModelCatalog.Legacy)
                if (_service.HasModelArtifacts(model.Id)) removable.Add(model.Id);
            if (!IsCurrent(generation)) return;
            _installed.Clear();
            _installed.UnionWith(installed);
            _removable.Clear();
            _removable.UnionWith(removable);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (IsCurrent(generation)) ShowError(exception, "AiLyricsInspectFailed");
        }
        finally
        {
            if (IsCurrent(generation)) { _inspecting = false; Refresh(); }
        }
    }

    private void OnEnabled(object sender, RoutedEventArgs args)
    {
        if (_syncing) return;
        var enable = _enabled.IsOn;
        StartOperation(async (generation, token) =>
        {
            var model = SelectedModel();
            if (enable && !_editor.Settings.Lyrics.Enabled && _editor.Settings.Lyrics.Mode == LyricsMode.Online)
            {
                var consent = new ContentDialog
                {
                    XamlRoot = XamlRoot, Title = _strings.Get("LyricsEnabled"),
                    Content = _strings.Get("LyricsOnlinePrivacyHelp"),
                    PrimaryButtonText = _strings.Get("LyricsEnabled"), CloseButtonText = _strings.Get("AiLyricsNotNow"),
                    DefaultButton = ContentDialogButton.Close,
                };
                if (await ContentDialogLifetime.ShowAsync(consent, token) != ContentDialogResult.Primary || !IsCurrent(generation)) return;
            }
            if (enable && !await EnsureInstalledAsync(model, generation, token)) return;
            await SaveAsync(generation, settings => settings with
            {
                Lyrics = settings.Lyrics with { Enabled = enable || settings.Lyrics.Enabled, AiModelId = model.Id, AiTranslationEnabled = enable, SecondaryLyrics = enable || settings.Lyrics.SecondaryLyrics, GlowMode = LyricsGlowPolicy.OnAiEnabledChanged(enable) },
            });
        });
    }

    private void OnGpuAcceleration(object sender, RoutedEventArgs args)
    {
        if (_syncing) return;
        var preferGpu = _gpuAcceleration.IsOn;
        // This is a persisted preference, not a claim about the runtime's active device.
        // Settings propagation owns cancellation/draining when the execution mode changes.
        StartOperation(async (generation, _) =>
        {
            await SaveAsync(generation, settings => settings with
            {
                Lyrics = settings.Lyrics with { AiLyricsGpuAccelerationEnabled = preferGpu },
            });
        });
    }

    private void OnModelSelected(object sender, SelectionChangedEventArgs args)
    {
        if (_syncing || _models.SelectedItem is not ComboBoxItem { Tag: AiLyricsModelDescriptor model }) return;
        StartOperation(async (generation, token) =>
        {
            // A missing replacement cannot silently interrupt the current working model.
            if (_editor.Settings.Lyrics.AiTranslationEnabled && !await EnsureInstalledAsync(model, generation, token)) return;
            await SaveAsync(generation, settings => settings with { Lyrics = settings.Lyrics with { AiModelId = model.Id } });
        });
    }

    private void OnDownload(object sender, RoutedEventArgs args)
    {
        var model = SelectedModel();
        StartOperation(async (generation, token) => { await EnsureInstalledAsync(model, generation, token); });
    }

    private void OnDelete(object sender, RoutedEventArgs args) => DeleteModel(SelectedModel());

    private void DeleteModel(AiLyricsModelDescriptor model)
    {
        StartOperation(async (generation, token) =>
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = _strings.Get("AiLyricsDeleteModel"),
                Content = _strings.Format("AiLyricsDeleteConfirm", ModelLabel(model)),
                PrimaryButtonText = _strings.Get("AiLyricsDeleteModel"), CloseButtonText = _strings.Get("AiLyricsNotNow"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await ContentDialogLifetime.ShowAsync(dialog, token) != ContentDialogResult.Primary || !IsCurrent(generation)) return;
            if (_editor.Settings.Lyrics.AiModelId == model.Id &&
                !await SaveAsync(generation, settings => settings with { Lyrics = settings.Lyrics with { AiTranslationEnabled = false } })) return;
            await _service.DeleteModelAsync(model.Id, token);
            if (!IsCurrent(generation)) return;
            _installed.Remove(model.Id);
            _interrupted.Remove(model.Id);
            _removable.Remove(model.Id);
            _message = _strings.Get("AiLyricsModelDeleted");
        });
    }

    private void OnClearCache(object sender, RoutedEventArgs args)
    {
        StartOperation(async (generation, token) =>
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = _strings.Get("AiLyricsClearCache"),
                Content = _strings.Get("AiLyricsClearCacheConfirm"),
                PrimaryButtonText = _strings.Get("AiLyricsClearCache"), CloseButtonText = _strings.Get("AiLyricsNotNow"),
                DefaultButton = ContentDialogButton.Close,
            };
            if (await ContentDialogLifetime.ShowAsync(dialog, token) != ContentDialogResult.Primary || !IsCurrent(generation)) return;
            _message = _strings.Get("AiLyricsClearingCache");
            Refresh();
            await _clearLyricsCache(token);
            if (IsCurrent(generation)) _message = _strings.Get("AiLyricsCacheCleared");
        });
    }

    private void OnCancel(object sender, RoutedEventArgs args)
    {
        _cancel.IsEnabled = false;
        _downloadStop?.Cancel();
    }

    private void OnGlowChanged(object? sender, EventArgs args)
    {
        if (_syncing || _lifetime is null) return;
        if (_busy) { Refresh(); return; }
        // Keep the native selection control responsive while persistence runs.
        // Coalesce to the newest selection while settings save is in progress.
        _desiredGlow = _glow.Mode;
        ++_glowRevision;
        if (!_savingGlow) _glowTask = SaveGlowAsync(_generation);
    }

    private async Task SaveGlowAsync(int generation)
    {
        _savingGlow = true;
        _errorMessage = string.Empty;
        try
        {
            while (IsCurrent(generation) && _desiredGlow is { } mode)
            {
                var revision = _glowRevision;
                // All three selections work with AI off; Music never enables AI or starts a download.
                await SaveAsync(generation, settings => settings with { Lyrics = settings.Lyrics with { GlowMode = mode } });
                if (!IsCurrent(generation)) return;
                if (revision == _glowRevision) { _desiredGlow = null; break; }
            }
        }
        catch (Exception exception)
        {
            if (IsCurrent(generation)) ShowError(exception, "AiLyricsOperationFailed");
        }
        finally
        {
            if (IsCurrent(generation)) { _savingGlow = false; _desiredGlow = null; Refresh(); }
        }
    }

    private void StartOperation(Func<int, CancellationToken, Task> action)
    {
        if (_lifetime is null || _busy || _inspecting) { Refresh(); return; }
        _operationTask = RunOperationAsync(action, _generation, _lifetime.Token);
    }

    private async Task RunOperationAsync(Func<int, CancellationToken, Task> action, int generation, CancellationToken token)
    {
        _busy = true;
        _message = _errorMessage = string.Empty;
        try
        {
            Refresh();
            // Finish earlier manual selections before an AI toggle applies its default mode.
            await _glowTask;
            if (!IsCurrent(generation)) return;
            await action(generation, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (IsCurrent(generation)) ShowError(exception, "AiLyricsOperationFailed");
        }
        finally
        {
            if (IsCurrent(generation)) { _busy = false; Refresh(); }
        }
    }

    private async Task<bool> EnsureInstalledAsync(AiLyricsModelDescriptor model, int generation, CancellationToken token)
    {
        // Verify disk bytes again before enabling; a stale UI status alone is not sufficient.
        if (await _service.GetInstalledPathAsync(model.Id, token) is not null)
        {
            if (!IsCurrent(generation)) return false;
            _installed.Add(model.Id);
            return true;
        }
        if (!IsCurrent(generation)) return false;
        _installed.Remove(model.Id);
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = _strings.Format("AiLyricsConfirmBody", ModelLabel(model), SizeLabel(model), model.Name, SourceLabel(model)) + "\n\n" + ModelHelp(model) + "\n\n" + _strings.Format("AiLyricsRuntimeIncluded", _service.RuntimeExtractionMiB), TextWrapping = TextWrapping.Wrap });
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = _strings.Get("AiLyricsConfirmTitle"), Content = content,
            PrimaryButtonText = _strings.Get("AiLyricsConfirmDownload"), CloseButtonText = _strings.Get("AiLyricsNotNow"),
            DefaultButton = ContentDialogButton.Close,
        };
        if (await ContentDialogLifetime.ShowAsync(dialog, token) != ContentDialogResult.Primary || !IsCurrent(generation)) return false;
        using var downloadStop = CancellationTokenSource.CreateLinkedTokenSource(token);
        _downloadStop = downloadStop;
        _downloadModelId = model.Id;
        _downloading = true;
        _progress.Value = 0;
        _progress.IsIndeterminate = true;
        _message = _strings.Get("AiLyricsDownloading");
        Refresh();
        var progress = new PercentageProgress(new Progress<double>(value =>
        {
            if (!IsCurrent(generation) || !ReferenceEquals(_downloadStop, downloadStop) || !_downloading) return;
            _progress.IsIndeterminate = false;
            _progress.Value = Math.Clamp(value * 100, 0, 100);
            // The progress bar exposes changes. Do not make a live-region announcement per chunk.
            _message = value >= 1 ? _strings.Get("AiLyricsVerifying") : _strings.Get("AiLyricsDownloading");
            _status.Text = _message;
        }));
        try
        {
            await _service.DownloadAsync(model.Id, consent: true, progress, downloadStop.Token);
            downloadStop.Token.ThrowIfCancellationRequested();
            if (!IsCurrent(generation)) return false;
            _installed.Add(model.Id);
            _interrupted.Remove(model.Id);
            _message = _strings.Get("AiLyricsDownloadComplete");
            return true;
        }
        catch (OperationCanceledException) when (downloadStop.IsCancellationRequested)
        {
            if (IsCurrent(generation))
            {
                _interrupted.Add(model.Id);
                _message = _strings.Get("AiLyricsDownloadCanceled");
            }
            return false;
        }
        catch (Exception exception)
        {
            if (IsCurrent(generation))
            {
                _interrupted.Add(model.Id);
                ShowError(exception, "AiLyricsDownloadFailed");
            }
            return false;
        }
        finally
        {
            if (IsCurrent(generation))
            {
                _downloadStop = null;
                _downloadModelId = null;
                _downloading = false;
                Refresh();
            }
        }
    }

    private async Task<bool> SaveAsync(int generation, Func<AppSettings, AppSettings> change)
    {
        if (!IsCurrent(generation)) return false;
        var saved = await _editor.UpdateAsync(settings => IsCurrent(generation) ? change(settings) : settings);
        if (IsCurrent(generation) && !saved) _errorMessage = _strings.Get("NativeSettingsSaveFailed");
        return IsCurrent(generation) && saved;
    }

    private bool IsCurrent(int generation) => _generation == generation && _lifetime is { IsCancellationRequested: false };
    private AiLyricsModelDescriptor SelectedModel() =>
        (_models.SelectedItem as ComboBoxItem)?.Tag as AiLyricsModelDescriptor ??
        AiLyricsModelCatalog.FindSelectable(_editor.Settings.Lyrics.AiModelId) ?? AiLyricsModelCatalog.ExperimentalPlain;
    private string ModelLabel(AiLyricsModelDescriptor model) => _strings.Get(
        model.Id == AiLyricsModelCatalog.ExperimentalPlain.Id ? "AiLyricsPlainBetaModel" :
        model.Id == AiLyricsModelCatalog.ExperimentalLargePlain.Id ? "AiLyricsLargePlainBetaModel" :
        model.Id == AiLyricsModelCatalog.Standard.Id ? "AiLyricsStandardModel" : "AiLyricsSmallerModel");
    private string ModelHelp(AiLyricsModelDescriptor model) => _strings.Get(
        model.Id == AiLyricsModelCatalog.ExperimentalPlain.Id ? "AiLyricsPlainBetaHelp" :
        model.Id == AiLyricsModelCatalog.ExperimentalLargePlain.Id ? "AiLyricsLargePlainBetaHelp" :
        model.Id == AiLyricsModelCatalog.Standard.Id ? "AiLyricsStandardHelp" : "AiLyricsCompactHelp");
    private string SizeLabel(AiLyricsModelDescriptor model) => _strings.Format("AiLyricsModelSize", (model.Bytes / 1_000_000_000d).ToString("0.00", _strings.Culture), model.Bytes.ToString("N0", _strings.Culture));
    private static string SourceLabel(AiLyricsModelDescriptor model)
    {
        var segments = model.DownloadUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return model.DownloadUri.Host + "/" + string.Join('/', segments.Take(2));
    }

    private void ShowError(Exception exception, string key)
    {
        Debug.WriteLine($"DropSpace AI lyrics UI failed: {exception.GetType().Name}");
        _errorMessage = _strings.Get(key);
    }

    private void Refresh()
    {
        _syncing = true;
        try
        {
            var settings = _editor.Settings.Lyrics;
            _enabled.IsOn = settings.AiTranslationEnabled;
            _gpuAcceleration.IsOn = settings.AiLyricsGpuAccelerationEnabled;
            // Retain the candidate while its confirmation/download is running.
            if (!_busy)
                _models.SelectedItem = _models.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
                    item.Tag is AiLyricsModelDescriptor model && model.Id == settings.AiModelId) ?? _models.Items.OfType<ComboBoxItem>().FirstOrDefault();
            var selected = SelectedModel();
            var installed = _installed.Contains(selected.Id);
            _details.Text = _strings.Format("AiLyricsModelDetails", selected.Name, SizeLabel(selected), SourceLabel(selected)) + "\n" + ModelHelp(selected);
            _status.Text = _inspecting ? _strings.Get("AiLyricsChecking") : !string.IsNullOrEmpty(_message) ? _message :
                installed ? _strings.Get("AiLyricsReady") : _strings.Get("AiLyricsNeedsDownload");
            if (installed && settings.AiTranslationEnabled && !_busy && !_inspecting && string.IsNullOrEmpty(_message))
                _status.Text = _strings.Get(_service.TranslationState switch
                {
                    AiLyricsTranslationState.Translating => "AiLyricsTranslating",
                    AiLyricsTranslationState.Unavailable => "AiLyricsTemporarilyUnavailable",
                    AiLyricsTranslationState.ResourcesUnavailable => "AiLyricsResourcesUnavailable",
                    AiLyricsTranslationState.Completed => "AiLyricsTranslationComplete",
                    _ => "AiLyricsReady",
                });
            if (_service.TranslationPaused && settings.AiTranslationEnabled && !_busy && !_inspecting)
                _status.Text = _strings.Get("AiLyricsTranslationPaused");
            _resume.Visibility = _service.TranslationPaused && settings.AiTranslationEnabled ? Visibility.Visible : Visibility.Collapsed;
            _resume.IsEnabled = !_busy && !_inspecting;
            _delete.Visibility = installed || _removable.Contains(selected.Id) || _interrupted.Contains(selected.Id) ? Visibility.Visible : Visibility.Collapsed;
            _delete.IsEnabled = !_busy && !_inspecting;
            _clearCache.IsEnabled = !_busy && !_inspecting;
            foreach (var (legacyId, remove) in _legacyDeleteButtons)
            {
                remove.Visibility = _removable.Contains(legacyId) ? Visibility.Visible : Visibility.Collapsed;
                remove.IsEnabled = !_busy && !_inspecting;
            }
            _legacyModels.Visibility = _legacyDeleteButtons.Keys.Any(legacyId => _removable.Contains(legacyId)) ? Visibility.Visible : Visibility.Collapsed;
            _error.Text = !string.IsNullOrEmpty(_errorMessage) ? _errorMessage :
                _service.CacheMigrationFailed ? _strings.Get("LyricsCacheMigrationFailed") : string.Empty;
            _error.Visibility = string.IsNullOrEmpty(_error.Text) ? Visibility.Collapsed : Visibility.Visible;
            _enabled.IsEnabled = !_busy && !_inspecting;
            _gpuAcceleration.IsEnabled = !_busy && !_inspecting;
            _models.IsEnabled = !_busy && !_inspecting;
            _download.Content = _strings.Get(_interrupted.Contains(selected.Id) ? "AiLyricsResumeDownload" : "AiLyricsDownload");
            _download.Visibility = !installed && !_downloading ? Visibility.Visible : Visibility.Collapsed;
            _download.IsEnabled = !_busy && !_inspecting;
            _cancel.Visibility = _downloading ? Visibility.Visible : Visibility.Collapsed;
            _cancel.IsEnabled = _downloading && _downloadStop is { IsCancellationRequested: false };
            _progress.Visibility = _downloading ? Visibility.Visible : Visibility.Collapsed;
            _glow.Mode = _desiredGlow ?? settings.GlowMode;
            _glow.IsEnabled = !_busy;
            // Include the model in progress accessibility, without putting implementation terms in buttons.
            if (_downloadModelId is { } id && AiLyricsModelCatalog.Find(id) is { } downloadingModel)
                AutomationProperties.SetName(_progress, _strings.Format("AiLyricsModelProgress", ModelLabel(downloadingModel)));
        }
        finally { _syncing = false; }
    }
    private sealed class PercentageProgress(IProgress<double> target) : IProgress<double>
    {
        private int _lastPercent = -1;
        public void Report(double value)
        {
            if (!double.IsFinite(value)) return;
            var percent = (int)Math.Clamp(Math.Floor(value * 100), 0, 100);
            if (percent == _lastPercent) return;
            _lastPercent = percent;
            target.Report(Math.Clamp(value, 0, 1));
        }
    }

}
