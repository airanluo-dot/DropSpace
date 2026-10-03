using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DropSpace.Infrastructure.Tests;

internal static class WindowsReparseFixture
{
    internal static void RequireSymbolicLinks(bool directory)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-link-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, "target");
        var link = Path.Combine(root, "link");
        try
        {
            if (directory) { Directory.CreateDirectory(target); Directory.CreateSymbolicLink(link, target); }
            else { File.WriteAllText(target, "fixture"); File.CreateSymbolicLink(link, target); }
        }
        catch (UnauthorizedAccessException error)
        {
            Assert.Inconclusive($"Actual Windows {(directory ? "directory" : "file")} symlink probe failed: {error.GetType().Name}, HRESULT 0x{error.HResult:X8}. No permission/security settings were changed.");
        }
        catch (IOException error) when ((error.HResult & 0xffff) is 1314 or 5)
        {
            Assert.Inconclusive($"Actual Windows {(directory ? "directory" : "file")} symlink probe failed: Win32 {error.HResult & 0xffff}, HRESULT 0x{error.HResult:X8}. No permission/security settings were changed.");
        }
        finally
        {
            if (directory && Directory.Exists(link)) Directory.Delete(link);
            else if (File.Exists(link)) File.Delete(link);
            Directory.Delete(root, recursive: true);
        }
    }

    // An NTFS mount-point reparse tag can be created by an ordinary user. This
    // adds real Windows containment coverage without granting symlink privileges.
    internal static void CreateJunction(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        link = Path.GetFullPath(link); target = Path.GetFullPath(target);
        Assert.IsTrue(link.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) && target.StartsWith(temporary, StringComparison.OrdinalIgnoreCase),
            "Both junction paths must stay in the test-owned temporary tree.");
        Directory.CreateDirectory(link);
        using var handle = CreateFileW(link, 0x40000000, 0, 0, 3, 0x02200000, 0);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
        var print = Encoding.Unicode.GetBytes(target);
        var buffer = new byte[16 + substitute.Length + 2 + print.Length + 2];
        BitConverter.TryWriteBytes(buffer.AsSpan(0, 4), 0xA0000003u);
        BitConverter.TryWriteBytes(buffer.AsSpan(4, 2), checked((ushort)(buffer.Length - 8)));
        BitConverter.TryWriteBytes(buffer.AsSpan(10, 2), checked((ushort)substitute.Length));
        BitConverter.TryWriteBytes(buffer.AsSpan(12, 2), checked((ushort)(substitute.Length + 2)));
        BitConverter.TryWriteBytes(buffer.AsSpan(14, 2), checked((ushort)print.Length));
        substitute.CopyTo(buffer, 16); print.CopyTo(buffer, 18 + substitute.Length);
        if (!DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, 0, 0, out _, 0))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        Assert.IsTrue((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputBytes, nint output, int outputBytes, out int returned, nint overlapped);
}
