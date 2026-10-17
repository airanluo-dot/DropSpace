using System.Collections.Specialized;
using System.Text.Json;
using ClipboardIslandRefreshProbe;
using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using Microsoft.UI.Xaml.Media.Imaging;
using NavigationProjectionProbe;
using Windows.Storage;

string? selected = null;
string? outputPath = null;
for (var index = 0; index < args.Length; index++)
{
    if (args[index] == "--scenario" && index + 1 < args.Length) selected = args[++index];
    else if (outputPath is null) outputPath = args[index];
    else throw new ArgumentException("Expected [--scenario name] [results.json].");
}

var scenarios = new (string Name, Func<Checks, Task> Run)[]
{
    ("refresh_error_recovery", RefreshErrorRecovery),
    ("retired_success", RetiredSuccess),
    ("retired_failure", RetiredFailure),
    ("remaining_visible_owner", RemainingVisibleOwner),
};
if (selected == "pin_action_recovery")
    scenarios = [("pin_action_recovery", PinActionRecovery)];
if (selected == "projection_identity")
    scenarios = [("projection_identity", ProjectionIdentity)];
if (selected == "action_error_ownership")
    scenarios = [("action_error_ownership", ActionErrorOwnership)];
if (selected is not null && !scenarios.Any(scenario => scenario.Name == selected))
    throw new ArgumentException("Unknown scenario: " + selected);
var results = new List<ScenarioResult>();
ProbeDispatcher.Run(async () =>
{
    foreach (var scenario in scenarios.Where(scenario => selected is null || scenario.Name == selected))
    {
        var checks = new Checks();
        try { await scenario.Run(checks); }
        catch (Exception error) { checks.That(false, "Unexpected fixture/operation exception: " + error); }
        var passed = checks.Assertions.All(assertion => assertion.Passed);
        results.Add(new(scenario.Name, passed, checks.Assertions.ToArray()));
        Console.WriteLine((passed ? "PASS " : "FAIL ") + scenario.Name);
        foreach (var assertion in checks.Assertions.Where(assertion => !assertion.Passed))
            Console.WriteLine("  " + assertion.Message);
    }
});
if (outputPath is not null)
    File.WriteAllText(outputPath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"RESULT {results.Count(result => result.Passed)}/{results.Count} scenarios passed.");
Environment.ExitCode = results.All(result => result.Passed) ? 0 : 1;

static async Task ActionErrorOwnership(Checks check)
{
    await using var fixture = new Fixture();
    var card = Fixture.Card("action feedback owner");
    var originalGeneration = card.PresentationGeneration;

    // Use the actual card lifecycle: Undo-like restoration may reuse the same instance.
    var retiredAction = fixture.View.CopyAsync(card);
    var retiredCopy = await fixture.Main.Copies.NextAsync();
    card.RetirePresentation();
    var restored = card.TryUpdate(card.Item with { Revision = card.Item.Revision + 1 });
    check.That(restored && !card.IsPresentationRetired && card.PresentationGeneration != originalGeneration,
        "The fixture did not restore the same card instance under a new presentation generation.");
    check.That(!retiredAction.IsCompleted && !retiredCopy.Completion.IsCompleted,
        "Retiring and restoring a card canceled or completed the outstanding upstream action.");
    retiredCopy.Fail("controlled failure from the retired presentation");
    await retiredAction;
    check.That(fixture.View.Error.Length == 0,
        "An old action published an error after its card instance was retired and restored.");

    // No visible owner is required for direct calls. Metadata revision is not presentation ownership.
    var currentAction = fixture.View.CopyAsync(card);
    var currentCopy = await fixture.Main.Copies.NextAsync();
    var currentGeneration = card.PresentationGeneration;
    var metadataApplied = card.TryUpdate(card.Item with
    {
        Revision = card.Item.Revision + 1,
        IsPinned = !card.IsPinned,
        LastUsedAtUtc = card.Item.CreatedAtUtc.AddMinutes(1),
    });
    check.That(metadataApplied && card.PresentationGeneration == currentGeneration,
        "Metadata progression unexpectedly changed the current presentation generation.");
    currentCopy.Fail("controlled failure from the current presentation");
    await currentAction;
    check.That(fixture.View.Error == "ClipboardIslandActionFailed" && fixture.Main.Reads.CallCount == 0,
        "A current direct action lost its error after metadata advanced while no surface owner was visible.");

    // Keep the worker read pending so an unrelated successful refresh cannot mask an error.
    var owner = new object();
    fixture.View.SetVisible(owner, true);
    _ = await fixture.Main.Reads.NextAsync();
    var hiddenAction = fixture.View.CopyAsync(card);
    var hiddenCopy = await fixture.Main.Copies.NextAsync();
    fixture.View.SetVisible(owner, false);
    fixture.View.SetVisible(owner, true);
    check.That(!hiddenAction.IsCompleted && !hiddenCopy.Completion.IsCompleted,
        "Hiding and reopening the surface canceled or completed the outstanding upstream action.");
    hiddenCopy.Fail("controlled failure from the preceding visibility period");
    await hiddenAction;
    check.That(fixture.View.Error.Length == 0,
        "An action from before the hide/reopen cycle published an error on the reopened surface.");

    var disposedAction = fixture.View.CopyAsync(card);
    var disposedCopy = await fixture.Main.Copies.NextAsync();
    await fixture.DisposeAsync();
    check.That(!disposedAction.IsCompleted && !disposedCopy.Completion.IsCompleted,
        "Disposing the presentation canceled or completed the outstanding upstream action.");
    disposedCopy.Fail("controlled failure after presentation disposal");
    await disposedAction;
    check.That(fixture.View.Error.Length == 0,
        "An action completing after disposal published a presentation error.");
    check.That(fixture.Main.Copies.CallCount == 4,
        "The ownership scenario did not execute exactly its four controlled action phases.");
}

