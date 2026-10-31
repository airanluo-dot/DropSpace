using DropSpace.App.Services;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using DropSpace.Core.Preview;
using Microsoft.Extensions.Logging.Abstractions;

var scenario = args.Length == 0 ? "all" : args[0];
if (scenario is "all" or "invalid-cache") await InvalidCacheAsync();
if (scenario is "all" or "cancel") await CancellationAsync();
if (scenario is not ("all" or "invalid-cache" or "cancel"))
    throw new ArgumentException("Expected all, invalid-cache or cancel.");

static async Task InvalidCacheAsync()
{
    var cache = new FixtureCache(_ => throw new InvalidDataException("Cache destination is unsafe."));
    var service = Create(cache);
    var item = Item();
    var descriptor = Descriptor(item);
    // MainPage calls this after native PDF rendering and before opening its dialog.
    // An optional cache failure must not throw and hide/dispose that rendered host.
    await service.CacheSuccessfulAsync(item, descriptor);
    Require(cache.Calls == 1, "The optional cache boundary was not exercised.");
    Console.WriteLine("PASS invalid-cache: rejected cache destination cannot fail rendered-preview publication");
}

static async Task CancellationAsync()
{
    using var stop = new CancellationTokenSource();
    stop.Cancel();
    var cache = new FixtureCache(token =>
    {
        token.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    });
    var service = Create(cache);
    var item = Item();
    try
    {
        await service.CacheSuccessfulAsync(item, Descriptor(item), cancellationToken: stop.Token);
        throw new InvalidOperationException("Page cancellation was swallowed as an optional cache failure.");
    }
    catch (OperationCanceledException error) when (error.CancellationToken == stop.Token) { }
    Require(cache.Calls == 1, "Cancellation did not reach the persistence boundary.");
    Console.WriteLine("PASS cancel: page cancellation still prevents preview publication");
}

static QuickPreviewService Create(IPreviewCache cache) => new(new UnusedRegistry(), new UnusedPayloadStore(), cache,
    NullLogger<QuickPreviewService>.Instance);

static DropItem Item() => new(Guid.NewGuid(), ItemSource.Space, ItemKind.File, "document.pdf",
    DateTimeOffset.UtcNow, null, false, ItemStatus.Available, "document.pdf", 1, null, null,
    null, null, null, null, null);

static PreviewDescriptor Descriptor(DropItem item) => new(item.Id, PreviewKind.Pdf, item.Title, "application/pdf",
    null, null, null, null, 1, null, new Dictionary<string, string> { ["page"] = "1" });

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

internal sealed class FixtureCache(Func<CancellationToken, Task> write) : IPreviewCache
{
    public int Calls { get; private set; }
    public long Generation => 0;
    public Task PutAsync(PreviewRequest request, PreviewDescriptor descriptor, CancellationToken cancellationToken = default)
    {
        Calls++;
        return write(cancellationToken);
    }
    public Task<PreviewDescriptor?> TryGetAsync(Guid itemId, int revision, PreviewKind kind, int page, int targetPixelWidth,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task ClearAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal sealed class UnusedRegistry : IPreviewProviderRegistry
{
    public IReadOnlyList<IPreviewProvider> Providers => [];
    public Task<PreviewCapability> ProbeAsync(DropItemSnapshot item, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    public Task<PreviewDescriptor> LoadAsync(PreviewRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

internal sealed class UnusedPayloadStore : IPayloadStore
{
    public string ResolvePath(string relativePath) => throw new NotSupportedException();
    public Task<PayloadRecord> WriteAsync(string kind, Stream source, long maximumBytes, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    public Task<PayloadRecord> WriteFileAsync(string kind, string? extension, Stream source, long maximumBytes, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
    public Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeleteAsync(string relativePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task ExportAsync(string relativePath, string destinationPath, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
