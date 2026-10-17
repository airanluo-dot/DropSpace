using System.Text.Json;
using DropSpace.App.ViewModels;
using DropSpace.Core.Models;
using NavigationProjectionProbe;

var results = new List<ScenarioResult>();
ProbeDispatcher.Run(async () =>
{
    await RunScenario("cross-section-failure", CrossSectionFailure);
    await RunScenario("cross-section-cancellation", CrossSectionCancellation);
    await RunScenario("same-section-failure", SameSectionFailure);
    await RunScenario("late-success", LateSuccess);
    await RunScenario("late-failure", LateFailure);
});
if (args.Length == 1)
    File.WriteAllText(args[0], JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"RESULT {results.Count(result => result.Passed)}/{results.Count} scenarios passed.");
Environment.ExitCode = results.All(result => result.Passed) ? 0 : 1;

async Task RunScenario(string name, Func<Checks, Task> scenario)
{
    var checks = new Checks();
    try { await scenario(checks); }
    catch (Exception error) { checks.Failures.Add("Unexpected fixture/operation exception: " + error); }
    results.Add(new(name, checks.Failures.Count == 0, checks.Failures.ToArray()));
    Console.WriteLine((checks.Failures.Count == 0 ? "PASS " : "FAIL ") + name);
    foreach (var failure in checks.Failures) Console.WriteLine("  " + failure);
}

static async Task CrossSectionFailure(Checks check)
{
    var (repository, control) = ScriptedRepository.Create();
    using var view = new MainViewModel(repository);
    var initial = await Seed(view, control);
    var navigation = view.NavigateAsync("Clipboard");
    using var failed = await control.NextAsync();
    check.That(failed.Query.Source == ItemSource.Clipboard && failed.Cursor is null, "destination did not request Clipboard's first page");
    failed.Fail(new IOException("controlled destination failure"));
    check.That(await ObserveError(navigation) is IOException, "destination failure did not propagate");
    CheckRetiredDestination(check, view, initial.Owner, "OperationIncompleteContent");

    var previousCalls = control.CallCount;
    var more = view.LoadMoreItemsAsync();
    if (!more.IsCompleted)
    {
        using var unexpected = await control.NextAsync();
        check.That(false, "failed destination queried more; oldCursorReused=" + (unexpected.Cursor == initial.Cursor) +
            ", source=" + unexpected.Query.Source + ", cursor=" + unexpected.Cursor?.Id);
        unexpected.Reply(new([Item(ItemSource.Clipboard, "clipboard-tail")], null, false));
    }
    await more;
    check.That(control.CallCount == previousCalls, "retired paging cursor was reused");
    check.That(view.Items.Count == 0, "failed destination mixed previous rows with a continuation page");
    check.That(!(view.Items.Any(card => card.Item.Source == ItemSource.Space) &&
        view.Items.Any(card => card.Item.Source == ItemSource.Clipboard)), "Space and Clipboard rows actually coexist after continuation");
}

static async Task CrossSectionCancellation(Checks check)
{
    var (repository, control) = ScriptedRepository.Create();
    using var view = new MainViewModel(repository);
    var initial = await Seed(view, control);
    using var cancellation = new CancellationTokenSource();
    var navigation = view.NavigateAsync("Pinned", cancellation.Token);
    using var pending = await control.NextAsync();
    check.That(pending.Query.PinnedOnly && pending.Cursor is null, "destination did not request Pinned's first page");
    cancellation.Cancel();
    check.That(await ObserveError(navigation) is OperationCanceledException, "navigation cancellation did not propagate");
    CheckRetiredDestination(check, view, initial.Owner, "ActionCancelled");
}

static async Task SameSectionFailure(Checks check)
{
    var (repository, control) = ScriptedRepository.Create();
    using var view = new MainViewModel(repository);
    var initial = await Seed(view, control);
    var status = view.ObservedStatus;
    var refresh = view.NavigateAsync("Space");
    using var failed = await control.NextAsync();
    failed.Fail(new IOException("controlled same-section failure"));
    check.That(await ObserveError(refresh) is IOException, "same-section failure did not propagate");
    check.That(view.Items.Count == 1 && ReferenceEquals(view.Items[0], initial.Card), "same-section failure replaced accepted cards");
    check.That(ReferenceEquals(view.SelectedItem, initial.Card), "same-section failure lost selection");
    check.That(view.HasMoreItems && view.ObservedCursor == initial.Cursor && view.ItemCount == 1 && !view.IsEmpty && !view.IsBusy,
        "same-section failure changed the accepted paging/count state");
    check.That(view.ActiveAssetOwners == 1 && ReferenceEquals(view.AssetFor(initial.Card), initial.Owner) && !initial.Owner.Token.IsCancellationRequested,
        "same-section failure retired or replaced accepted asset ownership");
    check.That(ReferenceEquals(view.ObservedStatus, status), "same-section failure replaced the accepted status");
}