static async Task ProjectionIdentity(Checks check)
{
    await using var fixture = new Fixture();
    var original = Fixture.Card("original.txt").Item;
    var retained = Snapshot(original with
    {
        Kind = ItemKind.File,
        File = new FileReference("original.txt", "original.txt", FileEntryKind.File, ".txt", 7,
            original.CreatedAtUtc, null, null),
    });
    var removed = Fixture.Card("removed from next snapshot");
    var thumbnail = new BitmapImage();
    var storage = new ProbeStorageItem();
    retained.Thumbnail = thumbnail;
    retained.DragStorageItem = storage;
    removed.Thumbnail = new BitmapImage();
    removed.DragStorageItem = new ProbeStorageItem();
    var retainedGeneration = retained.PresentationGeneration;
    var removedGeneration = removed.PresentationGeneration;

    fixture.View.SetVisible(new object(), true);
    var seed = await fixture.Main.Reads.NextAsync();
    seed.Reply([retained, removed]);
    await fixture.PublishNextAsync();
    var changes = new List<NotifyCollectionChangedEventArgs>();
    fixture.View.Items.CollectionChanged += (_, change) => changes.Add(change);

    // Keep a real PinAsync operation pending while an equivalent fresh card arrives.
    var pin = fixture.View.PinAsync(retained);
    var pendingPin = await fixture.Main.Pins.NextAsync();
    var metadata = retained.Item with
    {
        Revision = retained.Item.Revision + 1,
        Title = "metadata refresh",
        LastUsedAtUtc = retained.Item.CreatedAtUtc.AddMinutes(1),
    };
    await PublishAsync(Snapshot(metadata));
    check.That(fixture.View.Items.Count == 1 && ReferenceEquals(fixture.View.Items[0], retained) && retained.Item == metadata,
        "An equivalent snapshot did not update the existing same-ID card instance.");
    check.That(!retained.IsPinActionEnabled && fixture.Main.Pins.CallCount == 1,
        "An equivalent refresh lost the pending pin action state.");
    check.That(changes.Count == 1 && changes[0].Action == NotifyCollectionChangedAction.Remove &&
        changes[0].OldItems?.Count == 1 && ReferenceEquals(changes[0].OldItems![0], removed),
        "An equivalent refresh rebuilt the retained row instead of removing only the missing card.");
    check.That(retained.PresentationGeneration == retainedGeneration &&
        ReferenceEquals(retained.Thumbnail, thumbnail) && ReferenceEquals(retained.DragStorageItem, storage),
        "A same-content metadata refresh discarded current presentation assets or generation.");
    check.That(removed.IsPresentationRetired && removed.Thumbnail is null && removed.DragStorageItem is null,
        "A missing card was removed without retiring and releasing its presentation assets.");
    var removedCallbackRan = false;
    check.That(!removed.TryApplyPresentation(removedGeneration, () => removedCallbackRan = true) && !removedCallbackRan,
        "A removed card accepted a presentation callback from its old generation.");
    check.That(fixture.Main.QuickActionRefreshCount == 1,
        "The reused card did not request a quick-action refresh through the upstream boundary.");
    pendingPin.Fail("controlled pending pin release after identity assertions");
    await pin;

    // A changed file path, unlike title or pin metadata, invalidates presentation content.
    var replacement = retained.Item with
    {
        Revision = retained.Item.Revision + 1,
        File = retained.Item.File! with { OriginalPath = "replacement.txt", NormalizedPath = "replacement.txt" },
    };
    await PublishAsync(Snapshot(replacement));
    check.That(fixture.View.Items.Count == 1 && ReferenceEquals(fixture.View.Items[0], retained) && retained.Item == replacement,
        "A content change replaced the card instance or failed to apply the new snapshot.");
    check.That(retained.PresentationGeneration > retainedGeneration && retained.Thumbnail is null && retained.DragStorageItem is null,
        "A content change did not advance the presentation generation and clear old assets.");
    var replacedCallbackRan = false;
    check.That(!retained.TryApplyPresentation(retainedGeneration, () => replacedCallbackRan = true) && !replacedCallbackRan,
        "A content change accepted a presentation callback from the preceding file reference.");

    // Model a removal retiring the visible card before an already-read snapshot publishes.
    retained.Thumbnail = new BitmapImage();
    retained.DragStorageItem = new ProbeStorageItem();
    var beforeRetirement = retained.PresentationGeneration;
    fixture.RequestRefresh();
    var staleRead = await fixture.Main.Reads.NextAsync();
    staleRead.Reply([Snapshot(retained.Item)]);
    var queuedPublication = await fixture.Dispatcher.NextAsync();
    retained.RetirePresentation();
    queuedPublication();
    check.That(fixture.View.Items.Count == 0 && retained.IsPresentationRetired &&
        retained.Thumbnail is null && retained.DragStorageItem is null,
        "An equal-revision snapshot revived a retired card or retained its assets.");
    check.That(!retained.IsPresentationCurrent(beforeRetirement) && fixture.Main.QuickActionRefreshCount == 2,
        "Rejecting a retired revision revived its generation or refreshed its quick actions.");

    // A later authoritative revision, as supplied after Undo, may re-enter the projection.
    var restored = Snapshot(retained.Item with { Revision = retained.Item.Revision + 1 });
    await PublishAsync(restored);
    check.That(fixture.View.Items.Count == 1 && ReferenceEquals(fixture.View.Items[0], restored) && !restored.IsPresentationRetired,
        "A newer revision could not restore the removed item to the projection.");

    // Also exercise a newer revision arriving while the retired instance remains visible.
    var beforeSecondRetirement = restored.PresentationGeneration;
    restored.RetirePresentation();
    var restoredAgain = restored.Item with { Revision = restored.Item.Revision + 1 };
    await PublishAsync(Snapshot(restoredAgain));
    check.That(fixture.View.Items.Count == 1 && ReferenceEquals(fixture.View.Items[0], restored) &&
        restored.Item == restoredAgain && !restored.IsPresentationRetired,
        "A newer revision did not restore the still-present retired instance through its merge contract.");
    check.That(!restored.IsPresentationCurrent(beforeSecondRetirement) && fixture.Main.QuickActionRefreshCount == 3,
        "Restoring a retired instance reused an old generation or skipped quick-action refresh.");
    check.That(changes.All(change => change.Action != NotifyCollectionChangedAction.Reset),
        "Projection synchronization emitted a collection Reset/Clear notification.");

    async Task PublishAsync(params ItemCardViewModel[] cards)
    {
        fixture.RequestRefresh();
        var read = await fixture.Main.Reads.NextAsync();
        read.Reply(cards);
        await fixture.PublishNextAsync();
    }
    static ItemCardViewModel Snapshot(DropItem item) => new(item, IdentityAppStringLocalizer.Instance);
}

