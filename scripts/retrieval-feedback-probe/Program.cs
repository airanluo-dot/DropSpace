using System.ComponentModel;
using System.Text.Json;
using System.Threading.Channels;
using DropSpace.App.Services;
using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Dispatching;
using RetrievalFeedbackProbe;

var results = new List<object>();
var failed = 0;
await RunAsync("recovered-query-clears-refresh-error", RecoveryAsync);
await RunAsync("successful-query-preserves-action-feedback", ActionFeedbackAsync);
await RunAsync("preview-whitespace-compatibility", () =>
{
    Require(Card(" \talpha\r\nbeta \n").Preview == "alpha  beta", "Short preview whitespace behavior changed.");
    Require(Card(" \r\n\t").Preview == string.Empty, "Whitespace-only preview changed.");
    return Task.CompletedTask;
});
await RunAsync("preview-surrogate-boundary", () =>
{
    Require(Card(new string('a', 218) + "🎵tail").Preview == new string('a', 218) + "…",
        "Truncation split a supplementary character.");
    return Task.CompletedTask;
});
await RunAsync("large-preview-has-bounded-copy-allocation", () =>
{
    _ = Card("warm\r\nup").Preview;
    var card = Card(string.Concat(Enumerable.Repeat("a\r\n", 100_000)));
    var before = GC.GetAllocatedBytesForCurrentThread();
    var preview = card.Preview;
    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    Require(preview == string.Concat(Enumerable.Repeat("a  ", 73)) + "…", "Large preview output changed.");
    Require(allocated < 65_536, $"Formatting a 220-character preview allocated {allocated} bytes.");
    return Task.CompletedTask;
});
if (args.Length > 0) File.WriteAllText(args[0], JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"RESULT {results.Count - failed}/{results.Count} scenarios passed.");
Environment.ExitCode = failed == 0 ? 0 : 1;

async Task RunAsync(string name, Func<Task> run)
{
    try { await run(); results.Add(new { Name = name, Passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; results.Add(new { Name = name, Passed = false, Error = error.Message }); Console.WriteLine("FAIL " + name + ": " + error.Message); }
}

static async Task RecoveryAsync()
{
    var main = new MainViewModel();
    using var view = View(main);
    var failed = ObserveAsync(view, nameof(IslandRetrievalViewModel.Status));
    view.SetVisible(new object(), true);
    (await main.Reads.NextAsync()).Fail();
    await failed;
    Require(view.Status == "SearchUnavailable", "Fixture did not publish the initial query failure.");
    await PublishAsync(view, main, [Card("recovered")]);
    Require(view.Items.Single().Title == "recovered", "Successful retry did not publish rows.");
    Require(view.Status == string.Empty, "Recovered rows retained SearchUnavailable.");
}

static async Task ActionFeedbackAsync()
{
    var main = new MainViewModel();
    using var view = View(main);
    var ready = ObserveAsync(view, nameof(IslandRetrievalViewModel.CanReference));
    view.SetVisible(new object(), true);
    var card = Card("selected");
    (await main.Reads.NextAsync()).Reply([card]);
    await ready;
    view.SelectedItem = view.Items.Single();
    Require(await view.CopyAsync(), "Fixture copy did not complete.");
    await PublishAsync(view, main, [card]);
    Require(view.Status == "ItemCopied", "Passive refresh erased successful action feedback.");
    main.FailCopy = true;
    Require(!await view.CopyAsync(), "Fixture copy failure was not observed.");
    await PublishAsync(view, main, [card]);
    Require(view.Status == "ClipboardIslandActionFailed", "Passive refresh erased failed action feedback.");
}

static async Task PublishAsync(IslandRetrievalViewModel view, MainViewModel main, IReadOnlyList<ItemCardViewModel> cards)
{
    var completed = ObserveAsync(view, nameof(IslandRetrievalViewModel.CanReference));
    view.RequestRefresh();
    (await main.Reads.NextAsync()).Reply(cards);
    await completed;
}

static Task ObserveAsync(INotifyPropertyChanged view, string property)
{
    var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    PropertyChangedEventHandler? handler = null;
    handler = (_, args) =>
    {
        if (args.PropertyName != property) return;
        view.PropertyChanged -= handler;
        complete.TrySetResult();
    };
    view.PropertyChanged += handler;
    return complete.Task.WaitAsync(TimeSpan.FromSeconds(5));
}

static IslandRetrievalViewModel View(MainViewModel main) => new(main, new UnusedRecent(), new ClipboardCaptureService(),
    new QuickPreviewService(), new ReferenceWindowService(), IdentityAppStringLocalizer.Instance,
    new DispatcherQueue(), NullLogger<IslandRetrievalViewModel>.Instance);

static ItemCardViewModel Card(string text) => new(new DropItem(Guid.NewGuid(), ItemSource.Space, ItemKind.Text,
    text, DateTimeOffset.UtcNow, null, false, ItemStatus.Available, text, 1, null, null, null,
    new TextPayload(text, text.Length, DetectedSubtype.Plain, DetectionConfidence.High, null), null, null, null), IdentityAppStringLocalizer.Instance);

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

namespace RetrievalFeedbackProbe
{
    internal sealed class ReadBoundary
    {
        private readonly Channel<ReadCall> _calls = Channel.CreateUnbounded<ReadCall>();
        internal Task<IReadOnlyList<ItemCardViewModel>> Start(CancellationToken token)
        {
            var call = new ReadCall();
            if (!_calls.Writer.TryWrite(call)) throw new InvalidOperationException("Fixture closed.");
            return call.Task.WaitAsync(token);
        }
        internal Task<ReadCall> NextAsync() => _calls.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }
    internal sealed class ReadCall
    {
        private readonly TaskCompletionSource<IReadOnlyList<ItemCardViewModel>> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<IReadOnlyList<ItemCardViewModel>> Task => _completion.Task;
        internal void Reply(IReadOnlyList<ItemCardViewModel> cards) => _completion.SetResult(cards);
        internal void Fail() => _completion.SetException(new IOException("Fixture query failure."));
    }
}
