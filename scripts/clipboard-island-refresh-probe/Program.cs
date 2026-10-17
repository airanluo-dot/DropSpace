using System.Text.Json;
using ClipboardIslandRefreshProbe;
using NavigationProjectionProbe;

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
