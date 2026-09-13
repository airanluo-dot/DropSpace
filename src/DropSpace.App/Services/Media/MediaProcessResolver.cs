using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DropSpace.App.Services.Media;

/// <summary>Resolves an SMTC identity on source changes; never samples processes on each audio frame.</summary>
public sealed class MediaProcessResolver
{
    public async Task<uint?> ResolveAudioAsync(string sourceAppUserModelId, CancellationToken cancellationToken)
    {
        var audioIdentity = DropSpace.Core.Media.MediaProcessIdentityPolicy.AudioIdentity(sourceAppUserModelId);
        var renderer = await ResolveAsync(audioIdentity, cancellationToken).ConfigureAwait(false);
        return renderer ?? (audioIdentity == sourceAppUserModelId ? null : await ResolveAsync(sourceAppUserModelId, cancellationToken).ConfigureAwait(false));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetApplicationUserModelId(SafeProcessHandle process, ref uint length, StringBuilder applicationId);

    public Task<uint?> ResolveAsync(string sourceAppUserModelId, CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (string.IsNullOrWhiteSpace(sourceAppUserModelId)) return (uint?)null;
        var matches = new List<(uint Id, bool HasWindow)>();
        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes.Take(512))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var processId = checked((uint)process.Id);
                    var exeMatch = string.Equals(process.ProcessName + ".exe", sourceAppUserModelId, StringComparison.OrdinalIgnoreCase);
                    var identityMatch = false;
                    if (!exeMatch)
                    {
                        using var handle = OpenProcess(0x1000, false, processId);
                        if (handle.IsInvalid) continue;
                        uint length = 512;
                        var identity = new StringBuilder((int)length);
                        identityMatch = GetApplicationUserModelId(handle, ref length, identity) == 0 &&
                            string.Equals(identity.ToString(), sourceAppUserModelId, StringComparison.OrdinalIgnoreCase);
                    }
                    if (exeMatch || identityMatch) matches.Add((processId, process.MainWindowHandle != 0));
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        finally { foreach (var process in processes) process.Dispose(); }
        var visible = matches.Where(match => match.HasWindow).ToArray();
        // Ambiguous identities fail closed instead of capturing an arbitrary process.
        return visible.Length == 1 ? visible[0].Id : matches.Count == 1 ? matches[0].Id : (uint?)null;
    }, cancellationToken);
}
