using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Graphics.Effects;

namespace DropSpace.App.Services;

/// <summary>
/// Dependency-free effect descriptions for the isolated shutter-coverage prototype.
/// Sources must be parameters from the same Windows.UI.Composition effect graph.
/// Gaussian is an isotropic material-softness comparison, not directional motion blur.
/// </summary>
internal static class CompositionEffectDescriptions
{
    internal static Guid GraphicsEffectD2D1InteropIid => EffectInteropVtable.InterfaceId;

    internal static ArithmeticCompositeEffectDescription CreateThreeTapAverage(
        IGraphicsEffectSource source0,
        IGraphicsEffectSource source1,
        IGraphicsEffectSource source2,
        string name = "Shutter")
    {
        var pair = new ArithmeticCompositeEffectDescription(
            name + "Pair", source0, source1,
            multiplyAmount: 0, source1Amount: 1f / 3, source2Amount: 1f / 3, offset: 0);
        return new ArithmeticCompositeEffectDescription(
            name, pair, source2,
            multiplyAmount: 0, source1Amount: 1, source2Amount: 1f / 3, offset: 0);
    }
}

/// <summary>Metadata consumed through IGraphicsEffectD2D1Interop, not a rendering device.</summary>
internal abstract class CompositionEffectDescription : IGraphicsEffect
{
    private string _name;

    protected CompositionEffectDescription(string name) => _name = name ?? throw new ArgumentNullException(nameof(name));

    public string Name
    {
        get => _name;
        set => _name = value ?? throw new ArgumentNullException(nameof(value));
    }

    internal abstract Guid EffectId { get; }
    internal abstract uint PropertyCount { get; }
    internal abstract uint SourceCount { get; }
    internal abstract object GetPropertyValue(uint index);
    internal abstract IGraphicsEffectSource GetSource(uint index);
    internal abstract bool TryGetPropertyMapping(string name, out uint index, out EffectInteropPropertyMapping mapping);

    protected static float RequireFinite(float value, string name) =>
        float.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(name);
}

/// <summary>k1*A*B + k2*A + k3*B + k4, applied equally to premultiplied RGBA.</summary>
[WinRT.WinRTExposedType(typeof(EffectInteropExposedTypeDetails))]
internal sealed class ArithmeticCompositeEffectDescription : CompositionEffectDescription
{
    // Windows SDK 10.0.26100.0, um/d2d1effects.h.
    private static readonly Guid ArithmeticCompositeId = new("FC151437-049A-4784-A24A-F1C4DAF20987");
    private readonly IGraphicsEffectSource _source1;
    private readonly IGraphicsEffectSource _source2;
    private readonly float[] _coefficients;
    private readonly bool _clampOutput;

    internal ArithmeticCompositeEffectDescription(
        string name,
        IGraphicsEffectSource source1,
        IGraphicsEffectSource source2,
        float multiplyAmount = 0,
        float source1Amount = 1,
        float source2Amount = 0,
        float offset = 0,
        bool clampOutput = true) : base(name)
    {
        _source1 = source1 ?? throw new ArgumentNullException(nameof(source1));
        _source2 = source2 ?? throw new ArgumentNullException(nameof(source2));
        _coefficients = [
            RequireFinite(multiplyAmount, nameof(multiplyAmount)),
            RequireFinite(source1Amount, nameof(source1Amount)),
            RequireFinite(source2Amount, nameof(source2Amount)),
            RequireFinite(offset, nameof(offset))];
        _clampOutput = clampOutput;
    }

    internal override Guid EffectId => ArithmeticCompositeId;
    internal override uint PropertyCount => 2;
    internal override uint SourceCount => 2;

