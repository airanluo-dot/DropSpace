using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace DropSpace.App.Services.NeteaseEnhancement;

public sealed partial class InfLinkDeploymentService
{
    public async Task StopAsync(NeteaseInstallation installation, CancellationToken cancellationToken = default)
    {
        await DrainProcessGenerationsAsync(async token =>
        {
            var processes = MatchingProcesses(installation);
            if (processes.Count == 0) return false;
            await StopSnapshotAsync(installation, processes, token).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
        AssertStopped(installation);
    }

    // Chromium can spawn another renderer while the first snapshot is closing.
    // Re-enumerate after each real exit wait, within one bounded operation budget.
    internal static async Task DrainProcessGenerationsAsync(Func<CancellationToken, Task<bool>> stopSnapshot, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            for (int generation = 0; generation < 8; generation++)
            {
                budget.Token.ThrowIfCancellationRequested();
                if (!await stopSnapshot(budget.Token).ConfigureAwait(false)) return;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new EnhancementDeploymentException("PlayerCloseTimedOut");
        }
        throw new EnhancementDeploymentException("PlayerCloseTimedOut");
    }

    private static async Task StopSnapshotAsync(NeteaseInstallation installation, List<Process> processes, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (!HasExitedFresh(process) && process.MainWindowHandle != 0) _ = process.CloseMainWindow();
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    if (!HasExitedFresh(process)) throw new EnhancementDeploymentException("PlayerProcessUnavailable");
                }
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try { await Task.WhenAll(processes.Select(p => p.WaitForExitAsync(timeout.Token))).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                foreach (var process in processes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (!IsMatchingLiveProcess(() => HasExitedFresh(process), () => process.MainModule?.FileName, installation.ExecutablePath)) continue;
                        process.Kill(entireProcessTree: false);
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                    {
                        if (!HasExitedFresh(process)) throw new EnhancementDeploymentException("PlayerProcessUnavailable");
                    }
                }
                using var exitTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                exitTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                try { await Task.WhenAll(processes.Select(p => p.WaitForExitAsync(exitTimeout.Token))).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new EnhancementDeploymentException("PlayerCloseTimedOut"); }
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public Task RestartAsync(NeteaseInstallation installation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeploymentPaths.AssertSafe(installation.ExecutablePath);
        var running = MatchingProcesses(installation);
        try { if (running.Any(p => !HasExitedFresh(p))) return Task.CompletedTask; }
        finally { foreach (var item in running) item.Dispose(); }
        var start = new ProcessStartInfo(installation.ExecutablePath) { UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(installation.ExecutablePath)!, WindowStyle = ProcessWindowStyle.Hidden };
        start.Environment["BETTERNCM_PROFILE"] = BetterNcmProbe.ResolveProfilePath();
        using var process = Process.Start(start);
        if (process is null) throw new EnhancementDeploymentException("PlayerStartFailed");
        return Task.CompletedTask;
    }

    private static List<Process> MatchingProcesses(NeteaseInstallation installation)
    {
        var result = new List<Process>();
        try
        {
            foreach (var process in Process.GetProcessesByName("cloudmusic"))
            {
                try
                {
                    if (IsMatchingLiveProcess(() => HasExitedFresh(process), () => process.MainModule?.FileName, installation.ExecutablePath)) { result.Add(process); continue; }
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { process.Dispose(); throw new EnhancementDeploymentException("PlayerProcessUnavailable"); }
                process.Dispose();
            }
            return result;
        }
        catch { foreach (var process in result) process.Dispose(); throw; }
    }
    private static void AssertStopped(NeteaseInstallation installation)
    {
        var processes = MatchingProcesses(installation);
        try { if (processes.Any(p => !HasExitedFresh(p))) throw new EnhancementDeploymentException("PlayerRunning"); }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static bool HasExitedFresh(Process process)
    {
        process.Refresh();
        return process.HasExited;
    }

    internal static bool IsMatchingLiveProcess(Func<bool> hasExited, Func<string?> executablePath, string expectedPath)
    {
        if (hasExited()) return false;
        try
        {
            string? path = executablePath();
            if (hasExited()) return false;
            if (path is null) throw new EnhancementDeploymentException("PlayerProcessUnavailable");
            return string.Equals(path, expectedPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            if (hasExited()) return false;
            throw new EnhancementDeploymentException("PlayerProcessUnavailable");
        }
    }
    private string ReceiptPath(NeteaseInstallation installation) => Path.Combine(stateRoot, Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(installation.ExecutablePath).ToUpperInvariant()))) + ".json");
    private void ValidateReceipt(InfLinkDeploymentReceipt receipt, NeteaseInstallation installation)
    {
        if (!Guid.TryParseExact(receipt.TransactionId, "N", out _) || !string.Equals(receipt.Installation.ExecutablePath, installation.ExecutablePath, StringComparison.OrdinalIgnoreCase) || receipt.Installation.Architecture != installation.Architecture || receipt.Files.Count != 2) throw new EnhancementDeploymentException("InvalidReceipt");
        string[] allowed = [Path.Combine(Path.GetDirectoryName(installation.ExecutablePath)!, "msimg32.dll"), Path.Combine(receipt.ProfilePath, "plugins", "InfLink-rs.plugin")];
        for (int i = 0; i < 2; i++)
        {
            var file = receipt.Files[i];
            if (!string.Equals(file.TargetPath, allowed[i], StringComparison.OrdinalIgnoreCase) || (file.BackupName is not null && file.BackupName != i + ".bak")) throw new EnhancementDeploymentException("InvalidReceipt");
            DeploymentPaths.AssertSafe(file.TargetPath);
        }
    }
    private async Task AssertCurrentAsync(InfLinkDeploymentReceipt receipt, CancellationToken ct)
    {
        ValidateReceipt(receipt, receipt.Installation);
        var current = await GetManagedReceiptAsync(receipt.Installation, ct).ConfigureAwait(false);
        if (current is null || JsonSerializer.Serialize(current with { Committed = false }) != JsonSerializer.Serialize(receipt with { Committed = false })) throw new EnhancementDeploymentException("StaleReceipt");
    }
    private async Task SaveReceiptAsync(InfLinkDeploymentReceipt receipt, CancellationToken ct)
    {
        string target = ReceiptPath(receipt.Installation);
        DeploymentPaths.AssertSafe(target);
        Directory.CreateDirectory(stateRoot);
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(receipt), ct).ConfigureAwait(false); File.Move(temporary, target, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private async Task RestoreFilesAsync(InfLinkDeploymentReceipt receipt, CancellationToken ct)
    {
        foreach (var file in receipt.Files.Where(f => f.Owned))
        {
            DeploymentPaths.AssertSafe(file.TargetPath);
            string? current = File.Exists(file.TargetPath) ? await HashAsync(file.TargetPath, ct).ConfigureAwait(false) : null;
            if (current is not null && !HashEquals(current, file.InstalledHash) && !HashEquals(current, file.PreviousHash)) throw new EnhancementDeploymentException("ManagedFileChanged");
            if (file.BackupName is { } name)
            {
                string backup = Path.Combine(stateRoot, "backups", receipt.TransactionId, name);
                DeploymentPaths.AssertSafe(backup);
                if (!HashEquals(await HashAsync(backup, ct).ConfigureAwait(false), file.PreviousHash)) throw new EnhancementDeploymentException("BackupChanged");
            }
        }
        foreach (var file in receipt.Files.Where(f => f.Owned))
        {
            if (file.BackupName is { } name) await ReplaceAsync(Path.Combine(stateRoot, "backups", receipt.TransactionId, name), file.TargetPath, ct).ConfigureAwait(false);
            else await RetryFileMutationAsync(() => File.Delete(file.TargetPath), ct).ConfigureAwait(false);
        }
    }
    private static async Task ReplaceAsync(string source, string destination, CancellationToken ct)
    {
        DeploymentPaths.AssertSafe(source); DeploymentPaths.AssertSafe(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + ".dropspace-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var input = File.OpenRead(source))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            { await input.CopyToAsync(output, ct).ConfigureAwait(false); await output.FlushAsync(ct).ConfigureAwait(false); }
            DeploymentPaths.AssertSafe(destination);
            await RetryFileMutationAsync(() => File.Move(temporary, destination, true), ct).ConfigureAwait(false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    // Windows can retain an image mapping briefly after its process exit signal.
    // Retry only failed file operations (never add a delay to successful shutdown),
    // and preserve the original access/sharing failure if contention persists.
    internal static async Task RetryFileMutationAsync(Action mutation, CancellationToken token,
        Func<TimeSpan, CancellationToken, Task>? wait = null)
    {
        wait ??= (delay, cancellation) => Task.Delay(delay, cancellation);
        for (int attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { mutation(); return; }
            catch (Exception exception) when (attempt < 6 &&
                exception is IOException or UnauthorizedAccessException &&
                (exception.HResult & 0xffff) is 5 or 32 or 33)
            {
                await wait(TimeSpan.FromMilliseconds(50 * (1 << attempt)), token).ConfigureAwait(false);
            }
        }
    }
    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        DeploymentPaths.AssertSafe(path);
        await using var stream = File.OpenRead(path);
        if (stream.Length > MaximumAssetBytes) throw new EnhancementDeploymentException("AssetTooLarge");
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
    }
    private static bool HashEquals(string? left, string? right) => left is not null && right is not null && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private async Task DownloadVerifiedAsync(Uri url, string destination, string expected, CancellationToken ct)
    {
        byte[] bytes = await DownloadAsync(url, MaximumAssetBytes, ct).ConfigureAwait(false);
        if (!HashEquals(Convert.ToHexString(SHA256.HashData(bytes)), expected)) throw new EnhancementDeploymentException("HashMismatch");
        await File.WriteAllBytesAsync(destination, bytes, ct).ConfigureAwait(false);
    }
    private async Task<byte[]> DownloadAsync(Uri url, int limit, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        ct = timeout.Token;
        for (int redirect = 0; redirect < 5; redirect++)
        {
            if (url.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(url.UserInfo) || !url.IsDefaultPort || url.Host is not ("api.github.com" or "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com")) throw new EnhancementDeploymentException("UntrustedDownload");
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
            { url = new Uri(url, response.Headers.Location ?? throw new EnhancementDeploymentException("InvalidRedirect")); continue; }
            if (response.StatusCode != HttpStatusCode.OK) throw new EnhancementDeploymentException("DownloadFailed");
            if (response.Content.Headers.ContentLength > limit) throw new EnhancementDeploymentException("AssetTooLarge");
            await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var output = new MemoryStream();
            byte[] buffer = new byte[81920];
            int count;
            while ((count = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
            { if (output.Length + count > limit) throw new EnhancementDeploymentException("AssetTooLarge"); await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false); }
            return output.ToArray();
        }
        throw new EnhancementDeploymentException("TooManyRedirects");
    }
}
