using System.Diagnostics;

namespace DropSpace.Infrastructure.Lyrics;

/// <summary>Owns one inference process and its redirected streams.</summary>
internal sealed class LocalInferenceProcess : IDisposable
{
    private readonly IDisposable? _limits;

    internal LocalInferenceProcess(Process process, StreamReader output, StreamReader errors, IDisposable? limits = null)
    {
        Process = process;
        StandardOutput = output;
        StandardError = errors;
        _limits = limits;
    }

    internal Process Process { get; }
    internal StreamReader StandardOutput { get; }
    internal StreamReader StandardError { get; }

    internal static LocalInferenceProcess Start(ProcessStartInfo start)
    {
        if (OperatingSystem.IsWindows()) return WindowsInferenceProcess.Start(start);
        // Development/test support only. Production is Windows and always requires the native limits.
        var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new IOException("Local inference failed to start.");
            process.StandardInput.Close();
            return new LocalInferenceProcess(process, process.StandardOutput, process.StandardError);
        }
        catch { process.Dispose(); throw; }
    }

    public void Dispose()
    {
        // Closing the only job handle kills the child even if normal shutdown fails.
        _limits?.Dispose();
        try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        StandardOutput.Dispose();
        StandardError.Dispose();
        Process.Dispose();
    }
}