static async Task PinActionRecovery(Checks check)
{
    await using var fixture = new Fixture();
    var card = Fixture.Card("controlled pin action");
    var notifications = new List<string?>();
    card.PropertyChanged += (_, change) => notifications.Add(change.PropertyName);
    check.That(card.IsPinActionEnabled && !card.IsPinned, "A fresh card is not enabled and unpinned.");

    var first = fixture.View.PinAsync(card);
    var pending = await fixture.Main.Pins.NextAsync();
    check.That(!card.IsPinActionEnabled, "A pending pin action left its card enabled.");
    var duplicate = fixture.View.PinAsync(card);
    if (fixture.Main.Pins.CallCount > 1)
    {
        var unexpected = await fixture.Main.Pins.NextAsync();
        unexpected.Fail("unexpected duplicate pin action");
    }
    await duplicate;
    check.That(fixture.Main.Pins.CallCount == 1, "A second pin request entered the upstream operation while the first was pending.");

    notifications.Clear();
    pending.Fail("controlled pin persistence failure");
    await first;
    check.That(card.IsPinActionEnabled && !card.IsPinned, "A failed pin action did not restore enabled state and the known pin value.");
    check.That(notifications.Contains(nameof(card.IsPinned)) && notifications.Contains(nameof(card.PinActionLabel)),
        "A failed pin action did not notify the binding of its unchanged authoritative state and action label.");
    check.That(fixture.View.Error == "ClipboardIslandActionFailed", "A failed pin action did not publish its error.");

    notifications.Clear();
    var retry = fixture.View.PinAsync(card);
    var retried = await fixture.Main.Pins.NextAsync();
    check.That(!card.IsPinActionEnabled && fixture.View.Error.Length == 0,
        "Retry did not disable the card and clear the preceding action error.");
    retried.Reply(card.Item with { IsPinned = true });
    await retry;
    check.That(fixture.Main.Pins.CallCount == 2 && card.IsPinActionEnabled && card.IsPinned,
        "The successful retry did not publish the confirmed pin value and restore availability.");
    check.That(notifications.Contains(nameof(card.IsPinned)) && notifications.Contains(nameof(card.PinActionLabel)) && fixture.View.Error.Length == 0,
        "The successful retry did not publish state notifications without a stale error.");
}

