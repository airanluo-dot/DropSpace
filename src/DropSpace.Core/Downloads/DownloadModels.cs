namespace DropSpace.Core.Downloads;

public enum DownloadTaskState { Queued, Resolving, DownloadingFile, Finalizing, Completed, Paused, Failed, Cancelled }
public enum DurableOperationState { Preparing, Transferring, Committed }
public enum TrackType { File }
public sealed record TrackProgress(TrackType Type, long DownloadedBytes, long? TotalBytes, double BytesPerSecond = 0, int ActiveConnections = 0);
public sealed record RetryPolicy(int MaxAttempts = 3, TimeSpan? InitialDelay = null, TimeSpan? MaxDelay = null)
{
    public TimeSpan GetDelay(int attempt) => TimeSpan.FromMilliseconds(Math.Min(
        (MaxDelay ?? TimeSpan.FromSeconds(16)).TotalMilliseconds,
        (InitialDelay ?? TimeSpan.FromSeconds(1)).TotalMilliseconds * Math.Pow(2, Math.Max(0, attempt - 1))));
}
public sealed record DownloadRequest(Guid TaskId, string Url, string OutputDirectory, string OutputFileName);
public sealed record OutputReservation(Guid TaskId, string OutputPath, string MarkerPath);
public sealed record DownloadTaskSnapshot
{
    public Guid Id { get; init; }
    public required DownloadRequest Request { get; init; }
    public DownloadTaskState State { get; init; }
    public DurableOperationState OperationState { get; init; }
    public long RunId { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public string OutputPath { get; init; } = "";
    public long DownloadedBytes { get; init; }
    public long? TotalBytes { get; init; }
    public double BytesPerSecond { get; init; }
    public int ActiveConnections { get; init; }
    public string? ErrorCode { get; init; }
    public string? FinalSha256 { get; init; }
}
public interface IDownloadTaskRepository
{
    Task UpsertAsync(DownloadTaskSnapshot snapshot, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DownloadTaskSnapshot>> GetAllAsync(CancellationToken cancellationToken = default);
}
public interface IOutputReservationService : IDisposable
{
    Task<OutputReservation> ReserveAsync(Guid taskId, string directory, string fileName, CancellationToken cancellationToken = default);
    Task<OutputReservation> CommitAsync(OutputReservation reservation, string stagingPath, CancellationToken cancellationToken = default);
    Task ReleaseAsync(OutputReservation reservation, CancellationToken cancellationToken = default);
}
public interface IFileNameSanitizer
{
    string Sanitize(string value, string fallback = "download.bin");
    string GetAvailablePath(string directory, string fileName);
}
