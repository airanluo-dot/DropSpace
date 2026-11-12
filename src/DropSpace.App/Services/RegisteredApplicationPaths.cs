using Microsoft.Win32;

namespace DropSpace.App.Services;

internal static class RegisteredApplicationPaths
{
    internal static IEnumerable<string> ForExecutable(string fileName)
    {
        if (!fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) yield break;

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + fileName);
            if (key?.GetValue(null) is not string value) continue;
            var path = value.Trim('"');
            if (Path.IsPathFullyQualified(path)) yield return path;
        }
    }
}
