using DropSpace.Core.Models;

namespace DropSpace.Infrastructure.Lyrics;

/// <summary>Single component state for both the GPU control and DLC page. Never downloads a model.</summary>
public sealed class CudaComponentService(CudaLyricsRuntimePackage package,
    PersistentPlainLyricsRunner runner, AiLyricsRuntimeOptions options) : IDisposable
{
    private readonly SemaphoreSlim _operation = new(1, 1);
    private CancellationTokenSource? _download;
    private bool _disposed;
    public event EventHandler? StateChanged;
    public bool IsNvidia => CudaDriverAvailability.IsCompatible();
    public bool IsInstalled { get; private set; }
    public bool IsDownloading { get; private set; }
    public double Progress { get; private set; }
    public string? Error { get; private set; }
    public string Id => CudaLyricsRuntimePackage.RuntimeId;
    public long DownloadBytes { get { try { return package.DownloadBytes; } catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException) { return 0; } } }
    public long InstalledBytes { get { try { return package.InstalledBytes; } catch (Exception e) when (e is IOException or InvalidDataException or System.Text.Json.JsonException) { return 0; } } }
    public bool HasDownloadManifest => DownloadBytes > 0;
    public bool ShouldOffer(LyricsSettings settings) => settings.AiLyricsGpuAccelerationEnabled &&
        settings.AiLyricsGpuBackend != LyricsGpuBackend.Vulkan && IsNvidia && !IsInstalled;
    public async Task RefreshAsync(CancellationToken token)
    {
        await _operation.WaitAsync(token).ConfigureAwait(false);
        try
        {
            try { await package.EnsureWorkerAsync(token).ConfigureAwait(false); IsInstalled = true; }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) { IsInstalled = false; }
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        finally { _operation.Release(); }
    }
    public async Task DownloadAsync(bool consent, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!consent) throw new InvalidOperationException("Explicit CUDA component consent is required.");
        await _operation.WaitAsync(token).ConfigureAwait(false);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        _download = stop;
        try
        {
            IsDownloading = true; Error = null; Progress = 0; StateChanged?.Invoke(this, EventArgs.Empty);
            await package.DownloadAsync(consent, new ComponentProgress(value =>
            { Progress = value; StateChanged?.Invoke(this, EventArgs.Empty); }), stop.Token).ConfigureAwait(false);
            await package.EnsureWorkerAsync(stop.Token).ConfigureAwait(false);
            await runner.DrainCleanupAsync(stop.Token).ConfigureAwait(false);
            options.NotifyBackendChanged(); IsInstalled = true; Progress = 1;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { Error = null; throw; }
        catch (Exception e) { Error = e.GetType().Name; throw; }
        finally
        {
            _download = null; IsDownloading = false; StateChanged?.Invoke(this, EventArgs.Empty); _operation.Release();
        }
    }
    public void CancelDownload() { try { _download?.Cancel(); } catch (ObjectDisposedException) { } }
    public async Task RemoveAsync(CancellationToken token)
    {
        CancelDownload(); await _operation.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await runner.DrainCleanupAsync(token).ConfigureAwait(false);
            await package.RemoveAsync(token).ConfigureAwait(false);
            options.NotifyBackendChanged(); IsInstalled = false; Progress = 0; Error = null;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        finally { _operation.Release(); }
    }
    public void Dispose() { _disposed = true; CancelDownload(); }
    private sealed class ComponentProgress(Action<double> report) : IProgress<double>
    { public void Report(double value) => report(value); }
}
