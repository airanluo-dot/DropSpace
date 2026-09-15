using Windows.Storage.Pickers;
using WinRT.Interop;

namespace DropSpace.App.Services;

public sealed class NativeFolderPickerService
{
    public async Task<string?> PickAsync(nint windowHandle)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, windowHandle);
        return (await picker.PickSingleFolderAsync())?.Path;
    }
}
