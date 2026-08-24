using System.Security.Cryptography;
using System.Text;

namespace DropSpace.Core.Displays;

public static class DisplayIdentity
{
    public static string CreatePersistentId(string monitorDevicePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(monitorDevicePath);
        var normalized = Normalize(monitorDevicePath);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return $"display:{digest}";
    }

    public static string CreateRuntimeFallbackId(nint monitorHandle) =>
        $"runtime:{monitorHandle.ToInt64():X}";

    public static bool IsPersistentId(string id) =>
        !string.IsNullOrWhiteSpace(id) && id.StartsWith("display:", StringComparison.Ordinal);

    public static string Normalize(string monitorDevicePath) =>
        CollapseRepeatedSeparators(monitorDevicePath.Trim().Replace('/', '\\')).ToUpperInvariant();

    private static string CollapseRepeatedSeparators(string value)
    {
        var prefix = value.StartsWith("\\\\", StringComparison.Ordinal)
            ? "\\\\"
            : string.Empty;
        var remainder = prefix.Length == 0 ? value : value[prefix.Length..];
        while (remainder.Contains("\\\\", StringComparison.Ordinal))
        {
            remainder = remainder.Replace("\\\\", "\\", StringComparison.Ordinal);
        }

        return prefix + remainder;
    }
}
