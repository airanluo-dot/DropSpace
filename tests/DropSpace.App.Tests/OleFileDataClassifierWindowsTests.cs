using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using DropSpace.Core.DragDrop;

namespace DropSpace.App.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class OleFileDataClassifierWindowsTests
{
    [TestMethod]
    public void IstorageOnlyVirtualFileIsNotAdvertisedAsMaterializable()
    {
        var classifier = new OleFileDataClassifier();
        var dataObject = new FakeDataObject(
            (classifier.FileGroupDescriptorWClipboardFormat, TYMED.TYMED_HGLOBAL, -1),
            (classifier.FileContentsClipboardFormat, TYMED.TYMED_ISTORAGE, 0));

        var result = classifier.Classify(dataObject);

        Assert.AreEqual(OleFileDataKind.None, result.Kind);
        Assert.IsFalse(result.IsFileLikeEvidence);
        Assert.IsFalse(result.CanMaterialize);
        Assert.IsFalse(result.CanAuthorizeVisual);
    }

    [TestMethod]
    public void StreamBackedVirtualFileIsAcceptedByClassifierContract()
    {
        var classifier = new OleFileDataClassifier();
        var dataObject = new FakeDataObject(
            (classifier.FileGroupDescriptorWClipboardFormat, TYMED.TYMED_HGLOBAL, -1),
            (classifier.FileContentsClipboardFormat, TYMED.TYMED_ISTREAM, 0));

        var result = classifier.Classify(dataObject);

        Assert.AreEqual(OleFileDataKind.VirtualFiles, result.Kind);
        Assert.IsTrue(result.IsFileLikeEvidence);
        Assert.IsTrue(result.CanMaterialize);
        Assert.IsTrue(result.CanAuthorizeVisual);
    }

    private sealed class FakeDataObject : IDataObject
    {
        private const int Success = 0;
        private readonly (short Format, TYMED Medium, int Index)[] _entries;

        public FakeDataObject(params (short Format, TYMED Medium, int Index)[] entries)
        {
            _entries = entries;
        }

        public void GetData(ref FORMATETC formatetc, out STGMEDIUM medium) =>
            throw new NotSupportedException();

        public void GetDataHere(ref FORMATETC formatetc, ref STGMEDIUM medium) =>
            throw new NotSupportedException();

        public int QueryGetData(ref FORMATETC formatetc)
        {
            return _entries.Any(entry =>
                       entry.Format == formatetc.cfFormat &&
                       entry.Index == formatetc.lindex &&
                       (entry.Medium & formatetc.tymed) != 0)
                ? Success
                : 1;
        }

        public int GetCanonicalFormatEtc(ref FORMATETC formatetcIn, out FORMATETC formatetcOut)
        {
            formatetcOut = default;
            return 1;
        }

        public void SetData(ref FORMATETC formatetc, ref STGMEDIUM medium, bool release) =>
            throw new NotSupportedException();

        public IEnumFORMATETC EnumFormatEtc(DATADIR direction) =>
            throw new NotSupportedException();

        public int DAdvise(
            ref FORMATETC formatetc,
            ADVF advf,
            IAdviseSink adviseSink,
            out int connection)
        {
            connection = 0;
            return 1;
        }

        public void DUnadvise(int connection) =>
            throw new NotSupportedException();

        public void EnumDAdvise(out IEnumSTATDATA? enumAdvise) => enumAdvise = null;
    }
}
