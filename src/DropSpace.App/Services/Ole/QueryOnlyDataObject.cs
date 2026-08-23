using System.Runtime.InteropServices.ComTypes;

namespace DropSpace.App.Services;

internal readonly record struct OleFormatAdvertisement(short Format, TYMED Medium, int Index = -1);

/// <summary>
/// Query-only synthetic IDataObject used by the executable Windows smoke. It never supplies or
/// reads payload bytes; it verifies that the classifier's format negotiation is fail-closed.
/// </summary>
internal sealed class QueryOnlyDataObject(params OleFormatAdvertisement[] formats) : IDataObject
{
    private const int FormatNotSupported = unchecked((int)0x80040064);
    private const int NotImplemented = unchecked((int)0x80004001);
    private readonly OleFormatAdvertisement[] _formats = formats;

    public int QueryGetData(ref FORMATETC format) =>
        format.dwAspect == DVASPECT.DVASPECT_CONTENT &&
        _formats.Any(candidate =>
            candidate.Format == format.cfFormat &&
            candidate.Index == format.lindex &&
            (candidate.Medium & format.tymed) != 0)
            ? 0
            : FormatNotSupported;

    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        medium = default;
        throw new NotSupportedException();
    }

    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) =>
        throw new NotSupportedException();

    public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut)
    {
        formatOut = formatIn;
        formatOut.ptd = nint.Zero;
        return NotImplemented;
    }

    public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release) =>
        throw new NotSupportedException();

    public IEnumFORMATETC EnumFormatEtc(DATADIR direction) =>
        throw new NotSupportedException();

    public int DAdvise(ref FORMATETC format, ADVF flags, IAdviseSink adviseSink, out int connection)
    {
        connection = 0;
        return NotImplemented;
    }

    public void DUnadvise(int connection) => throw new NotSupportedException();

    public int EnumDAdvise(out IEnumSTATDATA enumAdvise)
    {
        enumAdvise = null!;
        return NotImplemented;
    }
}
