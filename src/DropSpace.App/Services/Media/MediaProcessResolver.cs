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
        var renderer = await FindProcessAsync(audioIdentity, true, cancellationToken).ConfigureAwait(false);
        return renderer ?? (audioIdentity == sourceAppUserModelId ? null : await FindProcessAsync(sourceAppUserModelId, true, cancellationToken).ConfigureAwait(false));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetApplicationUserModelId(SafeProcessHandle process, ref uint length, StringBuilder applicationId);

    public Task<uint?> ResolveAsync(string sourceAppUserModelId, CancellationToken cancellationToken) =>
        FindProcessAsync(sourceAppUserModelId, false, cancellationToken);

    private static Task<uint?> FindProcessAsync(string sourceAppUserModelId, bool forAudio, CancellationToken cancellationToken) => Task.Run(() =>
    {
        if (string.IsNullOrWhiteSpace(sourceAppUserModelId)) return (uint?)null;
        var matches = new List<(uint Id, bool HasWindow)>();
        using var self = Process.GetCurrentProcess();
        var sessionId = self.SessionId;
        var processes = Process.GetProcesses();
        try
        {
            // Process identity resolution happens only on a media-source transition. Scanning
            // an arbitrary first 512 processes could miss Apple Music's detached renderer when
            // a machine has many background processes, so inspect the complete OS-provided
            // snapshot while keeping each process handle short-lived.
            foreach (var process in processes)
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
                    if ((exeMatch || identityMatch) && (!forAudio || process.SessionId == sessionId))
                        matches.Add((processId, process.MainWindowHandle != 0));
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
            if (matches.Count == 1) return matches[0].Id;
            if (forAudio && matches.Count > 1 &&
                AudioProcessTree.CommonPlayerRoot(matches.Select(m => m.Id).ToHashSet(), processes) is { } root)
                return root;
            // Independent instances still require an unambiguous window.
            var visible = matches.Where(match => match.HasWindow).ToArray();
            return visible.Length == 1 ? visible[0].Id : (uint?)null;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }, cancellationToken);
}