static async Task RefreshErrorRecovery(Checks check)
{
    await using var fixture = new Fixture();
    fixture.View.SetVisible(new object(), true);
    var failed = await fixture.Main.Reads.NextAsync();
    failed.Fail("controlled refresh failure");
    await fixture.PublishNextAsync();
    check.That(fixture.View.Error == "ClipboardIslandActionFailed", "Refresh failure did not publish its error.");

    fixture.RequestRefresh();
    var recovery = await fixture.Main.Reads.NextAsync();
    var recovered = Fixture.Card("recovered");
    recovery.Reply([recovered]);
    await fixture.PublishNextAsync();
    check.That(fixture.View.Items.Count == 1 && ReferenceEquals(fixture.View.Items[0], recovered),
        "Recovery did not publish the successful cards.");
    check.That(fixture.View.Error.Length == 0, "A successful refresh retained the prior refresh error.");

    fixture.RequestRefresh();
    var pendingRefresh = await fixture.Main.Reads.NextAsync();
    var copy = fixture.View.CopyAsync(recovered);
    var pendingCopy = await fixture.Main.Copies.NextAsync();
    pendingCopy.Fail("controlled action failure while refresh is pending");
    await copy;
    var actionError = fixture.View.Error;
    check.That(actionError == "ClipboardIslandActionFailed", "Concurrent action failure did not publish its error.");
    var refreshed = Fixture.Card("refresh after action failed");
    pendingRefresh.Reply([refreshed]);
    await fixture.PublishNextAsync();
    check.That(fixture.View.Items.Count == 1 && ReferenceEquals(fixture.View.Items[0], refreshed),
        "Concurrent successful refresh did not publish its cards.");
    check.That(fixture.View.Error == actionError, "A successful refresh swallowed the concurrent action failure.");
}

