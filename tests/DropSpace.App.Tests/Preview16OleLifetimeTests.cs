using DropSpace.App.Services;
using DropSpace.Infrastructure.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;

namespace DropSpace.App.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class Preview16OleLifetimeTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SourceOwnershipIsAcquiredBeforeDropReturns(bool asyncMode)
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppStoragePaths(root);
            var classifier = new OleFileDataClassifier();
            var source = new VirtualSource(classifier, asyncMode);
            var stagingLeases = new StagingLeaseStore(paths, NullLogger<StagingLeaseStore>.Instance);
            var materializer = new VirtualFileMaterializer(paths, classifier, NullLogger<VirtualFileMaterializer>.Instance, stagingLeases);
            var task = materializer.MaterializeAsync(source);
            // A non-async source only guarantees IDataObject access through the OLE callback.
            // The implementation now takes ownership of its content medium before returning,
            // then copies owned bytes off-thread, so the materialization task may still be
            // running when the source releases its IDataObject.
            if (!asyncMode) Assert.IsGreaterThan(0, source.ContentDataRequests);
            source.DropReturned = true;
            var batch = await task;
            Assert.AreEqual(1, batch.Paths.Count);
            Assert.AreEqual(1, Directory.GetFiles(paths.StagingLeases, "*.json").Length);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(batch.Paths[0]));
            Assert.AreEqual(asyncMode ? 1 : 0, source.EndCount);
            Assert.IsTrue(await stagingLeases.CompleteAsync(batch.Lease));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task CancellationRemovesEntireOwnedBatch()
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppStoragePaths(root);
            var classifier = new OleFileDataClassifier();
            var stagingLeases = new StagingLeaseStore(paths, NullLogger<StagingLeaseStore>.Instance);
            var materializer = new VirtualFileMaterializer(paths, classifier, NullLogger<VirtualFileMaterializer>.Instance, stagingLeases);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => materializer.MaterializeAsync(new VirtualSource(classifier, false), cancellation.Token));
            Assert.AreEqual(0, Directory.GetFiles(paths.Staging, "*", SearchOption.AllDirectories).Length);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task DuplicateVirtualNamesPreserveEveryContentMedium(bool asyncMode, bool useStream)
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new AppStoragePaths(root);
            var classifier = new OleFileDataClassifier();
            var leases = new StagingLeaseStore(paths, NullLogger<StagingLeaseStore>.Instance);
            var materializer = new VirtualFileMaterializer(paths, classifier, NullLogger<VirtualFileMaterializer>.Instance, leases);
            var source = new VirtualSource(classifier, asyncMode, useStream, ["文件.txt", "文件.txt", "文件 (1).txt"]);
            var task = materializer.MaterializeAsync(source);
            source.DropReturned = true;
            var batch = await task;
            Assert.AreEqual(3, batch.Paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            for (var index = 0; index < batch.Paths.Count; index++)
            {
                CollectionAssert.AreEqual(new byte[] { 1, 2, 3, (byte)index }, await File.ReadAllBytesAsync(batch.Paths[index]));
            }
            Assert.AreEqual(asyncMode ? 1 : 0, source.EndCount);
            Assert.IsTrue(await leases.CompleteAsync(batch.Lease));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task FailedLaterMediumReleasesEarlierMarshaledReference()
    {
        var root = Path.Combine(Path.GetTempPath(), "DropSpace-tests", Guid.NewGuid().ToString("N"));
        VirtualSource? source = null;
        try
        {
            var paths = new AppStoragePaths(root);
            var classifier = new OleFileDataClassifier();
            var leases = new StagingLeaseStore(paths, NullLogger<StagingLeaseStore>.Instance);
            var materializer = new VirtualFileMaterializer(paths, classifier, NullLogger<VirtualFileMaterializer>.Instance, leases);
            source = new VirtualSource(classifier, false, true, ["first.txt", "second.txt"])
            {
                RetainFirstStream = true,
                FailContentIndex = 1,
            };
            await Assert.ThrowsAsync<IOException>(() => materializer.MaterializeAsync(source));
            Assert.AreNotEqual(nint.Zero, source.RetainedStream);
            _ = Marshal.AddRef(source.RetainedStream);
            Assert.AreEqual(1, Marshal.Release(source.RetainedStream), "Only the fixture's reference may remain after rollback.");
            Assert.AreEqual(0, Directory.GetFiles(paths.Staging, "*", SearchOption.AllDirectories).Length);
        }
        finally
        {
            if (source?.RetainedStream is { } stream && stream != nint.Zero) _ = Marshal.Release(stream);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed class VirtualSource(OleFileDataClassifier classifier, bool asyncMode, bool useStream = false, string[]? names = null) : IDataObject, VirtualFileMaterializer.IDataObjectAsyncCapability
    {
        public bool DropReturned { get; set; }
        public int ContentDataRequests { get; private set; }
        public int EndCount { get; private set; }
        public bool RetainFirstStream { get; init; }
        public int? FailContentIndex { get; init; }
        public nint RetainedStream { get; private set; }
        private bool _started;
        public void GetData(ref FORMATETC formatetc, out STGMEDIUM medium)
        {
            if (DropReturned && !_started) throw new InvalidOperationException("Source lifetime has ended.");
            byte[] bytes;
            if (formatetc.cfFormat == classifier.FileGroupDescriptorWClipboardFormat)
            {
                var fileNames = names ?? ["payload.txt"];
                bytes = new byte[4 + 592 * fileNames.Length];
                BitConverter.GetBytes(fileNames.Length).CopyTo(bytes, 0);
                // FILEDESCRIPTORW.FileName begins at offset 72 within the descriptor;
                // the native payload's four-byte count precedes it.
                for (var index = 0; index < fileNames.Length; index++)
                    Encoding.Unicode.GetBytes(fileNames[index]).CopyTo(bytes, 76 + 592 * index);
            }
            else
            {
                if (formatetc.lindex == FailContentIndex) throw new IOException("Injected source-medium failure.");
                ContentDataRequests++;
                bytes = names is null ? [1, 2, 3] : [1, 2, 3, checked((byte)formatetc.lindex)];
            }
            var handle = GlobalAlloc(0x42, (nuint)bytes.Length);
            var pointer = GlobalLock(handle);
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            GlobalUnlock(handle);
            if (useStream && formatetc.cfFormat == classifier.FileContentsClipboardFormat)
            {
                Marshal.ThrowExceptionForHR(CreateStreamOnHGlobal(handle, true, out var stream));
                if (RetainFirstStream && formatetc.lindex == 0)
                {
                    _ = Marshal.AddRef(stream);
                    RetainedStream = stream;
                }
                medium = new STGMEDIUM { tymed = TYMED.TYMED_ISTREAM, unionmember = stream };
                return;
            }
            medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = handle };
        }
        public int QueryGetData(ref FORMATETC formatetc) => 0;
        public void GetDataHere(ref FORMATETC formatetc, ref STGMEDIUM medium) => throw new NotSupportedException();
        public int GetCanonicalFormatEtc(ref FORMATETC input, out FORMATETC output) { output = default; return 1; }
        public void SetData(ref FORMATETC formatetc, ref STGMEDIUM medium, bool release) => throw new NotSupportedException();
        public IEnumFORMATETC EnumFormatEtc(DATADIR direction) => throw new NotSupportedException();
        public int DAdvise(ref FORMATETC formatetc, ADVF advf, IAdviseSink sink, out int connection) { connection = 0; return 1; }
        public void DUnadvise(int connection) => throw new NotSupportedException();
        public int EnumDAdvise(out IEnumSTATDATA? advise) { advise = null; return 1; }
        public int SetAsyncMode(bool mode) => 0;
        public int GetAsyncMode(out bool mode) { mode = asyncMode; return 0; }
        public int StartOperation(nint context) { _started = true; return 0; }
        public int InOperation(out bool active) { active = _started; return 0; }
        public int EndOperation(int result, nint context, uint effects) { EndCount++; _started = false; return 0; }
        [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint size);
        [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalUnlock(nint memory);
        [DllImport("ole32.dll")] private static extern int CreateStreamOnHGlobal(nint memory, [MarshalAs(UnmanagedType.Bool)] bool deleteOnRelease, out nint stream);
    }
}
