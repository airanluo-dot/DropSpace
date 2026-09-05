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
            var classifier = new OleFileDataClassifier();
            var source = new VirtualSource(classifier, asyncMode);
            var materializer = new VirtualFileMaterializer(new AppStoragePaths(root), classifier, NullLogger<VirtualFileMaterializer>.Instance);
            var task = materializer.MaterializeAsync(source);
            if (!asyncMode) Assert.IsTrue(task.IsCompletedSuccessfully);
            source.DropReturned = true;
            var files = await task;
            Assert.AreEqual(1, files.Count);
            CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(files[0]));
            Assert.AreEqual(asyncMode ? 1 : 0, source.EndCount);
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
            var materializer = new VirtualFileMaterializer(paths, classifier, NullLogger<VirtualFileMaterializer>.Instance);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => materializer.MaterializeAsync(new VirtualSource(classifier, false), cancellation.Token));
            Assert.AreEqual(0, Directory.GetFiles(paths.Staging, "*", SearchOption.AllDirectories).Length);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class VirtualSource(OleFileDataClassifier classifier, bool asyncMode) : IDataObject, VirtualFileMaterializer.IDataObjectAsyncCapability
    {
        public bool DropReturned { get; set; }
        public int EndCount { get; private set; }
        private bool _started;
        public void GetData(ref FORMATETC formatetc, out STGMEDIUM medium)
        {
            if (DropReturned && !_started) throw new InvalidOperationException("Source lifetime has ended.");
            byte[] bytes;
            if (formatetc.cfFormat == classifier.FileGroupDescriptorWClipboardFormat)
            {
                bytes = new byte[596];
                BitConverter.GetBytes(1).CopyTo(bytes, 0);
                // FILEDESCRIPTORW.FileName begins at offset 72 within the descriptor;
                // the native payload's four-byte count precedes it.
                Encoding.Unicode.GetBytes("payload.txt").CopyTo(bytes, 76);
            }
            else bytes = [1, 2, 3];
            var handle = GlobalAlloc(0x42, (nuint)bytes.Length);
            var pointer = GlobalLock(handle);
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            GlobalUnlock(handle);
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
    }
}
