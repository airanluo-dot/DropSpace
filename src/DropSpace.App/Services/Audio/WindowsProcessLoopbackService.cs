using System.Runtime.InteropServices;
using DropSpace.Core.Media;
using Microsoft.Extensions.Logging;
using static DropSpace.App.Services.Audio.ProcessLoopbackInterop;

namespace DropSpace.App.Services.Audio;

public sealed class WindowsProcessLoopbackService(ILogger<WindowsProcessLoopbackService> logger) : IAsyncDisposable
{
    private const int SampleRate = 44_100;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _stop;
    private Task _worker = Task.CompletedTask;
    private uint? _processId;
    private bool _disposed;
    public event EventHandler<SpectrumFrame>? Changed;
    public SpectrumFrame Current { get; private set; } = SpectrumFrame.Empty;
    public string FailureCategory { get; private set; } = string.Empty;

    public async Task SetSourceAsync(uint? processId, bool playing, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var target = playing ? processId : null;
            if (target == _processId && !_worker.IsCompleted) return;
            await StopCoreAsync().ConfigureAwait(false);
            if (target is null or 0)
            {
                Publish(new(playing ? AudioCaptureMode.Unavailable : AudioCaptureMode.Stopped, new double[6], Current.Revision + 1));
                return;
            }
            _processId = target;
            _stop = new();
            var token = _stop.Token;
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _worker = Task.Factory.StartNew(() => Capture(target.Value, token, ready), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            try { await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch { await StopCoreAsync().ConfigureAwait(false); throw; }
        }
        finally { _gate.Release(); }
    }

    private void Capture(uint processId, CancellationToken token, TaskCompletionSource ready)
    {
        IAudioClient? audio = null;
        IAudioCaptureClient? capture = null;
        IActivateAudioInterfaceAsyncOperation? operation = null;
        var initialized = CoInitializeEx(0, 0) >= 0;
        var started = false;
        using var available = new AutoResetEvent(false);
        var parameters = Marshal.AllocHGlobal(Marshal.SizeOf<ActivationParameters>());
        var callback = new ActivationCallback(parameters);
        var activationSubmitted = false;
        try
        {
            if (!initialized) throw new COMException("Audio apartment initialization failed.");
            Marshal.StructureToPtr(new ActivationParameters { Type = 1, ProcessId = processId, Mode = 0 }, parameters, false);
            var variant = new PropVariant { Type = 65, Size = (uint)Marshal.SizeOf<ActivationParameters>(), Data = parameters };
            Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync("VAD\\Process_Loopback", typeof(IAudioClient).GUID, variant, callback, out operation));
            activationSubmitted = true;
            // This bounded wait is on the dedicated MTA capture thread, never the UI thread.
            audio = (IAudioClient)callback.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5), token).GetAwaiter().GetResult();
            callback.TransferOwnership();
            var format = new WaveFormat { FormatTag = 1, Channels = 2, SamplesPerSecond = SampleRate, BitsPerSample = 16, BlockAlign = 4, BytesPerSecond = SampleRate * 4 };
            audio.Initialize(0, 0x00020000 | 0x00040000 | 0x80000000, 0, 0, format, 0);
            audio.GetService(typeof(IAudioCaptureClient).GUID, out var captureObject);
            capture = (IAudioCaptureClient)captureObject;
            audio.SetEventHandle(available.SafeWaitHandle.DangerousGetHandle());
            audio.Start();
            started = true;
            FailureCategory = string.Empty;
            Publish(new(AudioCaptureMode.ProcessLoopback, new double[6], Current.Revision + 1));
            ready.TrySetResult();
            var analyzer = new SpectrumAnalyzer(SampleRate);
            var samples = new short[SampleRate * 2];
            WaitHandle[] events = [token.WaitHandle, available];
            while (WaitHandle.WaitAny(events) == 1)
            {
                capture.GetNextPacketSize(out var next);
                // Bound each drain to protect stop responsiveness even under continuous input.
                for (var packets = 0; next > 0 && packets < 64 && !token.IsCancellationRequested; packets++)
                {
                    capture.GetBuffer(out var pointer, out var frames, out var flags, out _, out _);
                    try
                    {
                        if (frames > SampleRate) throw new InvalidDataException("Audio packet exceeds capture budget.");
                        var silent = (flags & 2) != 0;
                        if (!silent) Marshal.Copy(pointer, samples, 0, checked((int)frames * 2));
                        for (var index = 0; index < frames; index++)
                        {
                            var sample = silent ? 0 : (samples[index * 2] + samples[index * 2 + 1]) / 65536.0;
                            if (analyzer.AddSample(sample) is { } frame) Publish(frame);
                        }
                    }
                    finally { capture.ReleaseBuffer(frames); }
                    capture.GetNextPacketSize(out next);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { ready.TrySetCanceled(token); }
        catch (Exception exception)
        {
            FailureCategory = exception.GetType().Name;
            logger.LogWarning("Process loopback unavailable ({Category}, HRESULT {Code}).", FailureCategory, exception.HResult);
            Publish(new(AudioCaptureMode.Unavailable, new double[6], Current.Revision + 1));
            ready.TrySetException(exception);
        }
        finally
        {
            callback.Abandon();
            // Windows can finish activation after our bounded wait is cancelled.
            // Keep the activation blob alive until both the call and callback finish.
            if (!activationSubmitted) callback.ReleaseCallbackParameters();
            callback.ReleaseCallerParameters();
            if (started) { try { audio?.Stop(); } catch (COMException) { } }
            Release(capture); Release(audio); Release(operation);
            if (initialized) CoUninitialize();
        }
    }

    private async Task StopCoreAsync()
    {
        _stop?.Cancel();
        await _worker.ConfigureAwait(false);
        _stop?.Dispose(); _stop = null; _processId = null;
        Publish(SpectrumFrame.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { if (_disposed) return; _disposed = true; await StopCoreAsync().ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private void Publish(SpectrumFrame frame)
    {
        Current = frame;
        if (Changed is not { } handlers) return;
        foreach (EventHandler<SpectrumFrame> handler in handlers.GetInvocationList())
            try { handler(this, frame); } catch (Exception exception) { logger.LogDebug("Spectrum subscriber failed ({Category}).", exception.GetType().Name); }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class ActivationCallback(nint parameters) : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        private int _parameterOwners = 2;
        private int _callbackParametersReleased;
        private readonly object _gate = new();
        private bool _abandoned;
        private object? _result;
        public TaskCompletionSource<object> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
        {
            object? value = null;
            try
            {
                operation.GetActivateResult(out var result, out value);
                Marshal.ThrowExceptionForHR(result);
                lock (_gate)
                {
                    if (_abandoned) { Release(value); return 0; }
                    _result = value;
                    Completion.TrySetResult(value);
                }
            }
            catch (Exception exception) { Release(value); Completion.TrySetException(exception); }
            finally { ReleaseCallbackParameters(); }
            return 0;
        }
        internal void ReleaseCallbackParameters()
        {
            if (Interlocked.Exchange(ref _callbackParametersReleased, 1) == 0) ReleaseCallerParameters();
        }
        internal void ReleaseCallerParameters()
        {
            if (Interlocked.Decrement(ref _parameterOwners) == 0) Marshal.FreeHGlobal(parameters);
        }
        public void TransferOwnership() { lock (_gate) _result = null; }
        public void Abandon() { lock (_gate) { _abandoned = true; Release(_result); _result = null; } }
    }
}
