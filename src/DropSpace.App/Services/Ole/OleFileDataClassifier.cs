using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using DropSpace.Core.DragDrop;

namespace DropSpace.App.Services;

internal sealed class OleFileDataClassifier
{
    private const int Success = 0;
    private const short ClipboardFormatHDrop = 15;
    private const uint QueryAllFiles = 0xFFFFFFFF;
    private const int MaximumItemCount = 256;
    private const int MaximumPidlSegments = 4_096;
    private static readonly short ShellIdListFormat = RegisterFormat("Shell IDList Array");
    private static readonly short FileGroupDescriptorWFormat = RegisterFormat("FileGroupDescriptorW");
    private static readonly short FileContentsFormat = RegisterFormat("FileContents");

    internal short ShellIdListClipboardFormat => ShellIdListFormat;

    internal short FileGroupDescriptorWClipboardFormat => FileGroupDescriptorWFormat;

    internal short FileContentsClipboardFormat => FileContentsFormat;

    public OleFileDataClassification Classify(IDataObject dataObject)
    {
        ArgumentNullException.ThrowIfNull(dataObject);
        if (HasFormat(dataObject, ClipboardFormatHDrop, TYMED.TYMED_HGLOBAL))
        {
            return new OleFileDataClassification(
                OleFileDataKind.FileSystemPaths,
                0,
                OlePreferredDropEffect.Copy,
                true,
                true,
                false);
        }

        if (ShellIdListFormat != 0 && HasFormat(dataObject, ShellIdListFormat, TYMED.TYMED_HGLOBAL))
        {
            return new OleFileDataClassification(
                OleFileDataKind.ShellItems,
                0,
                OlePreferredDropEffect.Copy,
                true,
                false,
                false);
        }

        if (FileGroupDescriptorWFormat != 0 &&
            FileContentsFormat != 0 &&
            HasFormat(dataObject, FileGroupDescriptorWFormat, TYMED.TYMED_HGLOBAL) &&
            HasFormat(
                dataObject,
                FileContentsFormat,
                TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL | TYMED.TYMED_ISTORAGE,
                index: 0))
        {
            return new OleFileDataClassification(
                OleFileDataKind.VirtualFiles,
                0,
                OlePreferredDropEffect.Copy,
                true,
                false,
                true);
        }

        return OleFileDataClassification.None;
    }

    public IReadOnlyList<string> ReadFileSystemPaths(
        IDataObject dataObject,
        OleFileDataClassification classification) =>
        classification.Kind switch
        {
            OleFileDataKind.FileSystemPaths => ReadHDropPaths(dataObject),
            OleFileDataKind.ShellItems => ReadShellItemPaths(dataObject),
            _ => [],
        };

    public OleFileDataClassification ResolveAcceptance(
        IDataObject dataObject,
        OleFileDataClassification classification)
    {
        if (classification.Kind == OleFileDataKind.FileSystemPaths)
        {
            return classification;
        }

        if (classification.Kind == OleFileDataKind.ShellItems)
        {
            var paths = ReadShellItemPaths(dataObject);
            return classification with
            {
                ItemCount = paths.Count,
                CanAcceptNow = paths.Count > 0,
            };
        }

        return classification;
    }

    private static bool HasFormat(
        IDataObject dataObject,
        short format,
        TYMED medium,
        int index = -1)
    {
        var formatEtc = CreateFormat(format, medium, index);
        return dataObject.QueryGetData(ref formatEtc) == Success;
    }

