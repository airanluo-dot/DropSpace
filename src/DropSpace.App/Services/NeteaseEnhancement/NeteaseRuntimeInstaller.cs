using System.Diagnostics;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace DropSpace.App.Services.NeteaseEnhancement;

/// <summary>Only the Microsoft-signed prerequisite; no third-party installer scripting.</summary>
public sealed class NeteaseRuntimeInstaller
{
    private const long MaximumInstallerBytes = 64 * 1024 * 1024;
    public async Task EnsureAsync(Architecture architecture, CancellationToken cancellationToken)
    {
        if (Installed(architecture)) return;
        var suffix = architecture == Architecture.X64 ? "x64" : "x86";
        var directory = Path.Combine(Path.GetTempPath(), "DropSpace-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "VC_redist.exe");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(4));
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
            var uri = new Uri($"https://aka.ms/vs/17/release/VC_redist.{suffix}.exe");
            for (var redirects = 0; ; redirects++)
            {
                if (redirects > 5 || !IsOfficial(uri)) throw new EnhancementDeploymentException("RuntimeIntegrity");
                using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect or HttpStatusCode.SeeOther)
                {
                    uri = new Uri(uri, response.Headers.Location ?? throw new EnhancementDeploymentException("RuntimeIntegrity"));
                    continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaximumInstallerBytes) throw new EnhancementDeploymentException("RuntimeIntegrity");
                await using (var source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
                await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    var buffer = new byte[81920]; long bytes = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                    {
                        bytes += read;
                        if (bytes > MaximumInstallerBytes) throw new EnhancementDeploymentException("RuntimeIntegrity");
                        await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                    }
                }
                break;
            }
            if (AuthenticodeTrustedUpdateVerifier.VerifyEmbeddedSignature(path) != 0)
                throw new EnhancementDeploymentException("RuntimeIntegrity");
#pragma warning disable SYSLIB0057
            using var signer = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            using var certificate = X509CertificateLoader.LoadCertificate(signer.GetRawCertData());
            if (!string.Equals(certificate.GetNameInfo(X509NameType.SimpleName, false), "Microsoft Corporation", StringComparison.Ordinal))
                throw new EnhancementDeploymentException("RuntimeIntegrity");
            var start = new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            start.ArgumentList.Add("/install"); start.ArgumentList.Add("/quiet"); start.ArgumentList.Add("/norestart");
            using var process = Process.Start(start) ?? throw new EnhancementDeploymentException("RuntimeInstallFailed");
            // Once launched, drain the Windows installer independently of UI cancellation.
            using var installation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await process.WaitForExitAsync(installation.Token).ConfigureAwait(false);
            if (process.ExitCode is not (0 or 1638 or 3010) || !Installed(architecture))
                throw new EnhancementDeploymentException("RuntimeInstallFailed");
        }
        finally
        {
            // Generated, fixed children only: never recursively delete an externally supplied path.
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    internal static bool Installed(Architecture architecture)
    {
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\" + (architecture == Architecture.X64 ? "x64" : "x86"));
            if (key?.GetValue("Installed") is int installed && installed == 1 && key.GetValue("Major") is int major && major >= 14)
                return true;
        }
        return false;
    }
    internal static bool IsOfficial(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        uri.Host is "aka.ms" or "download.visualstudio.microsoft.com" or "download.microsoft.com";
}
