namespace DropSpace.Core.Models;

public sealed record ItemCapabilities(
    bool CanOpen,
    bool CanCopy,
    bool CanDrag,
    bool CanExport,
    bool CanLocate,
    bool CanPin,
    bool CanRemove)
{
    public static ItemCapabilities For(DropItem item)
    {
        var isFile = item.Kind is ItemKind.File or ItemKind.Folder;
        var isReadable = item.Status == ItemStatus.Available;

        return new ItemCapabilities(
            CanOpen: isReadable && (isFile || item.Kind == ItemKind.Url),
            CanCopy: isReadable && item.Kind != ItemKind.Unknown,
            CanDrag: isReadable && isFile,
            CanExport: isReadable && item.Kind == ItemKind.Image,
            CanLocate: isFile && item.Status is ItemStatus.Missing or ItemStatus.Unavailable,
            CanPin: true,
            CanRemove: true);
    }
}
