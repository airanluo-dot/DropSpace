using System.ComponentModel;
using System.Text.Json;
using DropSpace.App.ViewModels;
using DropSpace.App.Services.Media;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Island;
using DropSpace.Core.Models;
using DropSpace.Core.Overlay;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Composition;
using System.Numerics;
using Windows.Graphics;

namespace DropSpace.App.Services.Diagnostics;

/// <summary>Isolated material inspection, with separately requested real music; no clipboard capture or user profile.</summary>
internal static class LiquidGlassCheck
{
    internal static async Task<int> RunAsync(string output, Func<string, ServiceProvider> createServices,
        bool enableMusic = false, string? seedSettings = null)
    {
        output = Path.GetFullPath(output);
        if (Directory.Exists(output) || File.Exists(output)) return 3;
        Directory.CreateDirectory(output);
        ServiceProvider? services = null; MainWindow? host = null; OverlayWindowService? overlay = null;
        MainViewModel? main = null; PropertyChangedEventHandler? sync = null;
        MediaExperienceService? music = null;
        string? failure = null;
        void Phase(string text) => File.AppendAllText(Path.Combine(output, "phases.txt"), text + "\n");
        void Unhandled(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args) => Phase(args.Exception.ToString());
        Application.Current.UnhandledException += Unhandled;
        try
        {
            Phase("create-services");
            services = createServices(Path.Combine(output, "profile"));
            var settings = BetaUiDiagnosticFixture.Settings with
            {
                Language = AppLanguagePreference.SimplifiedChinese,
                Theme = ThemePreference.Dark, IslandAppearance = new() { Theme = ThemePreference.Dark },
                QuickPanelHotkey = "Ctrl+Alt+F11", FileDragWakeMode = FileDragWakeMode.Disabled,
                CaptureFiles = false, CaptureImages = false, ClipboardPaused = true,
            };
            if (seedSettings is not null)
            {
                var previous = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(seedSettings))?.Validate()
                    ?? throw new InvalidDataException("The test settings seed is empty.");
                settings = settings with { IslandAppearance = previous.IslandAppearance,
                    IslandActivity = previous.IslandActivity, Lyrics = previous.Lyrics,
                    IslandContentPriority = previous.IslandContentPriority, Widgets = previous.Widgets,
                    LyricsTranslationTarget = previous.LyricsTranslationTarget,
                    LyricsTranslationTargetExplicit = previous.LyricsTranslationTargetExplicit };
            }
            if (enableMusic) settings = settings with { IslandActivity = settings.IslandActivity with { EnableMediaActivity = true } };
            settings = DropSpace.Core.Policies.LyricsTranslationTargetPolicy.ApplyDefault(settings, AppLanguageService.GetSystemLanguages());
            await services.GetRequiredService<ISettingsService>().SaveAsync(settings);
            Phase("settings-saved");
            services.GetRequiredService<AppLanguageService>().Apply(settings.Language);
            XamlResourceOverride.Initialize(services.GetRequiredService<IAppStringLocalizer>());
            Phase("resources-ready");
            main = services.GetRequiredService<MainViewModel>(); BetaUiDiagnosticFixture.ApplySettings(main, settings);
            var media = services.GetRequiredService<MediaViewModel>(); media.Settings = settings;
            sync = (_, args) => { if (args.PropertyName == nameof(MainViewModel.Settings)) media.Settings = main.Settings; };
            main.PropertyChanged += sync;
            Phase("create-main-window");
            host = ActivatorUtilities.CreateInstance<MainWindow>(services); host.Activate();
            Phase("main-visible");
            host.AppWindow.Resize(new SizeInt32(1100, 850));
            overlay = services.GetRequiredService<OverlayWindowService>(); await overlay.InitializeAsync(host.Activate);
            overlay.DiagnosticActiveWindow.Title = "DropSpace Liquid Glass diagnostic";
            overlay.DiagnosticActiveWindow.AppWindow.IsShownInSwitchers = true;
            Phase("overlay-initialized");
            await host.DiagnosticPage.OpenSettingsPageAsync("Island");
            var experience = services.GetRequiredService<IslandExperienceCoordinator>();
            if (enableMusic)
            {
                main.PropertyChanged -= sync; sync = null;
                music = services.GetRequiredService<MediaExperienceService>();
                await music.InitializeAsync(settings);
                await main.NavigateAsync("Music");
                Phase("real-music-initialized");
            }
            experience.Open(enableMusic ? IslandPage.Music : IslandPage.Widgets);
            await Task.Delay(1800);
            await Snapshot();
            await File.WriteAllTextAsync(Path.Combine(output, "ready.txt"), "Isolated live material UI ready. control.json accepts snapshot, freeze, expand, compact, hide, settings, measure. finish.txt exits.");
            var end = DateTime.UtcNow.AddMinutes(45);
            while (!File.Exists(Path.Combine(output, "finish.txt")) && DateTime.UtcNow < end)
            {
                var control = Path.Combine(output, "control.json");
                if (File.Exists(control))
                {
                    using var json = JsonDocument.Parse(await File.ReadAllTextAsync(control));
                    var name = json.RootElement.GetProperty("name").GetString(); File.Delete(control);
                    switch (name)
                    {
                        case "freeze": overlay.DiagnosticActiveWindow.FreezeGlassForDiagnosticCapture(); break;
                        case "expand": experience.Open(enableMusic ? IslandPage.Music : IslandPage.Widgets); break;
                        case "compact": experience.Collapse(); break;
                        case "hide": experience.DismissNow(); break;
                        case "settings": host.ShowAndActivate(); await host.DiagnosticPage.OpenSettingsPageAsync("Island"); break;
                        case "widget-settings": host.ShowAndActivate(); await host.DiagnosticPage.OpenSettingsPageAsync("Widgets"); break;
                        case "widgets": experience.Open(IslandPage.Widgets); break;
                        case "widget-music-open": overlay.DiagnosticActiveWindow.InvokeMusicWindowWidgetForDiagnostic(); break;
                        case "music-window-check": await VerifyMusicWindowLifecycle(); break;
                        case "music-page": host.ShowAndActivate(); await main.NavigateAsync("Music"); break;
                        case "snapshot": break;
                        case "measure": await MeasureRefresh(); break;
                        case "compare":
                        case "width-compare":
                            await File.WriteAllTextAsync(Path.Combine(output, $"{name}-pixels-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json"),
                                JsonSerializer.Serialize(overlay.DiagnosticActiveWindow.CompareGlassSampling(name == "width-compare"), new JsonSerializerOptions { WriteIndented = true }));
                            break;
                        default: throw new ArgumentException("Unknown glass diagnostic action.");
                    }
                    await Task.Delay(600); await Snapshot();
                }
                await Task.Delay(150);
            }
            async Task VerifyMusicWindowLifecycle()
            {
                var windowService = services.GetRequiredService<MusicWindowService>();
                var original = main.Settings.IslandAppearance;
                var samples = new List<object>();
                async Task Settle()
                {
                    await Task.Delay(100); // drain native presentation/settings notifications
                    await overlay.DiagnosticActiveWindow.WaitForMotionSettledAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                void Check(string phase, bool condition)
                {
                    samples.Add(new { phase, passed = condition, window = windowService.GetState(), island = experience.Current });
                    if (!condition) throw new InvalidOperationException("Music window lifecycle: " + phase);
                }
                try
                {
                    await main.UpdateSettingsAsync(main.Settings with { IslandAppearance = original with
                    { Resident = true, HideWhileMusicWindowVisible = true } });
                    experience.Open(IslandPage.Widgets); await Settle();
                    overlay.DiagnosticActiveWindow.InvokeMusicWindowWidgetForDiagnostic(); await Settle();
                    var opened = windowService.CurrentWindow;
                    Check("widget-opens-and-hides", opened is not null && windowService.GetState().IsOpen && experience.Current.State == DropSpace.Core.Overlay.OverlayState.Hidden);
                    opened!.SetMinimized(true); await Settle();
                    Check("minimize-restores-resident", windowService.GetState().IsMinimized && experience.Current.State == DropSpace.Core.Overlay.OverlayState.Compact);
                    windowService.Open(); await Settle();
                    Check("restore-reuses-and-hides", ReferenceEquals(opened, windowService.CurrentWindow) && !windowService.GetState().IsMinimized && experience.Current.State == DropSpace.Core.Overlay.OverlayState.Hidden);
                    await main.UpdateSettingsAsync(main.Settings with { IslandAppearance = main.Settings.IslandAppearance with { HideWhileMusicWindowVisible = false } });
                    await Settle();
                    Check("disable-restores-current-rules", experience.Current.State == DropSpace.Core.Overlay.OverlayState.Compact);
                    await main.UpdateSettingsAsync(main.Settings with { IslandAppearance = main.Settings.IslandAppearance with { HideWhileMusicWindowVisible = true } });
                    await Settle();
                    Check("reenable-hides", experience.Current.State == DropSpace.Core.Overlay.OverlayState.Hidden);
                    windowService.Close(); await Settle();
                    Check("close-restores-resident", !windowService.GetState().IsOpen && experience.Current.State == DropSpace.Core.Overlay.OverlayState.Compact);
                }
                finally
                {
                    windowService.Close();
                    await main.UpdateSettingsAsync(main.Settings with { IslandAppearance = original });
                    await File.WriteAllTextAsync(Path.Combine(output, "music-window-lifecycle.json"),
                        JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            async Task MeasureRefresh()
            {
                if (main.Settings.IslandAppearance.MaterialStyle != IslandMaterialStyle.LiquidGlass)
                    throw new InvalidOperationException("Select liquid glass before the refresh measurement.");
                var content = new Grid();
                var pattern = new Window { Title = "DropSpace moving-background measurement (10 seconds)", Content = content };
                var compositor = ElementCompositionPreview.GetElementVisual(content).Compositor;
                var stripes = compositor.CreateContainerVisual();
                for (var i = -1; i < 48; i++)
                {
                    var stripe = compositor.CreateSpriteVisual();
                    stripe.Size = new Vector2(64, 1600); stripe.Offset = new Vector3(i * 64, 0, 0);
                    stripe.Brush = compositor.CreateColorBrush((i & 1) == 0 ? Microsoft.UI.Colors.CornflowerBlue : Microsoft.UI.Colors.White);
                    stripes.Children.InsertAtTop(stripe);
                }
                ElementCompositionPreview.SetElementChildVisual(content, stripes);
                var movement = compositor.CreateVector3KeyFrameAnimation();
                movement.InsertKeyFrame(0, Vector3.Zero, compositor.CreateLinearEasingFunction());
                movement.InsertKeyFrame(1, new Vector3(128, 0, 0), compositor.CreateLinearEasingFunction());
                movement.Duration = TimeSpan.FromSeconds(1); movement.IterationBehavior = AnimationIterationBehavior.Forever;
                try
                {
                    pattern.Activate();
                    ((Microsoft.UI.Windowing.OverlappedPresenter)pattern.AppWindow.Presenter).SetBorderAndTitleBar(false, false);
                    var bounds = overlay.DiagnosticActiveWindow.AppWindow;
                    pattern.AppWindow.MoveAndResize(new RectInt32(bounds.Position.X, bounds.Position.Y, bounds.Size.Width, bounds.Size.Height));
                    stripes.StartAnimation("Offset", movement);
                    await Task.Delay(1000);
                    overlay.DiagnosticActiveWindow.BeginGlassMeasurement();
                    await Task.Delay(10000);
                    var result = overlay.DiagnosticActiveWindow.EndGlassMeasurement();
                    await File.WriteAllTextAsync(Path.Combine(output, $"refresh-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json"),
                        JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                }
                finally
                {
                    overlay.DiagnosticActiveWindow.EndGlassMeasurement();
                    stripes.StopAnimation("Offset"); pattern.Close();
                    host.ShowAndActivate();
                }
            }
            async Task Snapshot() => await File.WriteAllTextAsync(Path.Combine(output, "state.json"),
                JsonSerializer.Serialize(new { material = overlay.DiagnosticActiveWindow.ObserveGlassMaterial(),
                    island = overlay.DiagnosticActiveWindow.ObserveModuleOverlay(), toolbar = overlay.DiagnosticActiveWindow.ObserveIslandToolbar(),
                    appearance = main.Settings.IslandAppearance,
                    musicWindow = services.GetRequiredService<MusicWindowService>().GetState(),
                    translation = new { main.Settings.Language, main.Settings.LyricsTranslationTarget, main.Settings.LyricsTranslationTargetExplicit },
                    music = new { enabled = enableMusic, media.IsPlaying, hasTrack = !string.IsNullOrWhiteSpace(media.Title),
                        media.LyricsStatus, available = services.GetRequiredService<WindowsMediaSessionService>().IsAvailable,
                        sourceCount = services.GetRequiredService<WindowsMediaSessionService>().AvailableSources.Count }
                }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error) { failure = error.ToString(); Phase(failure); }
        finally
        {
            if (main is not null && sync is not null) main.PropertyChanged -= sync;
            try
            {
                services?.GetService<MusicWindowService>()?.Dispose();
                overlay?.Dispose();
                if (music is not null) await music.DisposeAsync();
                host?.PrepareForShutdown();
                if (services is not null) await services.DisposeAsync();
            }
            catch (Exception error) { failure = (failure is null ? "" : failure + "\n") + error; }
            host?.AllowCloseAndClose();
        }
        Application.Current.UnhandledException -= Unhandled;
        await File.WriteAllTextAsync(Path.Combine(output, "exit.json"), JsonSerializer.Serialize(new { status = failure is null ? "closed" : "failed", failure }));
        return failure is null ? 0 : 1;
    }
}