    private static IReadOnlyList<string> ReadHDropPaths(IDataObject dataObject)
    {
        var format = CreateFormat(ClipboardFormatHDrop, TYMED.TYMED_HGLOBAL);
        if (dataObject.QueryGetData(ref format) != Success)
        {
            return [];
        }

        dataObject.GetData(ref format, out var medium);
        try
        {
            if (medium.tymed != TYMED.TYMED_HGLOBAL || medium.unionmember == nint.Zero)
            {
                return [];
            }

            var count = Math.Min(DragQueryFile(medium.unionmember, QueryAllFiles, null, 0), MaximumItemCount);
            var paths = new List<string>(checked((int)count));
            for (uint index = 0; index < count; index++)
            {
                var length = DragQueryFile(medium.unionmember, index, null, 0);
                if (length == 0 || length > short.MaxValue)
                {
                    continue;
                }

                var buffer = new char[length + 1];
                if (DragQueryFile(medium.unionmember, index, buffer, (uint)buffer.Length) > 0)
                {
                    paths.Add(new string(buffer, 0, checked((int)length)));
                }
            }

            return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }

    private static IReadOnlyList<string> ReadShellItemPaths(IDataObject dataObject)
    {
        var officialPaths = TryReadShellItemArrayPaths(dataObject);
        if (officialPaths.Count > 0)
        {
            return officialPaths;
        }

        // Bounded CIDA parsing is retained only as a compatibility fallback for providers that
        // advertise Shell IDList Array but do not implement SHCreateShellItemArrayFromDataObject.
        var format = CreateFormat(ShellIdListFormat, TYMED.TYMED_HGLOBAL);
        if (dataObject.QueryGetData(ref format) != Success)
        {
            return [];
        }

        dataObject.GetData(ref format, out var medium);
        try
        {
            if (medium.tymed != TYMED.TYMED_HGLOBAL || medium.unionmember == nint.Zero)
            {
                return [];
            }

            var byteLength = checked((long)GlobalSize(medium.unionmember).ToUInt64());
            if (byteLength < sizeof(uint) * 2)
            {
                return [];
            }

            var pointer = GlobalLock(medium.unionmember);
            if (pointer == nint.Zero)
            {
                return [];
            }

            try
            {
                var itemCount = Marshal.ReadInt32(pointer);
                if (itemCount is < 1 or > MaximumItemCount)
                {
                    return [];
                }

                var offsetTableLength = checked(sizeof(uint) * (itemCount + 2L));
                if (offsetTableLength > byteLength)
                {
                    return [];
                }

                var parentOffset = checked((uint)Marshal.ReadInt32(pointer, sizeof(uint)));
                if (!IsPidlWithinBuffer(pointer, parentOffset, byteLength))
                {
                    return [];
                }

                var parent = pointer + checked((int)parentOffset);
                var paths = new List<string>(itemCount);
                for (var index = 0; index < itemCount; index++)
                {
                    var itemOffset = checked((uint)Marshal.ReadInt32(pointer, sizeof(uint) * (index + 2)));
                    if (!IsPidlWithinBuffer(pointer, itemOffset, byteLength))
                    {
                        continue;
                    }

                    var absolute = ILCombine(parent, pointer + checked((int)itemOffset));
                    if (absolute == nint.Zero)
                    {
                        continue;
                    }

                    try
                    {
                        var path = new char[32_768];
                        if (SHGetPathFromIDList(absolute, path))
                        {
                            var terminator = Array.IndexOf(path, '\0');
                            if (terminator > 0)
                            {
                                paths.Add(new string(path, 0, terminator));
                            }
                        }
                    }
                    finally
                    {
                        ILFree(absolute);
                    }
                }

                return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }
            finally
            {
                _ = GlobalUnlock(medium.unionmember);
            }
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }

    private static IReadOnlyList<string> TryReadShellItemArrayPaths(IDataObject dataObject)
    {
        var interfaceId = typeof(IShellItemArray).GUID;
        if (SHCreateShellItemArrayFromDataObject(dataObject, ref interfaceId, out var array) < 0 || array is null)
        {
            return [];
        }
        try
        {
            if (array.GetCount(out var count) < 0 || count > MaximumItemCount)
            {
                return [];
            }
            var paths = new List<string>((int)count);
            for (uint index = 0; index < count; index++)
            {
                if (array.GetItemAt(index, out var item) < 0 || item is null)
                {
                    continue;
                }
                try
                {
                    if (item.GetDisplayName(0x80058000, out var value) >= 0 && value != nint.Zero)
                    {
                        try
                        {
                            var path = Marshal.PtrToStringUni(value);
                            if (!string.IsNullOrWhiteSpace(path))
                            {
                                paths.Add(path);
                            }
                        }
                        finally
                        {
                            Marshal.FreeCoTaskMem(value);
                        }
                    }
                }
                finally
                {
                    if (Marshal.IsComObject(item)) _ = Marshal.ReleaseComObject(item);
                }
            }
            return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (COMException)
        {
            return [];
        }
        finally
        {
            if (Marshal.IsComObject(array)) _ = Marshal.ReleaseComObject(array);
        }
    }

    private static bool IsPidlWithinBuffer(nint buffer, uint offset, long byteLength)
    {
        var cursor = (long)offset;
        for (var segment = 0; segment < MaximumPidlSegments; segment++)
        {
            if (cursor < 0 || cursor + sizeof(ushort) > byteLength)
            {
                return false;
            }

            var segmentLength = unchecked((ushort)Marshal.ReadInt16(buffer, checked((int)cursor)));
            if (segmentLength == 0)
            {
                return true;
            }

            if (segmentLength < sizeof(ushort) || cursor + segmentLength > byteLength)
            {
                return false;
            }

            cursor += segmentLength;
        }

        return false;
    }

    private static FORMATETC CreateFormat(short format, TYMED medium, int index = -1) => new()
    {
        cfFormat = format,
        dwAspect = DVASPECT.DVASPECT_CONTENT,
        lindex = index,
        ptd = nint.Zero,
        tymed = medium,
    };

    private static short RegisterFormat(string formatName) =>
        unchecked((short)RegisterClipboardFormat(formatName));

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(nint drop, uint file, [Out] char[]? fileName, uint characterCount);

    [DllImport("shell32.dll")]
    private static extern nint ILCombine(nint parent, nint child);

    [DllImport("shell32.dll")]
    private static extern void ILFree(nint itemIdList);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHGetPathFromIDList(nint itemIdList, [Out] char[] path);

    [DllImport("shell32.dll")]
    private static extern int SHCreateShellItemArrayFromDataObject(
        [MarshalAs(UnmanagedType.Interface)] IDataObject dataObject,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemArray? shellItemArray);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint GlobalSize(nint memory);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string format);

    [ComImport]
    [Guid("B63EA76D-1F85-456F-A19C-48159EFA858B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        [PreserveSig] int BindToHandler(nint bindContext, ref Guid handlerId, ref Guid interfaceId, out nint value);
        [PreserveSig] int GetPropertyStore(int flags, ref Guid interfaceId, out nint value);
        [PreserveSig] int GetPropertyDescriptionList(nint keyType, ref Guid interfaceId, out nint value);
        [PreserveSig] int GetAttributes(uint flags, uint mask, out uint attributes);
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemAt(uint index, out IShellItem? item);
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(nint bindContext, ref Guid handlerId, ref Guid interfaceId, out nint value);
        [PreserveSig] int GetParent(out IShellItem? parent);
        [PreserveSig] int GetDisplayName(uint displayNameType, out nint name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }
}
