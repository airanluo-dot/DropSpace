using System.Runtime.InteropServices;

namespace DropSpace.App.Services;

internal static class OleDropTargetNative
{
    private const int DragDropAlreadyRegistered = unchecked((int)0x80040101);

    public static void Register(nint windowHandle, IOleDropTarget dropTarget)
    {
        var result = RegisterDragDrop(windowHandle, dropTarget);
        if (result == DragDropAlreadyRegistered)
        {
            throw new InvalidOperationException($"HWND {windowHandle} already has an OLE drop target.");
        }

        if (result < 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }
    }

    public static int Revoke(nint windowHandle) => RevokeDragDrop(windowHandle);

    [DllImport("ole32.dll")]
    private static extern int RegisterDragDrop(
        nint window,
        [MarshalAs(UnmanagedType.Interface)] IOleDropTarget dropTarget);

    [DllImport("ole32.dll")]
    private static extern int RevokeDragDrop(nint window);
}
