using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DropSpace.App.ViewModels;
using DropSpace.App.Views;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Actions;
using DropSpace.Core.Downloads;
using DropSpace.Core.Models;
using DropSpace.Core.Preview;
using DropSpace.Infrastructure.Downloads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace DropSpace.App.Services.Diagnostics;

/// <summary>Explicit local acceptance entry point; never enters the installed user's app lifecycle.</summary>
internal static partial class LocalDeliveryCheck
{
    internal static async Task<int> RunAsync(string output, Func<string, ServiceProvider> createServices)
    {
        output = Path.GetFullPath(output);
        if (Directory.Exists(output) || !Path.IsPathFullyQualified(output)) return 3;
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        ServiceProvider? services = null;
        Window? host = null;
        Exception? failure = null;
        void Check(bool passed, string description)
        {
            if (!passed) throw new InvalidOperationException(description);
            checks.Add(description);
        }
        try
        {
            var data = Path.Combine(output, "profile");
            var watched = Path.Combine(output, "relay");
            Directory.CreateDirectory(watched);
            await File.WriteAllTextAsync(Path.Combine(watched, "existing-not-imported.txt"), "Existing file");
            services = createServices(data);
            var settings = BetaUiDiagnosticFixture.Settings with
            {
                Language = AppLanguagePreference.SimplifiedChinese, QuickPanelHotkey = "Ctrl+Alt+F11",
                CaptureFiles = false, CaptureImages = false, ClipboardPaused = true,
            };
            await services.GetRequiredService<ISettingsService>().SaveAsync(settings);
            services.GetRequiredService<AppLanguageService>().Apply(settings.Language);
            XamlResourceOverride.Initialize(services.GetRequiredService<IAppStringLocalizer>());
            var main = services.GetRequiredService<MainViewModel>();
            BetaUiDiagnosticFixture.ApplySettings(main, settings);
            services.GetRequiredService<MediaViewModel>().Settings = settings;
            var repository = services.GetRequiredService<IItemRepository>();
            var recent = services.GetRequiredService<IRecentItemsService>();
            await recent.StartAsync();
            Check(recent.Directories.Count == 0, "Relay starts disabled without any selected directory");
            await main.NavigateAsync("Space");
            host = new Window { Title = "DropSpace 1.1 Beta1 — local verification" };
            host.AppWindow.Resize(new SizeInt32(1120, 800));
            var page = ActivatorUtilities.CreateInstance<MainPage>(services, WinRT.Interop.WindowNative.GetWindowHandle(host));
            host.Content = page; page.SetPresentationActive(true); host.Activate();
            var overlays = services.GetRequiredService<OverlayWindowService>();
            await overlays.InitializeAsync(host.Activate);
            await recent.AddDirectoryAsync(watched);
            var foreground = GetForegroundWindow();
            var experience = services.GetRequiredService<DropSpace.Core.Island.IslandExperienceCoordinator>();
            var pageBefore = experience.Current.Page;
            var imagePath = Path.Combine(watched, "接力图像.png");
            await CreateImageAsync(imagePath);
            var image = await WaitForItemAsync(repository, imagePath);
            Check(GetForegroundWindow() == foreground && experience.Current.Page == pageBefore,
                "Passive file arrival preserves foreground and island page");
            Check(!(await repository.QueryAsync(new ItemQuery())).Any(x => x.Title == "existing-not-imported.txt"),
                "Selected folder existing files were not imported");
            var textPath = Path.Combine(watched, "长文本参照.txt");
            await File.WriteAllTextAsync(textPath, string.Join('\n', Enumerable.Range(1, 160).Select(i => $"第 {i} 行：资料仍在原列表，参照仅用于持续查看。This is a long reference document.")));
            var textItem = await WaitForItemAsync(repository, textPath);
            var actions = services.GetRequiredService<IItemActionRegistry>();
            var selection = new ItemSelectionSnapshot([DropItemSnapshot.FromItem(image)]);
            foreach (var action in new[] { ItemActionId.CompressZip, ItemActionId.ConvertImage, ItemActionId.ResizeImage })
            {
                var result = await actions.ExecuteAsync(action, new ItemActionContext(selection, watched,
                    OutputFormat: action == ItemActionId.ConvertImage ? "jpg" : "png", Width: 320, Height: 200));
                Check(result.Succeeded && result.OutputPaths.Count == 1 && result.OutputItemIds.Count == 1 && File.Exists(result.OutputPaths[0]),
                    $"Native {action} produces and registers a final file");
                await Task.Delay(2200);
                var all = await repository.QueryAsync(new ItemQuery());
                Check(all.Count(x => string.Equals(x.File?.OriginalPath, result.OutputPaths[0], StringComparison.OrdinalIgnoreCase)) == 1,
                    $"Native {action} output and watcher merge into one item");
            }
            var beforeFailure = (await repository.QueryAsync(new ItemQuery())).Count;
            var failed = await actions.ExecuteAsync(ItemActionId.ResizeImage, new ItemActionContext(selection, watched, Width: -1, Height: -1));
            Check(!failed.Succeeded && failed.OutputItemIds.Count == 0 && (await repository.QueryAsync(new ItemQuery())).Count == beforeFailure,
                "Failed image operation has no successful output record");
            var downloader = services.GetRequiredService<DownloadManager>();
            using (var server = new TcpListener(IPAddress.Loopback, 0))
            {
                server.Start();
                var port = ((IPEndPoint)server.LocalEndpoint).Port;
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(25));
                var serve = ServeAsync(server, stop.Token);
                await downloader.EnqueueAsync($"http://127.0.0.1:{port}/material.txt", watched, "download-result.txt");
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (downloader.Tasks.All(x => x.State != DownloadTaskState.Completed) && DateTime.UtcNow < deadline) await Task.Delay(100);
                Check(downloader.Tasks.Any(x => x.State == DownloadTaskState.Completed), "Ordinary HTTP download commits successfully");
                var downloaded = await WaitForItemAsync(repository, Path.Combine(watched, "download-result.txt"));
                await Task.Delay(2200);
                Check(RecentItemMetadata.Read((await repository.GetAsync(downloaded.Id))?.MetadataJson)?.Operation == "Download" &&
                    (await repository.QueryAsync(new ItemQuery())).Count(x => x.File?.OriginalPath == downloaded.File?.OriginalPath) == 1,
                    "Download completion and selected-folder discovery share one output item");
                stop.Cancel(); server.Stop();
                try { await serve; } catch (Exception e) when (e is OperationCanceledException or SocketException) { }
            }
            var reference = services.GetRequiredService<ReferenceWindowService>();
            await reference.OpenAsync(image);
            var window = reference.DiagnosticWindow ?? throw new InvalidOperationException("Reference window was not constructed.");
            Check(window.HasContent && window.IsAlwaysOnTop, "Image reference renders and starts always on top");
            await reference.OpenAsync(textItem);
            Check(ReferenceEquals(window, reference.DiagnosticWindow) && window.CurrentText?.Contains("第 160 行", StringComparison.Ordinal) == true,
                "Long text replaces image in the same reference window");
            host.Activate(); await Task.Delay(200);
            var workWindow = GetForegroundWindow();
            await File.AppendAllTextAsync(textPath, "\n覆盖后更新内容。");
            await Task.Delay(3500);
            Check(GetForegroundWindow() == workWindow && window.CurrentText?.Contains("覆盖后更新内容", StringComparison.Ordinal) == true,
                "Reference refresh after overwrite updates content without stealing focus");
            window.Close();
            Check(await repository.GetAsync(textItem.Id) is not null && File.Exists(textPath), "Closing reference preserves original object and external file");
            await recent.RetainAsync(image.Id);
            Check((await repository.GetAsync(image.Id))?.Source == ItemSource.Space, "Keep retains the same item identity in Space");
            Check(main.CurrentVersionText == "1.1 Beta1", "UI version is exactly 1.1 Beta1");
            await VerifyRetrievalAsync(services, host, checks);
            await File.WriteAllTextAsync(Path.Combine(output, "checks.json"), JsonSerializer.Serialize(new { status = "passed", checks }, new JsonSerializerOptions { WriteIndented = true }));
            await File.WriteAllTextAsync(Path.Combine(output, "ready.txt"), "Manual UI check ready; Ctrl+Alt+F11 opens the actual island. Create finish.txt here to close this isolated session.");
            // Leave actual controls available for computer-use verification, with a bounded lifetime.
            var holdUntil = DateTime.UtcNow.AddMinutes(40);
            while (!File.Exists(Path.Combine(output, "finish.txt")) && DateTime.UtcNow < holdUntil)
            {
                var controlPath = Path.Combine(output, "control.txt");
                if (File.Exists(controlPath))
                {
                    var command = (await File.ReadAllTextAsync(controlPath)).Trim();
                    File.Delete(controlPath);
                    await ApplyRetrievalCommandAsync(services, host, command);
                }
                await Task.Delay(500);
            }
        }
        catch (Exception error)
        {
            failure = error;
            await File.WriteAllTextAsync(Path.Combine(output, "checks.json"), JsonSerializer.Serialize(new { status = "failed", checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            if (services is not null)
            {
                services.GetService<ReferenceWindowService>()?.Dispose();
                services.GetService<OverlayWindowService>()?.Dispose();
                await services.GetRequiredService<DownloadManager>().ShutdownAsync();
                await services.DisposeAsync();
            }
            host?.Close();
        }
        return failure is null ? 0 : 1;
    }

    private static async Task<DropItem> WaitForItemAsync(IItemRepository repository, string path)
    {
        for (var i = 0; i < 120; i++)
        {
            var found = (await repository.QueryAsync(new ItemQuery())).FirstOrDefault(x => x.File?.OriginalPath == path && x.Status == ItemStatus.Available);
            if (found is not null) return found;
            await Task.Delay(100);
        }
        throw new TimeoutException("File did not become available: " + path);
    }

    private static async Task CreateImageAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(await PrepareAsync(path));
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        var pixels = new byte[640 * 400 * 4];
        for (var y = 0; y < 400; y++) for (var x = 0; x < 640; x++)
        {
            var index = (y * 640 + x) * 4;
            pixels[index] = (byte)(80 + x / 4); pixels[index + 1] = (byte)(70 + y / 3); pixels[index + 2] = 65; pixels[index + 3] = 255;
        }
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, 640, 400, 96, 96, pixels);
        await encoder.FlushAsync();
        static async Task<string> PrepareAsync(string value) { await File.WriteAllBytesAsync(value, []); return value; }
    }

    private static async Task ServeAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            using var client = await listener.AcceptTcpClientAsync(token);
            await using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var headerBytes = 0;
            while (await reader.ReadLineAsync(token) is { Length: > 0 } line)
                if ((headerBytes += line.Length) > 16_384) throw new InvalidDataException("Fixture request headers exceed the bound.");
            var body = Encoding.UTF8.GetBytes("DropSpace ordinary file download acceptance fixture.\n");
            var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nContent-Type: text/plain\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers, token); await stream.WriteAsync(body, token);
        }
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
