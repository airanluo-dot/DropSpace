using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DropSpace.Infrastructure.Storage;

internal static class ReparseSafeFileOpen
{
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const int FileAttributeTagInfoClass = 9;

    public static FileStream OpenRead(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows())
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException("Transfer source files must not be reparse points.");
            }
            return new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81_920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        var handle = CreateFile(
            path,
            GenericRead,
            FileShareRead,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagSequentialScan | FileFlagOverlapped,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new IOException("The transfer source could not be opened.", new Win32Exception(Marshal.GetLastWin32Error()));
        }

        try
        {
            if (!GetFileInformationByHandleEx(
                    handle,
                    FileAttributeTagInfoClass,
                    out var tagInfo,
                    (uint)Marshal.SizeOf<FileAttributeTagInfo>()))
            {
                throw new IOException("The transfer source attributes could not be verified.", new Win32Exception(Marshal.GetLastWin32Error()));
            }
            if (tagInfo.FileAttributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidDataException("Transfer source files must not be reparse points.");
            }

            return new FileStream(handle, FileAccess.Read, 81_920, isAsync: true);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        public FileAttributes FileAttributes;
        public uint ReparseTag;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        out FileAttributeTagInfo fileInformation,
        uint bufferSize);
}
