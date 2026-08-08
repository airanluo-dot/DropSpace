using System.Security.Cryptography;
using System.Text;

namespace DropSpace.Core.Policies;

public static class FingerprintService
{
    public static string ForText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return ToHex(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    public static string ForBytes(ReadOnlySpan<byte> bytes) => ToHex(SHA256.HashData(bytes));

    private static string ToHex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