    internal override object GetPropertyValue(uint index) => index switch
    {
        // D2D1_ARITHMETICCOMPOSITE_PROP_COEFFICIENTS is one VECTOR_4F property.
        0 => PropertyValue.CreateSingleArray(_coefficients),
        1 => PropertyValue.CreateBoolean(_clampOutput),
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    internal override IGraphicsEffectSource GetSource(uint index) => index switch
    {
        0 => _source1,
        1 => _source2,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    internal override bool TryGetPropertyMapping(string name, out uint index, out EffectInteropPropertyMapping mapping)
    {
        index = 0;
        mapping = name switch
        {
            "MultiplyAmount" => EffectInteropPropertyMapping.VectorX,
            "Source1Amount" => EffectInteropPropertyMapping.VectorY,
            "Source2Amount" => EffectInteropPropertyMapping.VectorZ,
            "Offset" => EffectInteropPropertyMapping.VectorW,
            "Coefficients" => EffectInteropPropertyMapping.Direct,
            "ClampOutput" => EffectInteropPropertyMapping.Direct,
            _ => EffectInteropPropertyMapping.Unknown
        };
        if (name == "ClampOutput") index = 1;
        return mapping != EffectInteropPropertyMapping.Unknown;
    }
}

/// <summary>Supported Gaussian effect, retained solely as an isotropic comparison.</summary>
[WinRT.WinRTExposedType(typeof(EffectInteropExposedTypeDetails))]
internal sealed class GaussianBlurEffectDescription : CompositionEffectDescription
{
    private static readonly Guid GaussianBlurId = new("1FEB6D69-2FE6-4AC9-8C58-1D7F93E7A6A5");
    private readonly IGraphicsEffectSource _source;
    private readonly float _standardDeviation;
    private readonly uint _optimization;
    private readonly uint _borderMode;

    internal GaussianBlurEffectDescription(
        string name,
        IGraphicsEffectSource source,
        float standardDeviation = 0,
        uint optimization = 0,
        uint borderMode = 1) : base(name)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _standardDeviation = RequireFinite(standardDeviation, nameof(standardDeviation));
        if (standardDeviation < 0) throw new ArgumentOutOfRangeException(nameof(standardDeviation));
        if (optimization > 2) throw new ArgumentOutOfRangeException(nameof(optimization));
        if (borderMode > 1) throw new ArgumentOutOfRangeException(nameof(borderMode));
        _optimization = optimization; // 0 = SPEED; 1 = BALANCED; 2 = QUALITY.
        _borderMode = borderMode; // 0 = SOFT; 1 = HARD.
    }

    internal override Guid EffectId => GaussianBlurId;
    internal override uint PropertyCount => 3;
    internal override uint SourceCount => 1;

    internal override object GetPropertyValue(uint index) => index switch
    {
        0 => PropertyValue.CreateSingle(_standardDeviation),
        1 => PropertyValue.CreateUInt32(_optimization),
        2 => PropertyValue.CreateUInt32(_borderMode),
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    internal override IGraphicsEffectSource GetSource(uint index) =>
        index == 0 ? _source : throw new ArgumentOutOfRangeException(nameof(index));

    internal override bool TryGetPropertyMapping(string name, out uint index, out EffectInteropPropertyMapping mapping)
    {
        index = name switch
        {
            "BlurAmount" or "StandardDeviation" => 0,
            "Optimization" => 1,
            "BorderMode" => 2,
            _ => uint.MaxValue
        };
        mapping = index == uint.MaxValue ? EffectInteropPropertyMapping.Unknown : EffectInteropPropertyMapping.Direct;
        return index != uint.MaxValue;
    }
}

// Exact native values from winrt/windows.graphics.effects.interop.h.
internal enum EffectInteropPropertyMapping : uint
{
    Unknown = 0,
    Direct = 1,
    VectorX = 2,
    VectorY = 3,
    VectorZ = 4,
    VectorW = 5
}

/// <summary>
/// Extends the CsWinRT CCW itself, preserving one IUnknown identity for the projected
/// WinRT interfaces and native IGraphicsEffectD2D1Interop. No classic CLR COM wrapper.
/// </summary>
internal sealed class EffectInteropExposedTypeDetails : WinRT.IWinRTExposedTypeDetails
{
    public EffectInteropExposedTypeDetails() { }

    public ComWrappers.ComInterfaceEntry[] GetExposedInterfaces() =>
    [
        new()
        {
            IID = ABI.Windows.Graphics.Effects.IGraphicsEffectMethods.IID,
            Vtable = ABI.Windows.Graphics.Effects.IGraphicsEffectMethods.AbiToProjectionVftablePtr
        },
        new()
        {
            IID = ABI.Windows.Graphics.Effects.IGraphicsEffectSourceMethods.IID,
            Vtable = ABI.Windows.Graphics.Effects.IGraphicsEffectSourceMethods.AbiToProjectionVftablePtr
        },
        new() { IID = EffectInteropVtable.InterfaceId, Vtable = EffectInteropVtable.Pointer }
    ];
}

/// <summary>
/// Native ABI: IUnknown, GetEffectId, GetNamedPropertyMapping, GetPropertyCount,
/// GetProperty(IPropertyValue**), GetSource(IGraphicsEffectSource**), GetSourceCount.
/// Returned interface pointers each transfer one reference to the native caller.
/// </summary>
internal static class EffectInteropVtable
{
    internal static readonly Guid InterfaceId = new("2FC57384-A068-44D7-A331-30982FCF7177");
    private static readonly Guid PropertyValueId = new("4BD682DD-7554-40E9-9A9B-82654EDE7E62");
    private const int Success = 0;
    private const int PointerError = unchecked((int)0x80004003);
    private const int InvalidArgument = unchecked((int)0x80070057);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetEffectIdCallback(IntPtr thisPtr, IntPtr id);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetNamedPropertyMappingCallback(IntPtr thisPtr, IntPtr name, IntPtr index, IntPtr mapping);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetCountCallback(IntPtr thisPtr, IntPtr count);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetItemCallback(IntPtr thisPtr, uint index, IntPtr item);

    // Root delegates for the full lifetime of their type-associated native vtable.
    private static readonly Delegate[] Callbacks =
    [
        new GetEffectIdCallback(GetEffectId),
        new GetNamedPropertyMappingCallback(GetNamedPropertyMapping),
        new GetCountCallback(GetPropertyCount),
        new GetItemCallback(GetProperty),
        new GetItemCallback(GetSource),
        new GetCountCallback(GetSourceCount)
    ];

    internal static readonly IntPtr Pointer = CreateVtable();

    private static IntPtr CreateVtable()
    {
        var pointer = RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(EffectInteropVtable), IntPtr.Size * 9);
        ComWrappers.GetIUnknownImpl(out var queryInterface, out var addRef, out var release);
        Marshal.WriteIntPtr(pointer, 0, queryInterface);
        Marshal.WriteIntPtr(pointer, IntPtr.Size, addRef);
        Marshal.WriteIntPtr(pointer, IntPtr.Size * 2, release);
        for (var index = 0; index < Callbacks.Length; index++)
            Marshal.WriteIntPtr(pointer, IntPtr.Size * (index + 3), Marshal.GetFunctionPointerForDelegate(Callbacks[index]));
        return pointer;
    }

    private static CompositionEffectDescription Find(IntPtr thisPtr) =>
        WinRT.ComWrappersSupport.FindObject<CompositionEffectDescription>(thisPtr);

    private static int GetEffectId(IntPtr thisPtr, IntPtr id)
    {
        if (id == IntPtr.Zero) return PointerError;
        try { Marshal.StructureToPtr(Find(thisPtr).EffectId, id, false); return Success; }
        catch (Exception error) { return Marshal.GetHRForException(error); }
    }

    private static int GetNamedPropertyMapping(IntPtr thisPtr, IntPtr name, IntPtr index, IntPtr mapping)
    {
        if (index == IntPtr.Zero || mapping == IntPtr.Zero) return PointerError;
        Marshal.WriteInt32(index, 0);
        Marshal.WriteInt32(mapping, 0);
        if (name == IntPtr.Zero) return InvalidArgument;
        try
        {
            // Native header uses LPCWSTR, not HSTRING.
            var propertyName = Marshal.PtrToStringUni(name)!;
            if (!Find(thisPtr).TryGetPropertyMapping(propertyName, out var propertyIndex, out var propertyMapping))
                return InvalidArgument;
            Marshal.WriteInt32(index, unchecked((int)propertyIndex));
            Marshal.WriteInt32(mapping, unchecked((int)propertyMapping));
            return Success;
        }
        catch (Exception error) { return Marshal.GetHRForException(error); }
    }

    private static int GetPropertyCount(IntPtr thisPtr, IntPtr count)
    {
        if (count == IntPtr.Zero) return PointerError;
        Marshal.WriteInt32(count, 0);
        try { Marshal.WriteInt32(count, unchecked((int)Find(thisPtr).PropertyCount)); return Success; }
        catch (Exception error) { return Marshal.GetHRForException(error); }
    }

    private static int GetProperty(IntPtr thisPtr, uint index, IntPtr value)
    {
        if (value == IntPtr.Zero) return PointerError;
        Marshal.WriteIntPtr(value, IntPtr.Zero);
        try
        {
            var effect = Find(thisPtr);
            if (index >= effect.PropertyCount) return InvalidArgument;
            var inspectable = WinRT.MarshalInspectable<object>.FromManaged(effect.GetPropertyValue(index));
            try
            {
                var propertyValueId = PropertyValueId;
                var result = Marshal.QueryInterface(inspectable, in propertyValueId, out var propertyValue);
                if (result < 0) return result;
                // QI AddRef is deliberately transferred to IPropertyValue**.
                Marshal.WriteIntPtr(value, propertyValue);
                return Success;
            }
            finally { WinRT.MarshalInspectable<object>.DisposeAbi(inspectable); }
        }
        catch (Exception error) { return Marshal.GetHRForException(error); }
    }

    private static int GetSource(IntPtr thisPtr, uint index, IntPtr source)
    {
        if (source == IntPtr.Zero) return PointerError;
        Marshal.WriteIntPtr(source, IntPtr.Zero);
        try
        {
            var effect = Find(thisPtr);
            if (index >= effect.SourceCount) return InvalidArgument;
            // FromManaged returns the requested interface, with one owned reference.
            Marshal.WriteIntPtr(source, WinRT.MarshalInterface<IGraphicsEffectSource>.FromManaged(effect.GetSource(index)));
            return Success;
        }
        catch (Exception error) { return Marshal.GetHRForException(error); }
    }

    private static int GetSourceCount(IntPtr thisPtr, IntPtr count)
    {
        if (count == IntPtr.Zero) return PointerError;
        Marshal.WriteInt32(count, 0);
        try { Marshal.WriteInt32(count, unchecked((int)Find(thisPtr).SourceCount)); return Success; }
        catch (Exception error) { return Marshal.GetHRForException(error); }
    }
}
