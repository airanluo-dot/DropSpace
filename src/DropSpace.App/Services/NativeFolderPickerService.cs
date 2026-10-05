using Windows.Storage.Pickers;
using WinRT.Interop;

namespace DropSpace.App.Services;

public sealed class NativeFolderPickerService
{
    public static string GetDownloadsDirectory()
    {
        var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        var result = SHGetKnownFolderPath(ref id, 0, 0, out var path);
        System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(result);
        try { return System.Runtime.InteropServices.Marshal.PtrToStringUni(path) ?? throw new IOException("Downloads folder unavailable."); }
        finally { System.Runtime.InteropServices.Marshal.FreeCoTaskMem(path); }
    }
    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, nint token, out nint path);

    public static void OpenDirectory(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("An absolute directory is required.");
        Directory.CreateDirectory(path);
        var start = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        start.ArgumentList.Add(Path.GetFullPath(path));
        System.Diagnostics.Process.Start(start)?.Dispose();
    }
    public async Task<string?> PickAsync(nint windowHandle)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, windowHandle);
        return (await picker.PickSingleFolderAsync())?.Path;
    }
}
