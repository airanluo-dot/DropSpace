using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace DropSpace.App.Services.Diagnostics;

internal static partial class BetaUiDiagnostics
{
    // This is supplementary desktop evidence, never a replacement for the existing
    // control-readback oracle. In particular, GDI's unused alpha byte is not opacity.
    private static async Task CaptureDesktopAsync(OverlayWindow overlay, FrameworkElement? popup,
        string name, BetaUiDiagnosticReceipt receipt, CancellationToken token)
    {
        var file = name + "-desktop.png";
        var stage = "prepare";
        BetaUiDesktopSnapshot? before = null;
        BetaUiDesktopSnapshot? after = null;
        var boundsProbe = new BetaUiDesktopBoundsProbe();
        var boundsAfterProbe = new BetaUiDesktopBoundsProbe();
        var watch = Stopwatch.StartNew();
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(overlay);
            boundsProbe.Source = boundsAfterProbe.Source = popup is null ? "overlay-hwnd-client-rect-to-screen" : "real-popup-automation-peer-screen-rectangle";
            var peer = popup is null ? null : FrameworkElementAutomationPeer.CreatePeerForElement(popup)
                ?? throw new DesktopCaptureBlockedException("The real popup has no automation peer; no substitute bounds were used.");
            boundsProbe.PeerType = boundsAfterProbe.PeerType = peer?.GetType().FullName;
            stage = "validate-before";
            before = ObserveDesktopTarget(hwnd, peer, boundsProbe);
            ValidateDesktopTarget(before, popup is not null);
            receipt.Observations.Add(new { stage = "supplemental-" + name + "-before", state = before });

            stage = "desktop-bitblt";
            // The worker owns every GDI resource, including after a caller timeout.
            // A synchronous native call cannot be forcibly interrupted in-process.
            // The launcher supplies the ultimate process deadline in that case.
            var nativeRead = Task.Run(() => ReadDesktopPixels(before.Bounds), CancellationToken.None);
            byte[] pixels;
            try { pixels = await nativeRead.WaitAsync(TimeSpan.FromSeconds(5), deadline.Token); }
            catch
            {
                // Observe a late fault without touching receipt/UI state after timeout.
                _ = nativeRead.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw;
            }
            stage = "validate-after";
            after = ObserveDesktopTarget(hwnd, peer, boundsAfterProbe);
            ValidateDesktopTarget(after, popup is not null);
            if (before.Bounds != after.Bounds || before.OwnedHwnd != after.OwnedHwnd)
                throw new DesktopCaptureBlockedException("The target bounds or sampled center HWND changed during readback.");

            var sourceHash = Convert.ToHexString(SHA256.HashData(pixels)).ToLowerInvariant();
            // BI_RGB delivers BGRX. Preserve its raw hash before writing an explicit
            // opaque alpha channel solely to encode the desktop RGB values as PNG.
            for (var index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
            stage = "encode-png";
            using var encoded = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, encoded)
                .AsTask().WaitAsync(deadline.Token);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
                (uint)before.Bounds.Width, (uint)before.Bounds.Height, before.Dpi, before.Dpi, pixels);
            await encoder.FlushAsync().AsTask().WaitAsync(deadline.Token);
            using var reader = new DataReader(encoded.GetInputStreamAt(0));
            await reader.LoadAsync(checked((uint)encoded.Size)).AsTask().WaitAsync(deadline.Token);
            var png = new byte[checked((int)encoded.Size)];
            reader.ReadBytes(png);
            stage = "write-png";
            await File.WriteAllBytesAsync(Path.Combine(receipt.Output, file), png, deadline.Token);
            receipt.SupplementalCaptures.Add(new(file,
                Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant(), before.Bounds.Width,
                before.Bounds.Height, sourceHash, before.Bounds, after.Bounds, before.OverlayHwnd,
                before.OwnedHwnd, before, after,
                new { processId = Environment.ProcessId, before.Dpi, before.VirtualScreen,
                    boundsSource = popup is null ? "overlay-hwnd-client-rect-to-screen" : "real-popup-automation-peer-screen-rectangle",
                    peerType = peer?.GetType().FullName, elapsedMilliseconds = watch.ElapsedMilliseconds }));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            receipt.SupplementalCaptureFailures.Add(new(file,
                error is DesktopCaptureBlockedException ? "blocked" : "failed", stage, Describe(error),
                new { boundsProbe, boundsAfterProbe, before, after, elapsedMilliseconds = watch.ElapsedMilliseconds }));
        }
        // Evidence I/O must not turn an optional desktop attachment into a scenario
        // failure or change the primary stage/status recorded by CaptureAsync.
        try { WriteEvidence(receipt); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            receipt.SupplementalCaptureFailures.Add(new(file, "failed", "write-evidence", Describe(error), null));
        }
    }

    private static BetaUiDesktopSnapshot ObserveDesktopTarget(nint overlay, AutomationPeer? popupPeer,
        BetaUiDesktopBoundsProbe probe)
    {
        var virtualScreen = new BetaUiDesktopBounds(GetSystemMetrics(76), GetSystemMetrics(77),
            GetSystemMetrics(78), GetSystemMetrics(79));
        probe.VirtualScreen = virtualScreen;
        DesktopNative.GetWindowThreadProcessId(overlay, out var processId);
        var cloakResult = DesktopNative.DwmGetWindowAttribute(overlay, 14, out var cloaked, sizeof(uint));
        var bounds = ReadDesktopBounds(overlay, popupPeer, probe);
        probe.Bounds = bounds;
        ValidateDesktopBounds(bounds, virtualScreen);
        var samples = new List<BetaUiDesktopSample>();
        // Quarter, center and three-quarter points avoid sampling popup shadows.
        for (var row = 1; row <= 3; row++)
        for (var column = 1; column <= 3; column++)
        {
            var point = new DesktopNative.Point
            {
                X = bounds.X + bounds.Width * column / 4,
                Y = bounds.Y + bounds.Height * row / 4,
            };
            var hit = DesktopNative.WindowFromPoint(point);
            DesktopNative.GetWindowThreadProcessId(hit, out var hitProcessId);
            var root = DesktopNative.GetAncestor(hit, 2);
            var rootOwner = DesktopNative.GetAncestor(hit, 3);
            DesktopNative.GetWindowThreadProcessId(root, out var rootProcessId);
            var rootVisible = DesktopNative.IsWindowVisible(root);
            var rootIconic = DesktopNative.IsIconic(root);
            var rootCloakResult = DesktopNative.DwmGetWindowAttribute(root, 14, out var rootCloaked, sizeof(uint));
            var owners = new List<string>();
            var current = root;
            var linked = false;
            // Owner chains are bounded and cycle checked, with no class-name guess.
            var seen = new HashSet<nint>();
            for (var count = 0; current != 0 && count < 16 && seen.Add(current); count++)
            {
                owners.Add(Hwnd(current));
                if (current == overlay) linked = true;
                current = DesktopNative.GetWindow(current, 4);
            }
            samples.Add(new(point.X, point.Y, Hwnd(hit), hitProcessId, Hwnd(root), rootProcessId,
                Hwnd(rootOwner), owners.ToArray(), rootVisible, rootIconic, rootCloakResult, rootCloaked,
                hitProcessId == Environment.ProcessId && rootProcessId == Environment.ProcessId && linked &&
                rootVisible && !rootIconic && rootCloakResult == 0 && rootCloaked == 0));
        }
        return new(bounds, virtualScreen, Hwnd(overlay), samples[4].Hwnd, processId,
            DesktopNative.IsWindow(overlay), DesktopNative.IsWindowVisible(overlay), DesktopNative.IsIconic(overlay),
            cloakResult, cloaked, DesktopNative.GetDpiForWindow(overlay), samples.ToArray());
    }

    private static BetaUiDesktopBounds ReadDesktopBounds(nint overlay, AutomationPeer? popupPeer,
        BetaUiDesktopBoundsProbe probe)
    {
        if (popupPeer is not null)
        {
            var rectangle = popupPeer.GetBoundingRectangle();
            // Persist the actual peer values before conversion/validation can fail.
            // Nonfinite values remain explicit strings, which JSON can safely encode.
            probe.RawBounds = RawScreenBounds(rectangle);
            if (!double.IsFinite(rectangle.X) || !double.IsFinite(rectangle.Y) ||
                !double.IsFinite(rectangle.Width) || !double.IsFinite(rectangle.Height) ||
                rectangle.Width <= 0 || rectangle.Height <= 0)
                throw new DesktopCaptureBlockedException("The real popup peer did not provide a nonempty finite screen rectangle.");
            var left = checked((int)Math.Floor(rectangle.X));
            var top = checked((int)Math.Floor(rectangle.Y));
            return new(left, top, checked((int)Math.Ceiling(rectangle.Right) - left),
                checked((int)Math.Ceiling(rectangle.Bottom) - top));
        }
        if (!DesktopNative.GetClientRect(overlay, out var client)) throw DesktopError("GetClientRect");
        var origin = new DesktopNative.Point { X = client.Left, Y = client.Top };
        if (!DesktopNative.ClientToScreen(overlay, ref origin)) throw DesktopError("ClientToScreen");
        probe.RawBounds = new { x = origin.X, y = origin.Y, width = (long)client.Right - client.Left,
            height = (long)client.Bottom - client.Top };
        return new(origin.X, origin.Y, checked(client.Right - client.Left), checked(client.Bottom - client.Top));
    }

    private static object RawScreenBounds(Windows.Foundation.Rect rectangle)
    {
        static object Coordinate(double value) => double.IsFinite(value) ? value : value.ToString("R", CultureInfo.InvariantCulture);
        return new { x = Coordinate(rectangle.X), y = Coordinate(rectangle.Y),
            width = Coordinate(rectangle.Width), height = Coordinate(rectangle.Height) };
    }

    private static void ValidateDesktopBounds(BetaUiDesktopBounds bounds, BetaUiDesktopBounds screen)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || bounds.Width > 4096 || bounds.Height > 4096 ||
            (long)bounds.Width * bounds.Height > 4_000_000)
            throw new DesktopCaptureBlockedException("Desktop ROI is empty or exceeds the 4096-axis / 4-million-pixel limit.");
        if (screen.Width <= 0 || screen.Height <= 0 || bounds.X < screen.X || bounds.Y < screen.Y ||
            (long)bounds.X + bounds.Width > (long)screen.X + screen.Width ||
            (long)bounds.Y + bounds.Height > (long)screen.Y + screen.Height)
            throw new DesktopCaptureBlockedException("The complete target ROI is not within the virtual screen; no substitute crop was used.");
    }

    private static void ValidateDesktopTarget(BetaUiDesktopSnapshot snapshot, bool popup)
    {
        if (!snapshot.IsWindow || !snapshot.IsVisible || snapshot.IsIconic ||
            snapshot.ProcessId != Environment.ProcessId || snapshot.CloakResult != 0 || snapshot.Cloaked != 0 || snapshot.Dpi == 0)
            throw new DesktopCaptureBlockedException("The Overlay HWND is not a visible, uncloaked, current-process window with known DPI.");
        if (!snapshot.Samples[4].OwnedByOverlay || (popup && snapshot.Samples.Any(sample => !sample.OwnedByOverlay)))
            throw new DesktopCaptureBlockedException("WindowFromPoint could not prove the ROI center (and every popup sample) belongs to this Overlay owner chain.");
    }

    private static byte[] ReadDesktopPixels(BetaUiDesktopBounds bounds)
    {
        nint desktopDc = 0, memoryDc = 0, bitmap = 0, previous = 0;
        var cleanupFailures = new List<string>();
        byte[]? pixels = null;
        Exception? readFailure = null;
        try
        {
            Marshal.ThrowExceptionForHR(DesktopNative.DwmFlush());
            desktopDc = DesktopNative.GetDC(0);
            if (desktopDc == 0) throw DesktopError("GetDC(desktop)");
            memoryDc = DesktopNative.CreateCompatibleDC(desktopDc);
            if (memoryDc == 0) throw DesktopError("CreateCompatibleDC");
            var info = new DesktopNative.BitmapInfo
            {
                Header = new DesktopNative.BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<DesktopNative.BitmapInfoHeader>(), Width = bounds.Width,
                    Height = -bounds.Height, Planes = 1, BitCount = 32, Compression = 0,
                    SizeImage = checked((uint)(bounds.Width * bounds.Height * 4)),
                },
            };
            bitmap = DesktopNative.CreateDIBSection(memoryDc, ref info, 0, out var bits, 0, 0);
            if (bitmap == 0 || bits == 0) throw DesktopError("CreateDIBSection");
            previous = DesktopNative.SelectObject(memoryDc, bitmap);
            if (previous == 0 || previous == -1) throw DesktopError("SelectObject");
            if (!DesktopNative.BitBlt(memoryDc, 0, 0, bounds.Width, bounds.Height, desktopDc,
                    bounds.X, bounds.Y, 0x00CC0020 | 0x40000000)) throw DesktopError("BitBlt(SRCCOPY|CAPTUREBLT)");
            if (!DesktopNative.GdiFlush()) throw DesktopError("GdiFlush");
            pixels = new byte[checked(bounds.Width * bounds.Height * 4)];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
        }
        catch (Exception error) { readFailure = error; }
        finally
        {
            if (previous != 0 && previous != -1)
            {
                var restored = DesktopNative.SelectObject(memoryDc, previous);
                if (restored == 0 || restored == -1) cleanupFailures.Add("SelectObject(restore)");
            }
            if (memoryDc != 0 && !DesktopNative.DeleteDC(memoryDc)) cleanupFailures.Add("DeleteDC");
            if (bitmap != 0 && !DesktopNative.DeleteObject(bitmap)) cleanupFailures.Add("DeleteObject(bitmap)");
            if (desktopDc != 0 && DesktopNative.ReleaseDC(0, desktopDc) == 0) cleanupFailures.Add("ReleaseDC(desktop)");
        }
        if (cleanupFailures.Count != 0)
        {
            var cleanupFailure = new InvalidOperationException("Desktop capture resource cleanup failed: " + string.Join(", ", cleanupFailures));
            throw readFailure is null ? cleanupFailure : new AggregateException(readFailure, cleanupFailure);
        }
        if (readFailure is not null) ExceptionDispatchInfo.Capture(readFailure).Throw();
        return pixels ?? throw new InvalidOperationException("Desktop readback returned no pixels.");
    }

    private static string Hwnd(nint hwnd) => "0x" + hwnd.ToString("X");
    private static Win32Exception DesktopError(string operation) => new(Marshal.GetLastWin32Error(), operation + " failed.");
    private sealed class DesktopCaptureBlockedException(string message) : Exception(message);

    private static class DesktopNative
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] internal struct BitmapInfoHeader
        {
            public uint Size; public int Width, Height; public ushort Planes, BitCount;
            public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter;
            public uint ColorsUsed, ColorsImportant;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct BitmapInfo { public BitmapInfoHeader Header; public uint Color; }
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint hwnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint hwnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsIconic(nint hwnd);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
        [DllImport("user32.dll")] internal static extern uint GetDpiForWindow(nint hwnd);
        [DllImport("user32.dll")] internal static extern nint WindowFromPoint(Point point);
        [DllImport("user32.dll")] internal static extern nint GetAncestor(nint hwnd, uint flags);
        [DllImport("user32.dll")] internal static extern nint GetWindow(nint hwnd, uint command);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetClientRect(nint hwnd, out Rect rect);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ClientToScreen(nint hwnd, ref Point point);
        [DllImport("user32.dll", SetLastError = true)] internal static extern nint GetDC(nint hwnd);
        [DllImport("user32.dll")] internal static extern int ReleaseDC(nint hwnd, nint dc);
        [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint hwnd, uint attribute, out uint value, int size);
        [DllImport("dwmapi.dll")] internal static extern int DwmFlush();
        [DllImport("gdi32.dll", SetLastError = true)] internal static extern nint CreateCompatibleDC(nint dc);
        [DllImport("gdi32.dll", SetLastError = true)] internal static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
        [DllImport("gdi32.dll", SetLastError = true)] internal static extern nint SelectObject(nint dc, nint value);
        [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool BitBlt(nint destination, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
        [DllImport("gdi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GdiFlush();
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteDC(nint dc);
        [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeleteObject(nint value);
    }
}

internal sealed record BetaUiDesktopBounds(int X, int Y, int Width, int Height);
internal sealed class BetaUiDesktopBoundsProbe
{
    public string? Source { get; set; }
    public string? PeerType { get; set; }
    public object? RawBounds { get; set; }
    public BetaUiDesktopBounds? Bounds { get; set; }
    public BetaUiDesktopBounds? VirtualScreen { get; set; }
}
internal sealed record BetaUiDesktopSample(int X, int Y, string Hwnd, uint ProcessId, string RootHwnd,
    uint RootProcessId, string RootOwnerHwnd, string[] OwnerChain, bool RootIsVisible, bool RootIsIconic,
    int RootCloakResult, uint RootCloaked, bool OwnedByOverlay);
internal sealed record BetaUiDesktopSnapshot(BetaUiDesktopBounds Bounds, BetaUiDesktopBounds VirtualScreen,
    string OverlayHwnd, string OwnedHwnd, uint ProcessId, bool IsWindow, bool IsVisible, bool IsIconic,
    int CloakResult, uint Cloaked, uint Dpi, BetaUiDesktopSample[] Samples);
internal sealed record BetaUiSupplementalFailure(string File, string Status, string Stage, string Reason, object? Details);
internal sealed record BetaUiSupplementalCapture(string File, string Sha256, int PixelWidth, int PixelHeight,
    string SourcePixelSha256, BetaUiDesktopBounds Bounds, BetaUiDesktopBounds BoundsAfter, string OverlayHwnd,
    string OwnedHwnd, BetaUiDesktopSnapshot OwnershipBefore, BetaUiDesktopSnapshot OwnershipAfter, object Environment)
{
    public string Status => "captured-for-manual-review";
    public string CaptureKind => "desktop-bitblt-roi";
    public string SourceApi => "DwmFlush/GetDC(0)/CreateCompatibleDC/CreateDIBSection/BitBlt(SRCCOPY|CAPTUREBLT)/GdiFlush";
    public string SourcePixelFormat => "BGRX32-top-down";
    public bool SynthesizedOpaqueAlpha => true;
    public string[] Limitations => [
        "Supplementary manual-review attachment only; does not affect RenderTargetBitmap captures, visual status or scenario results.",
        "PNG alpha is set to 255 to encode GDI desktop RGB. It is not native transparency evidence; sourcePixelSha256 covers the unmodified BGRX bytes.",
        "Point samples prove only sampled HWND ownership before/after capture, not per-pixel identity or complete freedom from occlusion. Transparent client margins may show the desktop.",
        "GDI desktop readback may omit hardware-composed planes; an empty or missing-content attachment alone cannot establish that the physical screen did not show that content.",
        "Async waits use a 5-second native-read timeout and a linked 10-second budget. Synchronous OS/UI calls cannot be forcibly interrupted; a blocked read releases worker-owned handles when it returns, and the launcher process deadline is the ultimate bound.",
        "No physical pointer input, multi-monitor coverage, screenshot baseline comparison or complete visual correctness claim.",
    ];
}