static async Task LateSuccess(Checks check)
{
    var (repository, control) = ScriptedRepository.Create();
    using var view = new MainViewModel(repository);
    await Seed(view, control);
    var older = view.NavigateAsync("Clipboard");
    using var oldQuery = await control.NextAsync();
    var newer = view.NavigateAsync("Pinned");
    using var newQuery = await control.NextAsync();
    var accepted = Item(ItemSource.Space, "current-pin") with { IsPinned = true };
    var cursor = Cursor(accepted);
    newQuery.Reply(new([accepted], cursor, true));
    await newer;
    var selected = view.Items.Single();
    view.SelectedItem = selected;
    var owner = view.AssetFor(selected);

    oldQuery.Reply(new([Item(ItemSource.Clipboard, "obsolete-result")], null, false));
    await older;
    check.That(view.CurrentSection == "Pinned" && view.Items.Count == 1 && view.Items[0].Id == accepted.Id,
        "late success replaced the current destination or rows");
    check.That(ReferenceEquals(view.SelectedItem, selected) && view.ObservedCursor == cursor && view.HasMoreItems && !view.IsBusy,
        "late success changed current selection/paging/busy state");
    check.That(ReferenceEquals(view.AssetFor(selected), owner) && !owner.Token.IsCancellationRequested,
        "late success retired current asset ownership");
}

static async Task LateFailure(Checks check)
{
    var (repository, control) = ScriptedRepository.Create();
    using var view = new MainViewModel(repository);
    await Seed(view, control);
    var older = view.NavigateAsync("Clipboard");
    using var oldQuery = await control.NextAsync();
    // Search input is fixture state; debounce is outside this probe. The production
    // projection service and navigation still own query construction and publication.
    view.SearchText = "no-current-matches";
    var newer = view.NavigateAsync("Pinned");
    using var newQuery = await control.NextAsync();
    check.That(newQuery.Query.Search == view.SearchText, "current search was not forwarded by the projection service");
    newQuery.Reply(new([], null, false));
    await newer;
    var status = view.ObservedStatus;
    check.That(status.ResourceKey == "SearchNoMatches", "current empty search did not own its status");

    oldQuery.Fail(new IOException("controlled late failure"));
    check.That(await ObserveError(older) is IOException, "late failure did not propagate to its caller");
    check.That(ReferenceEquals(view.ObservedStatus, status), "late failure overwrote current status");
    check.That(view.CurrentSection == "Pinned" && view.IsEmpty && view.Items.Count == 0 && view.ItemCount == 0 && !view.IsBusy,
        "late failure changed the current empty projection");
}

static void CheckRetiredDestination(Checks check, MainViewModel view, AssetOwner previousOwner, string statusKey)
{
    check.That(view.Items.Count == 0 && view.ItemCount == 0 && view.SelectedItem is null, "previous destination rows/count/selection remain");
    check.That(view.ObservedCursor is null && !view.HasMoreItems, "previous destination cursor/HasMore remain");
    check.That(previousOwner.Token.IsCancellationRequested && view.ActiveAssetOwners == 0, "previous destination asset work remains owned");
    check.That(!view.IsBusy && !view.IsEmpty, "failed/unconfirmed destination is busy or claims a confirmed empty result");
    check.That(view.ObservedStatus.ResourceKey == statusKey, "destination status is not " + statusKey);
}

static async Task<(ItemCardViewModel Card, AssetOwner Owner, ItemQueryCursor Cursor)> Seed(MainViewModel view, ScriptedRepository control)
{
    var item = Item(ItemSource.Space, "initial-space");
    var cursor = Cursor(item);
    var seed = view.NavigateAsync("Space");
    using var query = await control.NextAsync();
    query.Reply(new([item], cursor, true));
    await seed;
    var card = view.Items.Single();
    view.SelectedItem = card;
    return (card, view.AssetFor(card), cursor);
}

static async Task<Exception?> ObserveError(Task operation)
{
    try { await operation; return null; }
    catch (Exception error) { return error; }
}

static ItemQueryCursor Cursor(DropItem item) => new(0, item.CreatedAtUtc, item.Id);
static DropItem Item(ItemSource source, string title) => new(Guid.NewGuid(), source, ItemKind.File,
    title, DateTimeOffset.Parse("2026-10-17T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
    null, false, ItemStatus.Available, title, 1, null, null, null, null, null, null, null);

internal sealed class Checks
{
    internal List<string> Failures { get; } = [];
    internal void That(bool condition, string message) { if (!condition) Failures.Add(message); }
}

internal sealed record ScenarioResult(string Name, bool Passed, IReadOnlyList<string> Failures);
