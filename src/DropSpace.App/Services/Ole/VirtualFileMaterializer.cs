using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using DropSpace.Infrastructure.Storage;
using Microsoft.Extensions.Logging;

namespace DropSpace.App.Services;

/// <summary>
/// Materializes FILEDESCRIPTORW/FILECONTENTS payloads on the OLE apartment that supplied them.
/// Content is copied in bounded chunks into an isolated staging batch and the whole batch is
/// rolled back on cancellation or failure.
/// </summary>
internal sealed class VirtualFileMaterializer
{
    private const int Success = 0;
    private const int MaximumItems = 1_000;
    private const int BufferSize = 128 * 1024;
    private const long MaximumFileBytes = 2L * 1024 * 1024 * 1024;
    private const long MaximumBatchBytes = 8L * 1024 * 1024 * 1024;
    private readonly AppStoragePaths _paths;
    private readonly OleFileDataClassifier _classifier;
    private readonly ILogger<VirtualFileMaterializer> _logger;

    public VirtualFileMaterializer(
        AppStoragePaths paths,
        OleFileDataClassifier classifier,
        ILogger<VirtualFileMaterializer> logger)
    {
        _paths = paths;
        _classifier = classifier;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> MaterializeAsync(
        IDataObject dataObject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataObject);
        var asyncCapability = dataObject as IDataObjectAsyncCapability;
        var asyncOperationStarted = false;
        var operationResult = 0;
        if (asyncCapability is not null &&
            asyncCapability.GetAsyncMode(out var asyncMode) >= 0 && asyncMode &&
            asyncCapability.StartOperation(nint.Zero) >= 0)
        {
            asyncOperationStarted = true;
        }
        var batchRoot = Path.Combine(_paths.Staging, $"virtual-{Guid.NewGuid():N}");
        try
        {
            _paths.EnsureCreated();
            Directory.CreateDirectory(batchRoot);
            // StartOperation and the first yield occur while Drop still owns the supplying STA.
            // Returning the incomplete task lets the OLE callback finish promptly; continuation
            // resumes on that same UI apartment and yields between bounded stream chunks.
            if (asyncOperationStarted) await Task.Yield();
            var descriptors = ReadDescriptors(dataObject);
            var paths = new List<string>(descriptors.Count);
            long totalBytes = 0;
            for (var index = 0; index < descriptors.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var descriptor = descriptors[index];
                var destination = GetUniqueConfinedPath(batchRoot, descriptor.FileName);
                var written = await WriteContentsAsync(dataObject, index, destination, asyncOperationStarted, cancellationToken);
                totalBytes = checked(totalBytes + written);
                if (written > MaximumFileBytes || totalBytes > MaximumBatchBytes)
                {
                    throw new InvalidDataException("A virtual-file payload exceeded the bounded staging limit.");
                }
                paths.Add(destination);
            }

            _logger.LogInformation(
                "Virtual-file batch materialized into confined staging: itemCount={ItemCount}, byteCount={ByteCount}. User filenames and paths were omitted.",
                paths.Count,
                totalBytes);
            return paths;
        }
        catch
        {
            operationResult = unchecked((int)0x80004005);
            try
            {
                Directory.Delete(batchRoot, recursive: true);
            }
            catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(cleanupException, "Virtual-file staging rollback could not remove the incomplete batch immediately.");
            }
            throw;
        }
        finally
        {
            if (asyncOperationStarted)
            {
                try { _ = asyncCapability!.EndOperation(operationResult, nint.Zero, operationResult == 0 ? 1u : 0u); }
                catch (Exception exception)
                {
                    _logger.LogWarning("OLE EndOperation failed: {Category}.", exception.GetType().Name);
                }
            }
        }
    }

    private IReadOnlyList<VirtualFileDescriptor> ReadDescriptors(IDataObject dataObject)
    {
        var format = CreateFormat(
            _classifier.FileGroupDescriptorWClipboardFormat,
            TYMED.TYMED_HGLOBAL,
            -1);
        if (dataObject.QueryGetData(ref format) != Success)
        {
            throw new InvalidDataException("The virtual-file descriptor format is unavailable.");
        }

        dataObject.GetData(ref format, out var medium);
        try
        {
            if (medium.tymed != TYMED.TYMED_HGLOBAL || medium.unionmember == nint.Zero)
            {
                throw new InvalidDataException("The virtual-file descriptor medium is invalid.");
            }

            var size = checked((long)GlobalSize(medium.unionmember).ToUInt64());
            var pointer = GlobalLock(medium.unionmember);
            if (pointer == nint.Zero)
            {
                throw new InvalidDataException("The virtual-file descriptor memory could not be locked.");
            }

            try
            {
                var count = Marshal.ReadInt32(pointer);
                var descriptorSize = Marshal.SizeOf<FileDescriptorW>();
                if (count is < 1 or > MaximumItems || sizeof(uint) + (long)count * descriptorSize > size)
                {
                    throw new InvalidDataException("The virtual-file descriptor count or size is invalid.");
                }

                var descriptors = new List<VirtualFileDescriptor>(count);
                for (var index = 0; index < count; index++)
                {
                    var native = Marshal.PtrToStructure<FileDescriptorW>(
                        pointer + sizeof(uint) + index * descriptorSize);
                    var safeName = ValidateLeafName(native.FileName);
                    var announcedSize = ((long)native.FileSizeHigh << 32) | native.FileSizeLow;
                    if (announcedSize < 0 || announcedSize > MaximumFileBytes)
                    {
                        throw new InvalidDataException("A virtual file announced an unsupported size.");
                    }
                    descriptors.Add(new VirtualFileDescriptor(safeName, announcedSize));
                }
                return descriptors;
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

    private async Task<long> WriteContentsAsync(
        IDataObject dataObject,
        int index,
        string destination,
        bool allowAsync,
        CancellationToken cancellationToken)
    {
        var format = CreateFormat(
            _classifier.FileContentsClipboardFormat,
            TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL,
            index);
        if (dataObject.QueryGetData(ref format) != Success)
        {
            throw new InvalidDataException("A virtual-file content stream is unavailable.");
        }

        dataObject.GetData(ref format, out var medium);
        try
        {
            using var output = new FileStream(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.WriteThrough | FileOptions.SequentialScan);
            return medium.tymed switch
            {
                TYMED.TYMED_ISTREAM => await CopyComStreamAsync(medium.unionmember, output, allowAsync, cancellationToken),
                TYMED.TYMED_HGLOBAL => await CopyGlobalMemoryAsync(medium.unionmember, output, allowAsync, cancellationToken),
                _ => throw new InvalidDataException("The virtual-file content medium is unsupported."),
            };
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }

    private static async Task<long> CopyComStreamAsync(
        nint unknown,
        Stream destination,
        bool allowAsync,
        CancellationToken cancellationToken)
    {
        if (unknown == nint.Zero)
        {
            throw new InvalidDataException("The virtual-file stream pointer is null.");
        }
        var stream = (IStream)Marshal.GetObjectForIUnknown(unknown);
        var buffer = new byte[BufferSize];
        var countPointer = Marshal.AllocCoTaskMem(sizeof(int));
        long total = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                stream.Read(buffer, buffer.Length, countPointer);
                var count = Marshal.ReadInt32(countPointer);
                if (count <= 0)
                {
                    break;
                }
                total = checked(total + count);
                if (total > MaximumFileBytes)
                {
                    throw new InvalidDataException("A virtual-file stream exceeded the per-file limit.");
                }
                if (allowAsync) await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                else destination.Write(buffer, 0, count);
                if (allowAsync) await Task.Yield();
            }
            return total;
        }
        finally
        {
            Marshal.FreeCoTaskMem(countPointer);
            if (Marshal.IsComObject(stream))
            {
                _ = Marshal.ReleaseComObject(stream);
            }
        }
    }

    private static async Task<long> CopyGlobalMemoryAsync(
        nint memory,
        Stream destination,
        bool allowAsync,
        CancellationToken cancellationToken)
    {
        if (memory == nint.Zero)
        {
            throw new InvalidDataException("The virtual-file memory handle is null.");
        }
        var length = checked((long)GlobalSize(memory).ToUInt64());
        if (length > MaximumFileBytes)
        {
            throw new InvalidDataException("A virtual-file memory payload exceeded the per-file limit.");
        }
        var pointer = GlobalLock(memory);
        if (pointer == nint.Zero)
        {
            throw new InvalidDataException("The virtual-file memory could not be locked.");
        }
        try
        {
            var buffer = new byte[BufferSize];
            long offset = 0;
            while (offset < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = (int)Math.Min(buffer.Length, length - offset);
                Marshal.Copy(pointer + checked((int)offset), buffer, 0, count);
                if (allowAsync) await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                else destination.Write(buffer, 0, count);
                offset += count;
                if (allowAsync) await Task.Yield();
            }
            return length;
        }
        finally
        {
            _ = GlobalUnlock(memory);
        }
    }

    private static string GetUniqueConfinedPath(string root, string leafName)
    {
        var stem = Path.GetFileNameWithoutExtension(leafName);
        var extension = Path.GetExtension(leafName);
        for (var suffix = 0; suffix < 10_000; suffix++)
        {
            var name = suffix == 0 ? leafName : $"{stem} ({suffix}){extension}";
            var candidate = Path.GetFullPath(Path.Combine(root, name));
            if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("A virtual filename escaped the staging root.");
            }
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new IOException("A unique virtual filename could not be allocated.");
    }

    private static string ValidateLeafName(string? name)
    {
        var trimmed = name?.Trim().TrimEnd('.') ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed) ||
            !string.Equals(Path.GetFileName(trimmed), trimmed, StringComparison.Ordinal) ||
            trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidDataException("A virtual filename was unsafe.");
        }
        return trimmed;
    }

    private static FORMATETC CreateFormat(short format, TYMED medium, int index) => new()
    {
        cfFormat = format,
        dwAspect = DVASPECT.DVASPECT_CONTENT,
        lindex = index,
        ptd = nint.Zero,
        tymed = medium,
    };

    private readonly record struct VirtualFileDescriptor(string FileName, long AnnouncedSize);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileDescriptorW
    {
        public uint Flags;
        public Guid ClassId;
        public int SizeX;
        public int SizeY;
        public int PointX;
        public int PointY;
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string FileName;
    }

    [ComImport]
    [Guid("3D8B0590-F691-11D2-8EA9-006097DF5BD4")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDataObjectAsyncCapability
    {
        [PreserveSig] int SetAsyncMode([MarshalAs(UnmanagedType.Bool)] bool asyncMode);
        [PreserveSig] int GetAsyncMode([MarshalAs(UnmanagedType.Bool)] out bool asyncMode);
        [PreserveSig] int StartOperation(nint bindContext);
        [PreserveSig] int InOperation([MarshalAs(UnmanagedType.Bool)] out bool inOperation);
        [PreserveSig] int EndOperation(int result, nint bindContext, uint effects);
    }

    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint GlobalSize(nint memory);
}