static async Task RetiredSuccess(Checks check)
{
    await using var fixture = new Fixture();
    var owner = new object();
    fixture.View.SetVisible(owner, true);
    var seed = await fixture.Main.Reads.NextAsync();
    var cached = Fixture.Card("cached");
    seed.Reply([cached]);
    await fixture.PublishNextAsync();

    fixture.RequestRefresh();
    var oldRead = await fixture.Main.Reads.NextAsync();
    var obsolete = Fixture.Card("obsolete");
    oldRead.Reply([obsolete]);
    var oldPublication = await fixture.Dispatcher.NextAsync();
    fixture.View.SetVisible(owner, false);
    check.That(fixture.View.Items.Count == 1 && ReferenceEquals(fixture.View.Items[0], cached),
        "Hiding every owner discarded the cached cards.");
    fixture.View.SetVisible(owner, true);
    oldPublication();
    check.That(fixture.View.Items.Count == 1 && ReferenceEquals(fixture.View.Items[0], cached),
        "An old successful publication replaced the reopened surface cache.");

    var currentRead = await fixture.Main.Reads.NextAsync();
    var current = Fixture.Card("current");
    currentRead.Reply([current]);
    await fixture.PublishNextAsync();
    check.That(fixture.View.Items.Count == 1 && ReferenceEquals(fixture.View.Items[0], current),
        "The reopened surface did not accept its current refresh.");
}

static async Task RetiredFailure(Checks check)
{
    await using var fixture = new Fixture();
    var owner = new object();
    fixture.View.SetVisible(owner, true);
    var oldRead = await fixture.Main.Reads.NextAsync();
    oldRead.Fail("controlled obsolete failure");
    var oldPublication = await fixture.Dispatcher.NextAsync();
    fixture.View.SetVisible(owner, false);
    fixture.View.SetVisible(owner, true);
    oldPublication();
    check.That(fixture.View.Error.Length == 0, "An old failed publication set an error on the reopened surface.");

    var currentRead = await fixture.Main.Reads.NextAsync();
    var current = Fixture.Card("current after retired failure");
    currentRead.Reply([current]);
    await fixture.PublishNextAsync();
    check.That(fixture.View.Items.Count == 1 && ReferenceEquals(fixture.View.Items[0], current) && fixture.View.Error.Length == 0,
        "The reopened surface did not recover after retiring the old failure.");
}

static async Task RemainingVisibleOwner(Checks check)
{
    await using var fixture = new Fixture();
    var firstOwner = new object();
    var secondOwner = new object();
    fixture.View.SetVisible(firstOwner, true);
    var inFlight = await fixture.Main.Reads.NextAsync();
    fixture.View.SetVisible(secondOwner, true);
    fixture.View.SetVisible(firstOwner, false);
    var accepted = Fixture.Card("still visible for second owner");
    inFlight.Reply([accepted]);
    await fixture.PublishNextAsync();
    check.That(fixture.View.Items.Count == 1 && ReferenceEquals(fixture.View.Items[0], accepted),
        "Removing one owner retired work still owned by another visible surface.");

    // Owner changes coalesce a refresh into the bounded channel; complete that request.
    var followup = await fixture.Main.Reads.NextAsync();
    followup.Reply([accepted]);
    await fixture.PublishNextAsync();
    fixture.View.SetVisible(secondOwner, false);
    check.That(fixture.View.Items.Count == 1 && ReferenceEquals(fixture.View.Items[0], accepted),
        "The final hide discarded the last accepted cache.");
}

internal sealed class Checks
{
    internal List<AssertionResult> Assertions { get; } = [];
    internal void That(bool condition, string message) => Assertions.Add(new(condition, message));
}
internal sealed record AssertionResult(bool Passed, string Message);
internal sealed record ScenarioResult(string Name, bool Passed, IReadOnlyList<AssertionResult> Assertions);
internal sealed class ProbeStorageItem : IStorageItem;
