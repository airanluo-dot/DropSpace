using System.ComponentModel;
using System.Text.Json;
using DropSpace.App.Services.Media;
using DropSpace.App.Services.Volume;
using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Island;
using DropSpace.Core.Media;
using DropSpace.Core.Models;
using DropSpace.Core.Overlay;
using DropSpace.Core.SystemActivities;
using DropSpace.Infrastructure.Downloads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DropSpace.App.Services.Diagnostics;

/// <summary>
/// Explicit local acceptance entry point. Media and lyrics are synthetic; windows,
/// D3D presentation, settings transactions, RegisterHotKey and endpoint volume are real.
/// It never initializes media capture, startup registration, updates or the user's profile.
/// </summary>
internal static partial class MusicWindowCheck
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static async Task<int> RunAsync(string output, Func<string, ServiceProvider> createServices, string? focus = null)
    {
        if (focus is not (null or "baseline" or "interactive" or "portrait" or "beta2" or "beta2-ui"))
            throw new ArgumentException("Music-check focus must be baseline, interactive, portrait, beta2 or beta2-ui.", nameof(focus));
        var runBaseline = focus is null or "baseline";
        output = Path.GetFullPath(output);
        if (Directory.Exists(output) || File.Exists(output)) return 3;
        Directory.CreateDirectory(output);
        ServiceProvider? services = null;
        MainWindow? host = null;
        MainViewModel? main = null;
        PropertyChangedEventHandler? projectSettings = null;
        WindowsVolumeActivityService? volume = null;
        OverlayWindowService? overlay = null;
        VolumeActivitySnapshot? originalVolume = null;
        EventHandler? musicInvocation = null, quickInvocation = null;
        var musicInvocationCount = 0;
        var quickInvocationCount = 0;
        var volumeOwner = new object();
        var islandOwner = new object();
        var ledger = new List<CheckResult>();
        string? fatal = null;
        void Check(bool condition, string category, string description, object? evidence = null) =>
            ledger.Add(new(condition ? "passed" : "failed", category, description, evidence));
        void Unverified(string category, string description, object? evidence = null) =>
            ledger.Add(new("unverified", category, description, evidence));
        try
        {
            services = createServices(Path.Combine(output, "profile"));
            var settings = BetaUiDiagnosticFixture.Settings with
            {
                Language = AppLanguagePreference.SimplifiedChinese,
                QuickPanelHotkey = "Ctrl+Alt+F11", MusicWindowHotkey = string.Empty,
                CaptureFiles = false, CaptureImages = false,
                FileDragWakeMode = FileDragWakeMode.Disabled, OverlayMotion = OverlayMotionPreference.Full,
                LyricsTranslationTarget = LyricsTranslationTarget.SimplifiedChinese,
                Lyrics = new() { Enabled = true, SecondaryLyrics = true },
            };
            await services.GetRequiredService<ISettingsService>().SaveAsync(settings);
            services.GetRequiredService<AppLanguageService>().Apply(settings.Language);
            XamlResourceOverride.Initialize(services.GetRequiredService<IAppStringLocalizer>());
            main = services.GetRequiredService<MainViewModel>();
            BetaUiDiagnosticFixture.ApplySettings(main, settings);
            var media = services.GetRequiredService<MediaViewModel>();
            media.Settings = settings;
            media.IsReducedMotion = false;
            // Production MediaExperienceService owns this projection. The fixture
            // supplies only the projection so no real session can overwrite its data.
            projectSettings = (_, args) => { if (args.PropertyName == nameof(MainViewModel.Settings)) media.Settings = main.Settings; };
            main.PropertyChanged += projectSettings;
            host = ActivatorUtilities.CreateInstance<MainWindow>(services);
            host.Activate();
            var presenter = (OverlappedPresenter)host.AppWindow.Presenter;
            if (runBaseline)
            {
                host.AppWindow.Resize(new SizeInt32(100, 100));
                await Task.Delay(250);
                Check(host.AppWindow.Size.Width >= Convert.ToInt32(presenter.PreferredMinimumWidth) && host.AppWindow.Size.Height >= Convert.ToInt32(presenter.PreferredMinimumHeight),
                    "native-main-window", "Actual main window clamps undersized requests to its DPI-aware minimum size",
                    new { actualWidth = host.AppWindow.Size.Width, actualHeight = host.AppWindow.Size.Height, minimumWidth = presenter.PreferredMinimumWidth, minimumHeight = presenter.PreferredMinimumHeight });
            }
            host.AppWindow.Resize(new SizeInt32(1120, 800));
            host.AppWindow.Hide(); // Retain a host while testing music-window close/reopen.
            var music = services.GetRequiredService<MusicWindowService>();
            musicInvocation = (_, _) => host.DispatcherQueue.TryEnqueue(() => { musicInvocationCount++; music.Open(); });
            quickInvocation = (_, _) => host.DispatcherQueue.TryEnqueue(() => quickInvocationCount++);
            services.GetRequiredService<GlobalMusicWindowHotkeyService>().Invoked += musicInvocation;
            services.GetRequiredService<GlobalQuickPanelHotkeyService>().Invoked += quickInvocation;
            var artwork = await CreateArtworkAsync();
            await File.WriteAllBytesAsync(Path.Combine(output, "synthetic-artwork.png"), artwork.Bytes);
            var lines = FixtureLyrics();
            if (focus is "beta2" or "beta2-ui")
            {
                lines = TimedFixtureLyrics(lines);
                await main.AddPathsAsync([Path.Combine(output, "synthetic-artwork.png")]);
                await main.AddTextToSpaceAsync("Beta2 中文检索与长文本参照\n" + string.Join('\n', Enumerable.Range(1, 80).Select(i => $"Line {i}: an isolated reference document.")), "beta2-local-check");
            }
            var fixture = new MediaSessionSnapshot("isolated-synthetic-session", "DropSpace.SyntheticFixture", "Synthetic fixture",
                "Evening in the window", "DropSpace · synthetic media", "Local verification", artwork.Bytes,
                MediaPlaybackState.Playing, true, true, true, true, true,
                new(TimeSpan.FromSeconds(44), TimeSpan.Zero, TimeSpan.FromSeconds(236), 1, DateTimeOffset.UtcNow), DateTimeOffset.UtcNow);
            void ApplyFixture(bool playing = true, bool lyrics = true, bool withArtwork = true, bool estimated = false, bool nextTrack = false)
            {
                media.Session = fixture with
                {
                    TrackTitle = nextTrack ? "Another quiet morning" : fixture.TrackTitle,
                    Artwork = withArtwork ? artwork.Bytes : null,
                    PlaybackState = playing ? MediaPlaybackState.Playing : MediaPlaybackState.Paused,
                };
                media.Artwork = withArtwork ? artwork.Image : null;
                media.PositionEstimated = estimated;
                media.Position = TimeSpan.FromSeconds(44);
                media.Spectrum = SpectrumFrame.Empty; // No invented capture or fake audio response.
                if (lyrics)
                {
                    media.SetLyricsDocument(new(lines, LyricsProviderKind.LocalLrc));
                    media.LyricsStatus = LyricsQueryStatus.Found;
                    media.Lyrics = focus is "beta2" or "beta2-ui" ? new LyricsTimelineEngine().GetFrame(new(lines, LyricsProviderKind.LocalLrc), media.Position, 0) : new(lines[2], -1, 0, DateTime.UtcNow.Ticks);
                }
                else
                {
                    media.SetLyricsDocument(LyricsDocument.Empty);
                    media.Lyrics = LyricsHighlightFrame.Empty;
                    media.LyricsStatus = LyricsQueryStatus.NotFound;
                }
            }
            JsonElement Layout() => JsonSerializer.SerializeToElement(music.CurrentWindow?.LayoutSnapshot ?? new { });
            object? IslandSnapshot() => overlay is null ? null : new
            {
                presentation = overlay.DiagnosticActiveWindow.ObserveModuleOverlay(),
                toolbar = overlay.DiagnosticActiveWindow.ObserveIslandToolbar(),
            };
            async Task SnapshotAsync(string name) => await WriteAsync(Path.Combine(output, name + ".json"), new
            {
                syntheticMedia = true, state = music.GetState(), layout = Layout(),
                island = IslandSnapshot(),
                mainWindow = new { host.AppWindow.Size.Width, host.AppWindow.Size.Height, presenter.PreferredMinimumWidth, presenter.PreferredMinimumHeight },
                nativeHotkeyInvocations = new { music = musicInvocationCount, quick = quickInvocationCount },
                media = new { media.Title, media.Artist, media.IsPlaying, media.PositionEstimated, media.CurrentLyricIndex,
                    media.IsPresentationVisible, media.IsSpectrumPresentationVisible, media.IsIslandGlowActive, media.IsMusicWindowAudioActive, captureMode = media.Spectrum.CaptureMode.ToString() },
            });

            if (runBaseline)
            {
            await InventoryRealSessionsAsync(output, Unverified, Check);
            ApplyFixture();
            music.Open();
            var first = music.CurrentWindow!;
            music.Open();
            Check(ReferenceEquals(first, music.CurrentWindow), "native-window", "Repeated explicit open reuses the same native music window");
            await UntilAsync(() => music.GetState().BackgroundReady && Layout().TryGetProperty("LyricRows", out var rows) && rows.GetInt32() == lines.Length, 8_000);
            var playing = music.GetState();
            Check(playing.IsOpen && playing.IsPlaying && playing.TrackIdentity == media.Session.TrackIdentity && playing.SeekEnabled,
                "synthetic-media-real-ui", "Music window presents the shared singleton session and an exact seekable timeline", playing);
            Check(Layout().GetProperty("TranslationDisplayed").GetBoolean(), "synthetic-media-real-ui", "Provider-tagged synthetic Chinese translations are rendered");
            var frame = first.Background.FrameCount;
            await Task.Delay(650);
            Check(first.Background.IsReady && first.Background.IsRendering && first.Background.FrameCount > frame && first.Background.LastFault is null,
                "real-gpu", "D3D background presents advancing frames on the actual native window",
                new { start = frame, end = first.Background.FrameCount, first.Background.IsReady, first.Background.IsRendering, fault = first.Background.LastFault });
            await SnapshotAsync("playing");

            ApplyFixture(playing: false);
            await UntilAsync(() => !music.GetState().IsPlaying && music.GetState().CoverScale < 1);
            Check(!music.GetState().IsPlaying && Math.Abs(music.GetState().CoverScale - 0.76) < 0.01,
                "synthetic-media-real-ui", "A shared paused session shrinks the same cover to its paused scale", music.GetState());
            await SnapshotAsync("paused");
            ApplyFixture();
            music.SetLyricsVisible(false);
            await Task.Delay(450);
            var centered = Layout();
            Check(!music.GetState().LyricsVisible && !centered.GetProperty("LyricsDisplayed").GetBoolean() &&
                Math.Abs(centered.GetProperty("LeftX").GetDouble() + centered.GetProperty("LeftWidth").GetDouble() / 2 - centered.GetProperty("RootWidth").GetDouble() / 2) < 3,
                "native-layout", "Hiding lyrics centers the complete cover and transport group", centered);
            await SnapshotAsync("hidden");
            music.SetLyricsVisible(true);
            await music.SetTranslationEnabledAsync(false);
            await Task.Delay(200);
            var saved = await services.GetRequiredService<ISettingsService>().LoadAsync();
            Check(!saved.Lyrics.SecondaryLyrics && !main.Settings.Lyrics.SecondaryLyrics && !media.Settings.Lyrics.SecondaryLyrics &&
                !Layout().GetProperty("TranslationDisplayed").GetBoolean(), "real-settings-synthetic-projection",
                "Translation toggle persists through the existing settings transaction and updates the shared preference");
            await SnapshotAsync("original");
            await music.SetTranslationEnabledAsync(true);

            ApplyFixture(estimated: true);
            await UntilAsync(() => !music.GetState().SeekEnabled);
            Check(!music.GetState().SeekEnabled, "synthetic-media-real-ui", "An estimated timeline disables the real seek control");
            ApplyFixture(lyrics: false, withArtwork: false);
            await Task.Delay(250);
            Check(Layout().GetProperty("LyricRows").GetInt32() == 0 && !Layout().GetProperty("ArtworkDisplayed").GetBoolean(),
                "synthetic-media-real-ui", "Unavailable lyrics and artwork show their empty states without old content", Layout());
            await SnapshotAsync("fallback");
            ApplyFixture();
            await Task.Delay(150);
            // Change only the track identity first: the original document is still
            // retained in the shared VM, but cannot be displayed for the new track.
            media.Session = media.Session with { TrackTitle = "Another quiet morning", Artwork = null };
            media.Artwork = null;
            await Task.Delay(200);
            Check(media.LyricsLines.Count == 0 && Layout().GetProperty("LyricRows").GetInt32() == 0,
                "synthetic-media-real-ui", "A new track identity rejects the previous track's lyric rows before new lyrics arrive");

            ApplyFixture();
            media.SetIslandGlowActive(islandOwner, true);
            media.SetPresentationVisible(islandOwner, true);
            await Task.Delay(150);
            first.SetMinimized(true);
            await UntilAsync(() => music.GetState().IsMinimized && !media.IsMusicWindowAudioActive);
            var stoppedFrame = first.Background.FrameCount;
            await Task.Delay(450);
            Check(!media.IsMusicWindowAudioActive && media.IsIslandGlowActive && media.IsPresentationVisible && media.IsSpectrumPresentationVisible,
                "native-lifetime", "Minimizing releases music-window audio ownership and retains the island's independent owner");
            Check(!first.Background.IsRendering && first.Background.FrameCount == stoppedFrame,
                "real-gpu", "Minimized native window stops D3D presentation", new { start = stoppedFrame, end = first.Background.FrameCount });
            music.Open();
            await UntilAsync(() => !music.GetState().IsMinimized && media.IsMusicWindowAudioActive && first.Background.FrameCount > stoppedFrame);
            Check(ReferenceEquals(first, music.CurrentWindow) && media.IsMusicWindowAudioActive && first.Background.FrameCount > stoppedFrame,
                "native-lifetime", "Explicit reopen restores the same minimized window and resumes GPU presentation");
            music.Close();
            await Task.Delay(200);
            var closedFrame = first.Background.FrameCount;
            await Task.Delay(200);
            Check(music.CurrentWindow is null && !media.IsMusicWindowAudioActive && media.IsIslandGlowActive &&
                !first.Background.IsRendering && first.Background.FrameCount == closedFrame,
                "native-lifetime", "Closing retires the window renderer and only its own audio ownership");
            media.SetIslandGlowActive(islandOwner, false);
            media.SetPresentationVisible(islandOwner, false);
            music.Open();
            await UntilAsync(() => music.GetState().BackgroundReady);
            Check(!ReferenceEquals(first, music.CurrentWindow) && music.GetState().IsOpen,
                "native-lifetime", "Opening after close creates a new native window using the same shared media object");

            volume = services.GetRequiredService<WindowsVolumeActivityService>();
            await volume.SetControlActiveAsync(volumeOwner, true);
            await UntilAsync(() => volume.IsAvailable && volume.Current is not null, 3_000);
            if (volume.IsAvailable && volume.Current is { } currentVolume)
            {
                originalVolume = currentVolume;
                try
                {
                    var reduced = Math.Max(0, currentVolume.Percent - 1);
                    var changed = await volume.SetMasterVolumeAsync(reduced / 100d);
                    Check(Math.Abs(changed.Percent - reduced) <= 1, "real-windows-volume",
                        "Native default endpoint accepts a one-percentage-point volume reduction (clamped at zero)",
                        new { original = currentVolume.Percent, requested = reduced, actual = changed.Percent });
                }
                finally
                {
                    await volume.SetMasterVolumeAsync(currentVolume.Percent / 100d);
                    var restored = await volume.SetMutedAsync(currentVolume.Muted);
                    Check(Math.Abs(restored.Percent - currentVolume.Percent) <= 1 && restored.Muted == currentVolume.Muted,
                        "real-windows-volume", "System master volume and mute are restored to the original values", restored);
                    originalVolume = null; // Do not overwrite later user changes during screenshot hold.
                }
            }
            else Unverified("real-windows-volume", "No available default Windows render endpoint; system-volume mutation was not attempted");
            await VerifyHotkeysAsync(services, main, Check, Unverified);
            Unverified("real-player", "Synthetic playback states do not verify real player play/pause/seek or process-loopback capture; no user's playback was started or changed");
            }

            // The regular overlay owner installs its regular quick-retrieval route.
            // Clipboard capture and MediaExperience initialization remain disabled.
            var experience = services.GetRequiredService<IslandExperienceCoordinator>();
            var overlayState = services.GetRequiredService<OverlayStateMachine>();
            async Task EnsureOverlayAsync()
            {
                if (overlay is not null) return;
                overlay = services.GetRequiredService<OverlayWindowService>();
                await overlay.InitializeAsync(host.ShowAndActivate);
            }
            async Task ShowIslandAsync(IslandPage page)
            {
                await EnsureOverlayAsync();
                music.CurrentWindow?.SetMinimized(true);
                host.AppWindow.Hide();
                experience.UpdateMedia(media.Session.IsActive, media.IsPlaying, true, 3_000, media.Session.TrackIdentity);
                overlayState.OpenQuickPanel();
                overlay!.DiagnosticActiveWindow.DiagnosticShowPage(page);
                await Task.Delay(500);
            }
            if (runBaseline)
            {
            await EnsureOverlayAsync();
            Check(overlay!.DiagnosticSurfaceCount > 0, "native-island", "Regular overlay surfaces initialize for native four-page and retrieval screenshots without media or clipboard startup", new { surfaces = overlay.DiagnosticSurfaceCount });
            foreach (var page in Enum.GetValues<IslandPage>())
            {
                await ShowIslandAsync(page);
                var toolbar = JsonSerializer.SerializeToElement(overlay.DiagnosticActiveWindow.ObserveIslandToolbar());
                var search = toolbar.GetProperty("Search");
                var collapse = toolbar.GetProperty("Collapse");
                Check(search.GetProperty("Visible").GetBoolean() && collapse.GetProperty("Visible").GetBoolean() &&
                    search.GetProperty("HitTestReachesButton").GetBoolean() && collapse.GetProperty("HitTestReachesButton").GetBoolean() &&
                    search.GetProperty("X").GetDouble() + search.GetProperty("Width").GetDouble() <= collapse.GetProperty("X").GetDouble() &&
                    Math.Abs(search.GetProperty("Y").GetDouble() - collapse.GetProperty("Y").GetDouble()) < 1 &&
                    search.GetProperty("Y").GetDouble() + search.GetProperty("Height").GetDouble() <= toolbar.GetProperty("ContentTop").GetDouble() + 1,
                    "native-island", $"{page}: shared search and collapse buttons remain visible, separate, above content and reachable by native hit testing", toolbar);
                await SnapshotAsync("island-" + page.ToString().ToLowerInvariant());
            }
            experience.DismissNow();
            }

            ApplyFixture();
            music.SetLyricsVisible(true);
            await music.SetTranslationEnabledAsync(true);
            music.Open();
            await Task.Delay(200);
            if (focus == "portrait")
            {
                var portrait = await VerifyPortraitAsync(Path.Combine(output, "portrait-check"), music, media);
                Check(portrait.Status == "passed", "native-portrait-focused", "Dedicated portrait and actual-animation checks completed; see their separate receipt", portrait);
            }
            if (focus == "beta2")
            {
                await EnsureOverlayAsync();
                var receipt = await VerifyBeta2Async(Path.Combine(output, "beta2-check"), music, media, lines, services, overlay!);
                Check(receipt.Status == "passed", "native-beta2-focused", "Word rendering, focus effects, retrieval and native lifetime checks; see the scoped receipt", receipt);
            }
            await SnapshotAsync("current");
            await SaveLedgerAsync();
            await WriteAsync(Path.Combine(output, "ready.txt"), new
            {
                syntheticMedia = true, focus = focus ?? "baseline", baselineExecuted = runBaseline, state = music.GetState(), quickHotkey = main.Settings.QuickPanelHotkey,
                musicHotkey = main.Settings.MusicWindowHotkey,
                control = "Write control.json with {name: playing|paused|hidden|original|no-lyrics|missing-art|estimated|next-track|minimize|restore|close|open|main|mainminimum|shortcuts|island-widgets|island-music|island-files|island-clipboard|retrieval|island-hide|portrait-cover|portrait-lyrics|landscape|wake|portrait-check|layout-check|session-probe|snapshot, outputSuffix?: string}; finish.txt exits.",
            });
            var deadline = DateTime.UtcNow.AddMinutes(25);
            while (!File.Exists(Path.Combine(output, "finish.txt")) && DateTime.UtcNow < deadline)
            {
                var commandPath = Path.Combine(output, "control.json");
                if (File.Exists(commandPath))
                {
                    string? name = null;
                    object? separateProbe = null;
                    try
                    {
                        using var command = JsonDocument.Parse(await File.ReadAllTextAsync(commandPath));
                        name = command.RootElement.GetProperty("name").GetString();
                        File.Delete(commandPath);
                        switch (name)
                        {
                            case "playing": ApplyFixture(); music.Open(); music.SetLyricsVisible(true); await music.SetTranslationEnabledAsync(true); break;
                            case "paused": ApplyFixture(playing: false); music.Open(); music.SetLyricsVisible(true); await music.SetTranslationEnabledAsync(true); break;
                            case "hidden": ApplyFixture(); music.Open(); music.SetLyricsVisible(false); break;
                            case "original": ApplyFixture(); music.Open(); music.SetLyricsVisible(true); await music.SetTranslationEnabledAsync(false); break;
                            case "no-lyrics": ApplyFixture(lyrics: false); music.Open(); music.SetLyricsVisible(true); break;
                            case "missing-art": ApplyFixture(withArtwork: false); music.Open(); break;
                            case "estimated": ApplyFixture(estimated: true); music.Open(); break;
                            case "next-track": ApplyFixture(lyrics: false, withArtwork: false, nextTrack: true); music.Open(); break;
                            case "minimize": music.CurrentWindow?.SetMinimized(true); break;
                            case "restore": case "open": music.Open(); break;
                            case "close": music.Close(); break;
                            case "main": await main.NavigateAsync("Music"); host.ShowAndActivate(); break;
                            case "mainminimum": await main.NavigateAsync("Music"); host.ShowAndActivate(); host.AppWindow.Resize(new SizeInt32(100, 100)); break;
                            case "shortcuts": host.ShowAndActivate(); await host.DiagnosticPage.OpenSettingsPageAsync("Shortcuts"); break;
                            case "island-widgets": await ShowIslandAsync(IslandPage.Widgets); break;
                            case "island-music": await ShowIslandAsync(IslandPage.Music); break;
                            case "island-files": await ShowIslandAsync(IslandPage.Files); break;
                            case "island-clipboard": await ShowIslandAsync(IslandPage.Clipboard); break;
                            case "retrieval": await EnsureOverlayAsync(); host.AppWindow.Hide(); music.CurrentWindow?.SetMinimized(true); overlay!.OpenRetrieval(); break;
                            case "island-hide": experience.DismissNow(); break;
                            case "portrait-cover": music.Open(); ResizeMusicDips(music.CurrentWindow!, 480, 800); music.SetLyricsVisible(false); music.CurrentWindow!.DiagnosticNotifyInteraction(); break;
                            case "portrait-lyrics": music.Open(); ResizeMusicDips(music.CurrentWindow!, 480, 800); music.SetLyricsVisible(true); music.CurrentWindow!.DiagnosticNotifyInteraction(); break;
                            case "landscape": music.Open(); ResizeMusicDips(music.CurrentWindow!, 1040, 700); music.CurrentWindow!.DiagnosticNotifyInteraction(); break;
                            case "wake": music.CurrentWindow?.DiagnosticNotifyInteraction(); break;
                            case "portrait-check": music.Open(); separateProbe = await VerifyPortraitAsync(ProbeOutput(output, "portrait-check", command.RootElement), music, media); break;
                            case "layout-check": ApplyFixture(); separateProbe = await VerifyLayoutActionsAsync(ProbeOutput(output, "layout-check", command.RootElement), music); break;
                            case "beta2-check": await EnsureOverlayAsync(); separateProbe = await VerifyBeta2Async(ProbeOutput(output, "beta2-check", command.RootElement), music, media, lines, services, overlay!); break;
                            case "beta2-edges": separateProbe = await VerifyBeta2EdgesAsync(ProbeOutput(output, "beta2-edges", command.RootElement), music, media); break;
                            case "beta2-spacing": separateProbe = await VerifyBeta2SpacingAsync(ProbeOutput(output, "beta2-spacing", command.RootElement), music); break;
                            case "beta2-focus": separateProbe = await VerifyBeta2FocusAsync(ProbeOutput(output, "beta2-focus", command.RootElement), music); break;
                            case "beta2-performance": await ShowIslandAsync(IslandPage.Music); separateProbe = await VerifyBeta2PerformanceAsync(ProbeOutput(output, "beta2-performance", command.RootElement), music, media, lines, experience, overlay!); break;
                            case "session-probe": separateProbe = await MusicSessionProbe.RunAsync(ProbeOutput(output, "session-probe", command.RootElement), services.GetRequiredService<ILoggerFactory>()); break;
                            case "snapshot": break;
                            default: throw new ArgumentException("Unknown diagnostic state.");
                        }
                        await Task.Delay(450);
                        await SnapshotAsync("current");
                        await WriteAsync(Path.Combine(output, "command-result.json"), new { status = "applied", name, syntheticMedia = true, state = music.GetState(), layout = Layout(), island = IslandSnapshot(), separateProbe, nativeHotkeyInvocations = new { music = musicInvocationCount, quick = quickInvocationCount } });
                    }
                    catch (Exception exception)
                    {
                        await WriteAsync(Path.Combine(output, "command-result.json"), new { status = "failed", name, error = exception.ToString() });
                        if (File.Exists(commandPath)) File.Delete(commandPath);
                    }
                }
                await Task.Delay(150);
            }

            async Task SaveLedgerAsync() => await WriteAsync(Path.Combine(output, "checks.json"), new
            {
                status = ledger.Any(check => check.Status == "failed") ? "failed" : ledger.Count == 0 ? "interactive_no_checks_run" :
                    ledger.Any(check => check.Status == "unverified") ? "passed_with_unverified_items" : "passed",
                syntheticMedia = true, focus = focus ?? "baseline", baselineExecuted = runBaseline, checks = ledger, nativeHotkeyInvocations = new { music = musicInvocationCount, quick = quickInvocationCount },
            });
        }
        catch (Exception error) { fatal = error.ToString(); }
        finally
        {
            if (main is not null && projectSettings is not null) main.PropertyChanged -= projectSettings;
            if (volume is not null && originalVolume is not null)
            {
                try
                {
                    await volume.SetMasterVolumeAsync(originalVolume.Percent / 100d);
                    await volume.SetMutedAsync(originalVolume.Muted);
                    ledger.Add(new("passed", "real-windows-volume", "Final cleanup restored original system volume and mute", null));
                }
                catch (Exception exception) { ledger.Add(new("failed", "real-windows-volume", "Final volume restoration failed", exception.ToString())); }
            }
            if (services is not null)
            {
                try
                {
                    services.GetService<MusicWindowService>()?.Dispose();
                    overlay?.Dispose();
                    if (services.GetService<MediaViewModel>() is { } media)
                    { media.SetIslandGlowActive(islandOwner, false); media.SetPresentationVisible(islandOwner, false); }
                    if (volume is not null) await volume.SetControlActiveAsync(volumeOwner, false);
                    if (musicInvocation is not null) services.GetRequiredService<GlobalMusicWindowHotkeyService>().Invoked -= musicInvocation;
                    if (quickInvocation is not null) services.GetRequiredService<GlobalQuickPanelHotkeyService>().Invoked -= quickInvocation;
                    await services.GetRequiredService<GlobalMusicWindowHotkeyService>().StopAsync();
                    await services.GetRequiredService<GlobalQuickPanelHotkeyService>().StopAsync();
                    await services.GetRequiredService<DownloadManager>().ShutdownAsync();
                    host?.PrepareForShutdown();
                    await services.DisposeAsync();
                }
                catch (Exception exception) { ledger.Add(new("failed", "native-lifetime", "Diagnostic service cleanup failed", exception.ToString())); }
            }
            await WriteAsync(Path.Combine(output, "checks.json"), new
            {
                status = fatal is not null || ledger.Any(check => check.Status == "failed") ? "failed" : ledger.Count == 0 ? "interactive_no_checks_run" :
                    ledger.Any(check => check.Status == "unverified") ? "passed_with_unverified_items" : "passed",
                syntheticMedia = true, focus = focus ?? "baseline", baselineExecuted = runBaseline, checks = ledger, error = fatal, nativeHotkeyInvocations = new { music = musicInvocationCount, quick = quickInvocationCount },
            });
            host?.AllowCloseAndClose();
        }
        return fatal is null && ledger.All(check => check.Status != "failed") ? 0 : 1;
    }

    private static async Task VerifyHotkeysAsync(ServiceProvider services, MainViewModel main,
        Action<bool, string, string, object?> check, Action<string, string, object?> unverified)
    {
        var quick = services.GetRequiredService<GlobalQuickPanelHotkeyService>();
        var music = services.GetRequiredService<GlobalMusicWindowHotkeyService>();
        var candidates = new[] { "Ctrl+Alt+F11", "Ctrl+Alt+F10", "Ctrl+Alt+F9", "Ctrl+Alt+F12", "Ctrl+Shift+F11", "Ctrl+Shift+F10" };
        var available = candidates.Where(quick.CanRegister).Take(3).ToArray();
        if (available.Length < 3) { unverified("real-hotkey", "Fewer than three test gestures were free; native conflict/rollback test was not attempted", null); return; }
        var configured = main.Settings with { QuickPanelHotkey = available[0], MusicWindowHotkey = available[1] };
        await main.UpdateSettingsAsync(configured);
        // An unchanged quick gesture is not re-registered by a settings transaction.
        await quick.StartAsync(configured.QuickPanelHotkey);
        check(quick.IsRegistered && music.IsRegistered, "real-hotkey", "Both independent global actions register concurrently through native RegisterHotKey", new { quick = configured.QuickPanelHotkey, music = configured.MusicWindowHotkey });
        await main.UpdateSettingsAsync(main.Settings with { MusicWindowHotkey = string.Empty });
        check(quick.IsRegistered && !music.IsRegistered && main.Settings.MusicWindowHotkey.Length == 0,
            "real-hotkey", "Clearing music's saved binding unregisters only the music action", null);
        await main.UpdateSettingsAsync(main.Settings with { MusicWindowHotkey = available[1] });
        await using var contender = new GlobalQuickPanelHotkeyService(services.GetRequiredService<ILogger<GlobalQuickPanelHotkeyService>>(), 0x4460, "DropSpace diagnostic occupied gesture");
        if (await contender.TryStartAsync(available[2]))
        {
            var rejected = false;
            try { await main.UpdateSettingsAsync(main.Settings with { MusicWindowHotkey = available[2] }); }
            catch (SettingsUpdateException) { rejected = true; }
            var saved = await services.GetRequiredService<ISettingsService>().LoadAsync();
            check(rejected && saved.MusicWindowHotkey == available[1] && music.IsRegistered && quick.IsRegistered,
                "real-hotkey", "A genuinely occupied native gesture rejects Save and preserves both the previous music binding and quick retrieval", new { saved.MusicWindowHotkey });
        }
        else unverified("real-hotkey", "A competing process claimed the conflict-test gesture before reservation", null);
        await main.UpdateSettingsAsync(main.Settings with { QuickPanelHotkey = available[1], MusicWindowHotkey = available[0] });
        check(quick.IsRegistered && music.IsRegistered && main.Settings.QuickPanelHotkey == available[1] && main.Settings.MusicWindowHotkey == available[0],
            "real-hotkey", "One settings transaction can swap both native global gestures without a self-conflict", null);
        await main.UpdateSettingsAsync(main.Settings with { QuickPanelHotkey = available[0], MusicWindowHotkey = available[1] });
        unverified("physical-input", "Native registration and rollback are exercised; no physical shortcut, recorder Cancel or external keyboard event is simulated", null);
    }

    private static async Task InventoryRealSessionsAsync(string output, Action<string, string, object?> unverified,
        Action<bool, string, string, object?> check)
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var inventory = new List<object>();
            foreach (var session in manager.GetSessions())
            {
                try
                {
                    var properties = await session.TryGetMediaPropertiesAsync();
                    var info = session.GetPlaybackInfo();
                    inventory.Add(new { source = session.SourceAppUserModelId, properties.Title, properties.Artist,
                        state = info.PlaybackStatus.ToString(), canPlay = info.Controls.IsPlayEnabled,
                        canPause = info.Controls.IsPauseEnabled, canSeek = info.Controls.IsPlaybackPositionEnabled });
                }
                catch (Exception error) { inventory.Add(new { source = session.SourceAppUserModelId, error = error.GetType().Name }); }
            }
            await WriteAsync(Path.Combine(output, "real-session-inventory.json"), new { readOnly = true, sessions = inventory });
            check(true, "real-session-readonly", "Actual Windows SMTC session inventory was read without controlling playback", new { count = inventory.Count });
            if (inventory.Count == 0) unverified("real-player", "Windows exposed no media sessions for additional real-player acceptance", null);
        }
        catch (Exception error) { unverified("real-session-readonly", "Actual SMTC inventory could not be read", error.ToString()); }
    }

    private static async Task<(byte[] Bytes, BitmapImage Image)> CreateArtworkAsync()
    {
        const uint side = 400;
        var pixels = new byte[side * side * 4];
        for (var y = 0; y < side; y++) for (var x = 0; x < side; x++)
        {
            var index = (y * side + x) * 4;
            var wave = Math.Sin(x / 67d + y / 91d) * 0.5 + 0.5;
            pixels[index] = (byte)(58 + 72 * wave);
            pixels[index + 1] = (byte)(62 + 100d * y / side);
            pixels[index + 2] = (byte)(120 + 110d * x / side);
            pixels[index + 3] = 255;
        }
        using var encoded = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, encoded);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, side, side, 96, 96, pixels);
        await encoder.FlushAsync();
        encoded.Seek(0);
        using var copy = new MemoryStream();
        await encoded.AsStreamForRead().CopyToAsync(copy);
        encoded.Seek(0);
        var image = new BitmapImage();
        await image.SetSourceAsync(encoded);
        return (copy.ToArray(), image);
    }

    private static LyricsLine[] FixtureLyrics()
    {
        string[] text = ["The evening opens at the window", "A quiet color fills the room", "We leave a little space for music", "And watch the softer light move through", "The notes are only here for testing", "The words belong to no released song", "A second line can tell the story", "The same shared rhythm carries on", "We pause and keep the moment near", "Then let the morning enter slowly", "A new page waits beyond the glass", "And every quiet color stays"];
        string[] translated = ["傍晚在窗边展开", "安静的颜色铺满房间", "给音乐留出一点空间", "看柔和的光慢慢经过", "这些音符只用于验证", "这些文字不属于任何已发行歌曲", "另一行也能讲述故事", "同一段节奏继续流动", "暂停，让此刻停留", "再让清晨缓缓走近", "玻璃外是等待翻开的新一页", "每一抹安静的颜色都留下来"];
        return text.Select((line, index) => new LyricsLine(TimeSpan.FromSeconds(index * 20), TimeSpan.FromSeconds((index + 1) * 20), line, translated[index], [])
        { SourceLanguage = "en", TranslationLanguage = "zh-CN", TranslationLanguageIsExplicit = true, TranslationOrigin = LyricsTranslationOrigin.Provider }).ToArray();
    }

    private static async Task UntilAsync(Func<bool> condition, int milliseconds = 3_000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(50);
    }
    private static Task WriteAsync(string path, object value) => File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions));
    private sealed record CheckResult(string Status, string Category, string Description, object? Evidence);
}
