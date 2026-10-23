using DropSpace.App.Services.Volume;
using DropSpace.App.ViewModels;
using DropSpace.App.Views.Music;
using DropSpace.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DropSpace.App.Services.Media;

/// <summary>Owns one explicitly opened music surface; it never owns playback.</summary>
public sealed class MusicWindowService(MediaViewModel media, NativeSettingsEditor editor,
    IAppStringLocalizer strings, WindowsVolumeActivityService volume, ILogger<MusicWindowService> logger) : IDisposable
{
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread()
        ?? throw new InvalidOperationException("Music windows require the UI dispatcher.");
    private MusicWindow? _window;
    private bool _disposed;

    public MusicWindow? CurrentWindow => _window;

    public void Open()
    {
        VerifyThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_window is null)
        {
            _window = new(media, editor, strings, volume, logger);
            _window.Closed += OnClosed;
        }
        _window.SetMinimized(false);
        // The only activation path follows an explicit UI, shortcut or Agent action.
        _window.Activate();
    }

    public void Close() { VerifyThread(); _window?.Close(); }
    public void SetLyricsVisible(bool visible) { VerifyThread(); _window?.SetLyricsVisible(visible); }
    public Task SetTranslationEnabledAsync(bool enabled)
    {
        VerifyThread();
        return _window?.SetTranslationEnabledAsync(enabled) ?? Task.CompletedTask;
    }
    public MusicWindowState GetState() => _window?.GetState() ?? new();

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (_window is not null) _window.Closed -= OnClosed;
        _window = null;
    }
    private void VerifyThread()
    {
        if (!_dispatcher.HasThreadAccess) throw new InvalidOperationException("Music presentation belongs to its UI dispatcher.");
    }
    public void Dispose()
    {
        if (_disposed) return;
        VerifyThread(); _disposed = true; Close();
    }
}

public sealed record MusicWindowState(bool IsOpen = false, bool IsMinimized = false,
    bool LyricsVisible = true, bool TranslationEnabled = false, string TrackIdentity = "",
    bool IsPlaying = false, bool SeekEnabled = false, double CoverScale = 1,
    bool BackgroundReady = false, bool AudioLeaseActive = false, long FrameCount = 0,
    long PresentedFrameCount = 0, bool IsRendering = false, string? BackgroundFault = null,
    bool Portrait = false, bool ControlsVisible = true, bool TransitionRunning = false,
    long TransitionGeneration = 0, double TransitionProgress = 1, double LyricsOpacity = 1,
    double ControlsOpacity = 1);
