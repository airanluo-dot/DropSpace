using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DropSpace.App.Services.NeteaseEnhancement;

public sealed record NeteaseInstallation(string ExecutablePath, Version Version, Architecture Architecture);

public sealed class EnhancementDeploymentException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed class NeteaseInstallationProbe
{
    public Task<NeteaseInstallation?> FindAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        foreach (string candidate in Candidates().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { if (Inspect(candidate) is { } installation) return installation; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or EnhancementDeploymentException) { }
        }
        return null;
    }, cancellationToken);

    internal static NeteaseInstallation? Inspect(string path)
    {
        path = Path.GetFullPath(path);
        if (!string.Equals(Path.GetFileName(path), "cloudmusic.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return null;
        DeploymentPaths.AssertSafe(path);
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        Architecture architecture = pe.PEHeaders.CoffHeader.Machine switch
        {
            System.Reflection.PortableExecutable.Machine.Amd64 => Architecture.X64,
            System.Reflection.PortableExecutable.Machine.I386 => Architecture.X86,
            _ => throw new EnhancementDeploymentException("UnsupportedArchitecture")
        };
        var info = FileVersionInfo.GetVersionInfo(path);
        if (info.FileMajorPart < 2 || string.IsNullOrWhiteSpace(info.ProductName)) return null;
        return new(path, new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart), architecture);
    }

    private static IEnumerable<string> Candidates()
    {
        var paths = new List<string>();
        foreach (var process in Process.GetProcessesByName("cloudmusic"))
        {
            using (process)
            {
                try { if (process.MainModule?.FileName is { } path) paths.Add(path); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
            }
        }
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\cloudmusic.exe");
            if (key?.GetValue(null) is string path && Path.IsPathFullyQualified(path.Trim('"'))) paths.Add(path.Trim('"'));
        }
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.LocalApplicationData })
        {
            string root = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(root)) paths.Add(Path.Combine(root, "NetEase", "CloudMusic", "cloudmusic.exe"));
        }
        return paths;
    }
}

internal static class DeploymentPaths
{
    public static void AssertSafe(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.IndexOf(':', 2) >= 0)
            throw new EnhancementDeploymentException("UnsafePath");
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new EnhancementDeploymentException("UnsafePath");
        }
    }
}
