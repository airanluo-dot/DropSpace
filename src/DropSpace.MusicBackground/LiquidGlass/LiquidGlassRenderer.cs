using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Lyricify.Backgrounds.AppleMusicInspired.Ios.WinUI;
using Microsoft.UI.Xaml.Controls;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace DropSpace.Rendering;

/// <summary>UI-thread-owned GPU crop, separable blur and refractive material. No CPU screen readback.</summary>
internal sealed class LiquidGlassRenderer : IDisposable
{
    private readonly List<IDisposable> _owned = [];
    internal ID3D11Device Device { get; private set; } = null!;
    private ID3D11DeviceContext _context = null!;
    private SwapChainPanelPresenter _presenter = null!;
    private ID3D11VertexShader _vertex = null!;
    private ID3D11PixelShader _blur = null!, _glass = null!;
    private ID3D11Buffer _constants = null!;
    private ID3D11SamplerState _sampler = null!;
    private Texture? _crop, _horizontal, _vertical, _output;
    private bool _disposed;
    internal long PresentedFrames { get; private set; }
    internal GlassRefreshMeasurement? Measurement { get; set; }
    [StructLayout(LayoutKind.Sequential)] private struct Constants { internal Vector4 Size, Shape, Optics, Crop, Lens; }

    internal LiquidGlassRenderer(SwapChainPanel panel)
    {
        try
        {
            D3D11.D3D11CreateDevice(nint.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_0], out ID3D11Device? created).CheckError();
            var device = created ?? throw new InvalidOperationException("D3D11 device creation returned no device.");
            Device = Own(device); _context = Own(device.ImmediateContext);
            _presenter = Own(new SwapChainPanelPresenter(panel, alphaMode: AlphaMode.Premultiplied));
            _presenter.Initialize(device);
            using var stream = typeof(LiquidGlassRenderer).Assembly.GetManifestResourceStream("DropSpace.LiquidGlass.hlsl")!;
            using var reader = new StreamReader(stream);
            var shader = reader.ReadToEnd();
            _vertex = Own(device.CreateVertexShader(Compile(shader, "VS", "vs_5_0")));
            _blur = Own(device.CreatePixelShader(Compile(shader, "Blur", "ps_5_0")));
            _glass = Own(device.CreatePixelShader(Compile(shader, "Glass", "ps_5_0")));
            _constants = Own(device.CreateBuffer(Marshal.SizeOf<Constants>(), BindFlags.ConstantBuffer,
                ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.None, 0));
            _sampler = Own(device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear,
                TextureAddressMode.Clamp, 0, 1, ComparisonFunction.Never, 0, float.MaxValue)));
        }
        catch { Dispose(); throw; }
    }

    private T Own<T>(T value) where T : IDisposable { _owned.Add(value); return value; }
    internal void CopyFrame(ID3D11Texture2D target, ID3D11Texture2D source) => _context.CopyResource(target, source);

    internal void Render(ID3D11Texture2D frame, int x, int y, GlassFrame shape, GlassOptions options,
        float panelScaleX, float panelScaleY)
    {
        var desc = frame.Description;
        var padding = (int)Math.Ceiling((options.Blur * 3 + 64) * shape.Dpi);
        var left = Math.Clamp(x - padding, 0, desc.Width - 1);
        var top = Math.Clamp(y - padding, 0, desc.Height - 1);
        var right = Math.Clamp(x + shape.Width + padding, left + 1, desc.Width);
        var bottom = Math.Clamp(y + shape.Height + padding, top + 1, desc.Height);
        var w = right - left; var h = bottom - top;
        Ensure(ref _crop, w, h); Ensure(ref _horizontal, w, h); Ensure(ref _vertical, w, h);
        Ensure(ref _output, shape.Width, shape.Height);
        _context.CopySubresourceRegion(_crop!.Image, 0, 0, 0, 0, frame, 0,
            new Vortice.Mathematics.Box(left, top, 0, right, bottom, 1));
        Measurement?.CopiedRegion(w, h);
        var constants = new Constants
        {
            Size = new(shape.Width, shape.Height, w, h),
            Shape = new(shape.TopRadius, shape.BottomRadius, shape.Dpi, options.Blur * shape.Dpi),
            Optics = new(options.Refraction / 100, options.Dispersion / 100, options.DarkOverlay / 100, options.BlackGradient ? 1 : 0),
            Crop = new(x - left, y - top, 1, 0),
            Lens = new(1 + (options.RefractionWidth / 100 - 1) * Math.Clamp(shape.ExpandedProgress, 0, 1), 0, 0, 0),
        };
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_vertex); _context.PSSetConstantBuffer(0, _constants); _context.PSSetSampler(0, _sampler);
        if (options.Blur > 0)
        {
            Pass(_crop, _horizontal!, _blur, constants);
            constants.Crop.Z = 0; constants.Crop.W = 1;
            Pass(_horizontal!, _vertical!, _blur, constants);
        }
        Pass(options.Blur > 0 ? _vertical! : _crop, _output!, _glass, constants);
        _context.OMSetRenderTargets(0, Array.Empty<ID3D11RenderTargetView>(), null);
        _presenter.EnsureSize(shape.Width, shape.Height, panelScaleX, panelScaleY);
        var measuredAt = Measurement is null ? 0 : System.Diagnostics.Stopwatch.GetTimestamp();
        _presenter.Present(_context, _output!.Image);
        if (measuredAt != 0) Measurement?.Present(measuredAt);
        Device.DeviceRemovedReason.CheckError();
        PresentedFrames++;
    }

    private void Pass(Texture source, Texture target, ID3D11PixelShader shader, Constants constants)
    {
        _context.UpdateSubresource(in constants, _constants);
        _context.OMSetRenderTargets(1, [target.Target], null);
        _context.RSSetViewport(0, 0, target.Width, target.Height);
        _context.PSSetShader(shader); _context.PSSetShaderResource(0, source.Source);
        _context.Draw(3, 0);
        _context.PSSetShaderResources(0, 1, new ID3D11ShaderResourceView[] { null! });
    }

    // Explicit diagnostic only. Compare the old full-copy path with direct region
    // sampling of exactly the same live source, including the final premultiplied alpha.
    internal object CompareSampling(ID3D11Texture2D frame, int x, int y, GlassFrame shape, GlassOptions options,
        float panelScaleX, float panelScaleY, bool compareWidth = false)
    {
        var description = frame.Description;
        description.BindFlags = BindFlags.None; description.MiscFlags = ResourceOptionFlags.None;
        using var fullCopy = Device.CreateTexture2D(description);
        CopyFrame(fullCopy, frame);
        Render(fullCopy, x, y, shape, compareWidth ? options with { RefractionWidth = 100 } : options, panelScaleX, panelScaleY);
        var reference = ReadDiagnosticOutput();
        Render(frame, x, y, shape, options, panelScaleX, panelScaleY);
        var direct = ReadDiagnosticOutput();
        var changedPixels = 0;
        for (var offset = 0; offset < direct.Length; offset += 4)
            if (!reference.AsSpan(offset, 4).SequenceEqual(direct.AsSpan(offset, 4))) changedPixels++;
        return new { identical = changedPixels == 0, changedPixels, comparedBytes = direct.Length,
            width = shape.Width, height = shape.Height, shape.ExpandedProgress,
            referenceWidth = compareWidth ? 100 : options.RefractionWidth, options };
    }

    private byte[] ReadDiagnosticOutput()
    {
        var description = _output!.Image.Description;
        description.Usage = ResourceUsage.Staging; description.BindFlags = BindFlags.None;
        description.CPUAccessFlags = CpuAccessFlags.Read;
        using var staging = Device.CreateTexture2D(description);
        _context.CopyResource(staging, _output.Image);
        _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None, out var mapped).CheckError();
        try
        {
            var stride = description.Width * 4;
            var bytes = new byte[stride * description.Height];
            for (var row = 0; row < description.Height; row++)
                Marshal.Copy(mapped.DataPointer + row * mapped.RowPitch, bytes, row * stride, stride);
            return bytes;
        }
        finally { _context.Unmap(staging, 0); }
    }

    private void Ensure(ref Texture? texture, int width, int height)
    {
        if (texture?.Width == width && texture.Height == height) return;
        var next = new Texture(Device, width, height);
        texture?.Dispose(); texture = next;
    }

    private sealed class Texture : IDisposable
    {
        internal ID3D11Texture2D Image { get; }
        internal ID3D11RenderTargetView Target { get; } = null!;
        internal ID3D11ShaderResourceView Source { get; } = null!;
        internal int Width { get; }
        internal int Height { get; }
        internal Texture(ID3D11Device device, int width, int height)
        {
            Width = width; Height = height;
            Image = device.CreateTexture2D(new Texture2DDescription
            {
                Width = width, Height = height, MipLevels = 1, ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm, SampleDescription = new(1, 0),
                Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            });
            try { Target = device.CreateRenderTargetView(Image); Source = device.CreateShaderResourceView(Image); }
            catch { Dispose(); throw; }
        }
        public void Dispose() { Source?.Dispose(); Target?.Dispose(); Image.Dispose(); }
    }

    private static byte[] Compile(string source, string entry, string target)
    {
        var bytes = Encoding.UTF8.GetBytes(source);
        var result = D3DCompile(bytes, (nuint)bytes.Length, "LiquidGlass.hlsl", 0, 0, entry, target,
            0x800 | 0x8000, 0, out var code, out var errors);
        try
        {
            if (result < 0) throw new InvalidOperationException(errors != 0
                ? Encoding.UTF8.GetString(CopyBlob(errors)).TrimEnd('\0') : $"Glass shader compilation failed: 0x{result:X8}");
            return CopyBlob(code);
        }
        finally { if (errors != 0) Marshal.Release(errors); if (code != 0) Marshal.Release(code); }
    }
    private static unsafe byte[] CopyBlob(nint blob)
    {
        var vtable = *(nint**)blob;
        var pointer = ((delegate* unmanaged[Stdcall]<nint, nint>)vtable[3])(blob);
        var length = ((delegate* unmanaged[Stdcall]<nint, nuint>)vtable[4])(blob);
        var bytes = new byte[checked((int)length)]; Marshal.Copy(pointer, bytes, 0, bytes.Length); return bytes;
    }
    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi)] private static extern int D3DCompile(
        byte[] source, nuint length, string name, nint defines, nint include, string entry, string target,
        uint flags, uint flags2, out nint code, out nint errors);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _context?.ClearState();
        _output?.Dispose(); _vertical?.Dispose(); _horizontal?.Dispose(); _crop?.Dispose();
        _output = _vertical = _horizontal = _crop = null;
        for (var i = _owned.Count - 1; i >= 0; i--) _owned[i].Dispose();
        _owned.Clear();
    }
}
