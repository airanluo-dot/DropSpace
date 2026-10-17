using System.Threading.Channels;
using DropSpace.App.Services;
using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Dispatching;

namespace ClipboardIslandRefreshProbe;

internal sealed class ControlledBoundary<T>
{
    private readonly Channel<ControlledCall<T>> _calls = Channel.CreateUnbounded<ControlledCall<T>>();
    internal int CallCount { get; private set; }

    internal Task<T> Start(CancellationToken cancellationToken)
    {
        var call = new ControlledCall<T>();
        CallCount++;
        if (!_calls.Writer.TryWrite(call)) throw new InvalidOperationException("Fixture boundary closed.");
        return call.Completion.WaitAsync(cancellationToken);
    }

    internal Task<ControlledCall<T>> NextAsync() =>
        _calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
}

internal sealed class ControlledCall<T>
{
    private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task<T> Completion => _completion.Task;
    internal void Reply(T value) => _completion.SetResult(value);
    internal void Fail(string message) => _completion.SetException(new IOException(message));
}

internal sealed class Fixture : IAsyncDisposable
{
    internal MainViewModel Main { get; } = new();
    internal ClipboardCaptureService Capture { get; } = new();
    internal DispatcherQueue Dispatcher { get; } = new();
    internal ClipboardIslandViewModel View { get; }

    internal Fixture() => View = new(Main, Capture, IdentityAppStringLocalizer.Instance,
        NullLogger<ClipboardIslandViewModel>.Instance, Dispatcher);

    internal void RequestRefresh() => Capture.Publish(Card("capture trigger").Item);

    internal async Task PublishNextAsync()
    {
        var callback = await Dispatcher.NextAsync();
        callback();
    }

    public async ValueTask DisposeAsync()
    {
        var shutdown = View.DisposeAsync().AsTask();
        Dispatcher.ReleaseForShutdown();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
    }

    internal static ItemCardViewModel Card(string title) => new(
        new DropItem(Guid.NewGuid(), ItemSource.Clipboard, ItemKind.Text, title,
            DateTimeOffset.Parse("2026-10-17T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            null, false, ItemStatus.Available, title, 1, null, null, null, null, null, null, null),
        IdentityAppStringLocalizer.Instance);
}
