using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DropSpace.App.Services.Agent;
using DropSpace.App.ViewModels;
using DropSpace.App.Views;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Agent;
using DropSpace.Core.Models;
using DropSpace.Infrastructure.Downloads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace DropSpace.App.Services.Diagnostics;

/// <summary>Opt-in isolated native acceptance; never uses the installed profile or its authorization.</summary>
internal static class AgentIntegrationCheck
{
    internal static async Task<int> RunAsync(string output, Func<string, ServiceProvider> createServices)
    {
        output = Path.GetFullPath(output);
        if (Directory.Exists(output)) return 3;
        Directory.CreateDirectory(output);
        ServiceProvider? services = null; Window? window = null;
        var checks = new List<string>(); var result = 1;
        void Check(bool condition, string description)
        { if (!condition) throw new InvalidOperationException(description); checks.Add(description); }
        try
        {
            services = createServices(Path.Combine(output, "profile"));
            var settings = BetaUiDiagnosticFixture.Settings with
            { Language = AppLanguagePreference.SimplifiedChinese, QuickPanelHotkey = "Ctrl+Alt+F11", CaptureFiles = false, CaptureImages = false, ClipboardPaused = true };
            await services.GetRequiredService<ISettingsService>().SaveAsync(settings);
            services.GetRequiredService<AppLanguageService>().Apply(settings.Language);
            XamlResourceOverride.Initialize(services.GetRequiredService<IAppStringLocalizer>());
            var main = services.GetRequiredService<MainViewModel>(); BetaUiDiagnosticFixture.ApplySettings(main, settings);
            services.GetRequiredService<MediaViewModel>().Settings = settings;
            await services.GetRequiredService<IRecentItemsService>().StartAsync();
            await main.NavigateAsync("Space");
            window = new Window { Title = "DropSpace 1.1 Beta1 — Agent local verification" };
            window.AppWindow.Resize(new SizeInt32(1120, 800));
            var page = ActivatorUtilities.CreateInstance<MainPage>(services, WinRT.Interop.WindowNative.GetWindowHandle(window));
            window.Content = page; page.SetPresentationActive(true); window.Activate();
            var desktop = services.GetRequiredService<AppAgentDesktopControl>();
            desktop.Attach(window.Activate, window.AppWindow.Hide,
                async key => { window.Activate(); await page.OpenSettingsPageAsync(key); },
                () => File.WriteAllText(Path.Combine(output, "finish.txt"), "exit requested"),
                async (operation, details, token) => { window.Activate(); return await page.ConfirmAgentOperationAsync(operation, details, token); });
            await services.GetRequiredService<OverlayWindowService>().InitializeAsync(window.Activate);
            var host = services.GetRequiredService<IAppAgentHost>();
            await host.StartAsync();
            Check(host.Endpoint is null && host.Bindings.Count == 0, "Fresh profile has no listener and no agent bindings");
            var bundle = services.GetRequiredService<AgentSkillPackageService>();
            var invitation = await host.CreateBindingAsync("本地验证助手");
            var prompt = await bundle.CreateInstallPromptAsync(invitation, host.DiscoveryFilePath);
            Check(prompt.StartsWith("Read ", StringComparison.Ordinal) && prompt.Contains(invitation.PairingKey, StringComparison.Ordinal) && prompt.Contains("SKILL.md", StringComparison.Ordinal), "Setup prompt remains English in the Chinese App and includes the one-time invitation and bundled skill");
            var files = Directory.GetFiles(bundle.SkillDirectory, "*", SearchOption.AllDirectories);
            Check(files.Length == 6 && files.All(file => !File.ReadAllText(file).Contains(invitation.PairingKey, StringComparison.Ordinal)), "All six bundled skill files export without a pairing secret");
            var helper = Path.Combine(bundle.SkillDirectory, "scripts", "Invoke-DropSpace.ps1");
            var pairing = await ClientAsync("Pair", JsonSerializer.Serialize(new { pairingKey = invitation.PairingKey }));
            Check(pairing.ExitCode == 0 && pairing.Json.GetProperty("paired").GetBoolean(), "Bundled PowerShell 5.1 client pairs with the real native App host");
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(15) };
            using (var repeated = await http.PostAsJsonAsync(new Uri(host.Endpoint!, "v1/pair"), new { bindingId = invitation.BindingId, pairingKey = invitation.PairingKey }))
                Check(repeated.StatusCode == HttpStatusCode.Unauthorized, "The consumed pairing key cannot bind a second time");
            var connection = await ClientAsync("Status");
            Check(connection.ExitCode == 0 && connection.Json.GetProperty("connected").GetBoolean(), "Saved DPAPI credential reconnects without asking for the pairing key");
            var catalogue = await ClientAsync("Catalog");
            var commands = catalogue.Json.GetProperty("commands");
            Check(commands.GetArrayLength() >= 100 && commands.EnumerateArray().Select(c => c.GetProperty("name").GetString()).Distinct().Count() == commands.GetArrayLength(), "Full App command catalog is unique and discoverable through the shipped client");
            await File.WriteAllTextAsync(Path.Combine(output, "catalog.json"), commands.GetRawText());
            var item = await ExecuteAsync("space.add_text", new { text = "Agent 中文集成验收🙂\n" + string.Join('\n', Enumerable.Range(1, 80).Select(i => $"第 {i} 行：同一资料对象。")) });
            var id = item.GetProperty("id").GetGuid();
            Check(id != Guid.Empty, "Agent writes a real shared Space object using the existing owner");
            var found = await ExecuteAsync("items.query", new { search = "中文集成验收", source = "Space" });
            Check(found.GetArrayLength() == 1 && found[0].GetProperty("id").GetGuid() == id, "Unified stored-text search finds the same object through HTTP");
            var bytes = await ExecuteAsync("items.read_bytes", new { id, offset = 0, count = 512 });
            Check(Encoding.UTF8.GetString(Convert.FromBase64String(bytes.GetProperty("dataBase64").GetString()!)).Contains("Agent 中文集成验收🙂", StringComparison.Ordinal), "Chunked content read preserves Chinese and emoji through the client");
            await ExecuteAsync("items.pin", new { id, pinned = true });
            Check((await ExecuteAsync("items.get", new { id })).GetProperty("isPinned").GetBoolean(), "Agent pin updates the shared persisted object");
            var changed = await ExecuteAsync("settings.patch", new { patch = new { retentionDays = 31 } });
            Check(changed.GetProperty("settings").GetProperty("retentionDays").GetInt32() == 31 && main.Settings.RetentionDays == 31, "Settings patch reaches the real settings coordinator and view model");
            var opened = await ExecuteAsync("reference.open", new { id });
            Check(opened.GetProperty("isOpen").GetBoolean() && services.GetRequiredService<ReferenceWindowService>().DiagnosticWindow?.CurrentText?.Contains("第 80 行", StringComparison.Ordinal) == true, "Agent explicitly opens long-text reference for the original object");
            await ExecuteAsync("reference.close", new { });
            Check(!(await ExecuteAsync("reference.state", new { })).GetProperty("isOpen").GetBoolean() &&
                (await ExecuteAsync("items.get", new { id })).GetProperty("id").GetGuid() == id, "Closing reference leaves its source object available");
            foreach (var command in new[] { "app.state", "island.state", "clipboard.status", "recent.directories", "widgets.state", "activities.state", "devices.status", "transfers.history", "transfers.list", "shares.list" })
                await ExecuteAsync(command, new { });
            Check(true, "Real native state commands across desktop, content, widgets, device, transfer and share domains serialize successfully");
            var invalid = await ClientAsync("Execute", JsonSerializer.Serialize(new { command = "settings.patch", arguments = new { patch = new { agentCredentials = "invalid" } } }));
            Check(invalid.ExitCode != 0, "Agent cannot alter authorization through settings.patch");
            var unknown = await ClientAsync("Execute", JsonSerializer.Serialize(new { command = "agent.create_binding", arguments = new { } }));
            Check(unknown.ExitCode != 0, "Authorization creation is not an Agent command");
            var disabledDiscovery = await ClientAsync("Execute", JsonSerializer.Serialize(new { command = "devices.discover", arguments = new { seconds = 1 } }));
            Check(disabledDiscovery.ExitCode != 0, "Disabled handoff cannot be bypassed by Agent device discovery");
            var missingOffer = await ClientAsync("Execute", JsonSerializer.Serialize(new { command = "transfers.respond", arguments = new { sessionId = Guid.NewGuid(), accept = true } }));
            Check(missingOffer.ExitCode != 0, "Unknown incoming transfer is rejected before showing a confirmation");
            window.Activate();
            await main.CopyAgentInvitationAsync("DropSpace sensitive-copy verification fixture (no key)");
            var clipboard = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            Check(clipboard.Contains("DropSpace.Sensitive") && (await clipboard.GetTextAsync()).Contains("no key", StringComparison.Ordinal),
                "Sensitive-copy path writes readable native text with the persistent capture-exclusion marker");
            await host.StopAsync(); await host.StartAsync();
            var reconnect = await ClientAsync("Status");
            Check(reconnect.ExitCode == 0 && reconnect.Json.GetProperty("connected").GetBoolean(), "Same saved client credential survives host restart and endpoint rediscovery");
            // Keep one independent grant alive so a revoked request reaches the host and is rejected.
            var other = await host.CreateBindingAsync("独立撤销验证");
            using var pairResponse = await http.PostAsJsonAsync(new Uri(host.Endpoint!, "v1/pair"), new { bindingId = other.BindingId, pairingKey = other.PairingKey });
            pairResponse.EnsureSuccessStatusCode();
            var token = (await pairResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("bearerToken").GetString()!;
            await host.RevokeAsync(invitation.BindingId);
            Check((await ClientAsync("Status")).ExitCode != 0, "Revoked saved client credential loses access immediately");
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(host.Endpoint!, "v1/catalog"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var stillActive = await http.SendAsync(request);
            Check(stillActive.IsSuccessStatusCode && host.Bindings.Count == 1, "Revocation preserves an independently paired agent");
            Check(main.CurrentVersionText == "1.1 Beta1", "Integrated build displays version 1.1 Beta1");
            await page.OpenSettingsPageAsync("Agents");
            await File.WriteAllTextAsync(Path.Combine(output, "checks.json"), JsonSerializer.Serialize(new { status = "passed", commandCount = commands.GetArrayLength(), checks }, new JsonSerializerOptions { WriteIndented = true }));
            await File.WriteAllTextAsync(Path.Combine(output, "ready.txt"), "Isolated native Agent UI ready; create finish.txt to close. No keys or tokens are included in receipts.");
            var until = DateTime.UtcNow.AddMinutes(25);
            while (!File.Exists(Path.Combine(output, "finish.txt")) && DateTime.UtcNow < until) await Task.Delay(500);
            await host.RevokeAsync(other.BindingId);
            result = 0;

            async Task<JsonElement> ExecuteAsync(string command, object arguments)
            {
                var call = await ClientAsync("Execute", JsonSerializer.Serialize(new { command, arguments }));
                if (call.ExitCode != 0) throw new InvalidOperationException($"Native client command failed: {command}; {call.Json.GetRawText()}");
                return call.Json.GetProperty("result").Clone();
            }
            async Task<(int ExitCode, JsonElement Json)> ClientAsync(string action, string? input = null)
            {
                var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
                foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", helper, "-Action", action, "-BindingId", invitation.BindingId.ToString("D"), "-EndpointFile", host.DiscoveryFilePath }) start.ArgumentList.Add(arg);
                using var process = Process.Start(start) ?? throw new IOException("Cannot start bundled client.");
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                if (input is not null) await process.StandardInput.WriteAsync(input);
                process.StandardInput.Close();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
                await process.WaitForExitAsync(deadline.Token);
                var response = await stdout; await stderr;
                using var json = JsonDocument.Parse(response);
                return (process.ExitCode, json.RootElement.Clone());
            }
        }
        catch (Exception error)
        {
            await File.WriteAllTextAsync(Path.Combine(output, "checks.json"), JsonSerializer.Serialize(new { status = "failed", checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            if (services is not null)
            {
                services.GetService<AppAgentDesktopControl>()?.Detach();
                if (services.GetService<IAppAgentHost>() is { } host) await host.StopAsync();
                services.GetService<ReferenceWindowService>()?.Dispose();
                services.GetService<OverlayWindowService>()?.Dispose();
                await services.GetRequiredService<DownloadManager>().ShutdownAsync();
                await services.DisposeAsync();
            }
            window?.Close();
        }
        return result;
    }
}
