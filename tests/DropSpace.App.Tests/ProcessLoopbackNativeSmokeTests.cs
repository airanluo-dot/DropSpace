using System.Runtime.InteropServices;
using DropSpace.App.Services.Audio;
using DropSpace.Core.Media;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class ProcessLoopbackNativeSmokeTests
{
    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(nint sound, nint module, uint flags);

    [TestMethod]
    [TestCategory("NativeSmoke")]
    public async Task CapturesRealProcessPcmAndStopsOnPause()
    {
        await using var endpoint = new DropSpace.App.Services.Volume.WindowsVolumeActivityService(NullLogger<DropSpace.App.Services.Volume.WindowsVolumeActivityService>.Instance);
        await endpoint.SetEnabledAsync(true);
        if (!endpoint.IsAvailable) Assert.Inconclusive("No real render endpoint is available. PCM validation requires a Windows desktop with audio output.");
        // Deliberately quiet, short PCM rendered through WinMM in this process.
        // The capture must discover it through WASAPI, not through an injected sample source.
        var sound = CreateTone();
        var pinned = GCHandle.Alloc(sound, GCHandleType.Pinned);
        await using var capture = new WindowsProcessLoopbackService(NullLogger<WindowsProcessLoopbackService>.Instance);
        var observed = new TaskCompletionSource<SpectrumFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        capture.Changed += (_, frame) =>
        {
            if (frame.CaptureMode == AudioCaptureMode.ProcessLoopback && frame.Bands.Max() > 0.1 && frame.Bands.Max() - frame.Bands.Min() > 0.1)
                observed.TrySetResult(frame);
        };
        try
        {
            await capture.SetSourceAsync((uint)Environment.ProcessId, true);
            Assert.AreEqual(AudioCaptureMode.ProcessLoopback, capture.Current.CaptureMode);
            Assert.IsTrue(PlaySound(pinned.AddrOfPinnedObject(), 0, 0x0004 | 0x0001 | 0x0008 | 0x0002));
            var frame = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(6, frame.Bands.Count);
            await capture.SetSourceAsync((uint)Environment.ProcessId, false);
            Assert.AreEqual(AudioCaptureMode.Stopped, capture.Current.CaptureMode);
            await capture.SetSourceAsync((uint)Environment.ProcessId, true);
            Assert.AreEqual(AudioCaptureMode.ProcessLoopback, capture.Current.CaptureMode);
            await capture.SetSourceAsync(null, false);
        }
        finally { PlaySound(0, 0, 0); pinned.Free(); }
    }

    private static byte[] CreateTone()
    {
        const int rate = 44_100;
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);
        writer.Write("RIFF"u8); writer.Write(36 + rate * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(rate);
        writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(rate * 2);
        for (var index = 0; index < rate; index++) writer.Write((short)(700 * Math.Sin(2 * Math.PI * 440 * index / rate)));
        return buffer.ToArray();
    }
}
