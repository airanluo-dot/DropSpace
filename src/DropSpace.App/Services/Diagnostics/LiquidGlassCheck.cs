using System.ComponentModel;
using System.Text.Json;
using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Island;
using DropSpace.Core.Models;
using DropSpace.Core.Overlay;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace DropSpace.App.Services.Diagnostics;

/// <summary>Opt-in isolated real-window material inspection; no clipboard/media capture or user profile.</summary>
internal static class LiquidGlassCheck
{
    internal static async Task<int> RunAsync(string output, Func<string, ServiceProvider> createServices)
    {
        output = Path.GetFullPath(output);
        if (Directory.Exists(output) || File.Exists(output)) return 3;
        Directory.CreateDirectory(output);
        ServiceProvider? services = null; MainWindow? host = null; OverlayWindowService? overlay = null;
        MainViewModel? main = null; PropertyChangedEventHandler? sync = null;
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
                Theme = ThemePreference.Dark, IslandAppearance = new() { Theme = ThemePreference.Dark, Resident = true, ShowLogoWhenIdle = true },
                QuickPanelHotkey = "Ctrl+Alt+F11", FileDragWakeMode = FileDragWakeMode.Disabled,
                CaptureFiles = false, CaptureImages = false, ClipboardPaused = true,
            };
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
            experience.Open(IslandPage.Widgets);
            await Task.Delay(1800);
            await Snapshot();
            await File.WriteAllTextAsync(Path.Combine(output, "ready.txt"), "Isolated live material UI ready. control.json accepts snapshot, freeze, expand, compact, hide, settings. finish.txt exits.");
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
                        case "expand": experience.Open(IslandPage.Widgets); break;
                        case "compact": experience.Collapse(); break;
                        case "hide": experience.DismissNow(); break;
                        case "settings": host.ShowAndActivate(); await host.DiagnosticPage.OpenSettingsPageAsync("Island"); break;
                        case "snapshot": break;
                        default: throw new ArgumentException("Unknown glass diagnostic action.");
                    }
                    await Task.Delay(600); await Snapshot();
                }
                await Task.Delay(150);
            }
            async Task Snapshot() => await File.WriteAllTextAsync(Path.Combine(output, "state.json"),
                JsonSerializer.Serialize(new { material = overlay.DiagnosticActiveWindow.ObserveGlassMaterial(),
                    island = overlay.DiagnosticActiveWindow.ObserveModuleOverlay(), toolbar = overlay.DiagnosticActiveWindow.ObserveIslandToolbar(),
                    appearance = main.Settings.IslandAppearance }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error) { failure = error.ToString(); Phase(failure); }
        finally
        {
            if (main is not null && sync is not null) main.PropertyChanged -= sync;
            try
            {
                services?.GetService<Services.Media.MusicWindowService>()?.Dispose();
                overlay?.Dispose(); host?.PrepareForShutdown();
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
