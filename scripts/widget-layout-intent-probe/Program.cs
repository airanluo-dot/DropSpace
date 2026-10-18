using System.Text.Json;
using DropSpace.App.Services;
using DropSpace.App.Services.Notifications;
using DropSpace.App.ViewModels;
using DropSpace.App.Views.Settings;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using DropSpace.Core.Widgets;
using DropSpace.Infrastructure.Downloads;
using Microsoft.Extensions.Logging.Abstractions;
using NavigationProjectionProbe;

if (args.Length != 0) throw new ArgumentException("This probe runs exactly the two widget layout intent scenarios; no arguments are accepted.");

ProbeDispatcher.Run(async () =>
{
    var results = new List<ScenarioResult>();
    // Do not short-circuit: run both focused drag/resize scenarios.
    results.Add(await RunScenarioAsync("pending-drag-then-resize", 4,
        [new(NativeWidgetId.Clock, 0, 0, 2, 1)], expectedColumn: 4));
    results.Add(await RunScenarioAsync("queued-blocked-drag-then-resize", 4,
        [new(NativeWidgetId.Clock, 0, 0, 2, 1), new(NativeWidgetId.Calendar, 2, 0, 2, 2)], expectedColumn: 0, queueObstruction: true));
    foreach (var result in results) Console.WriteLine(JsonSerializer.Serialize(result));
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        scenarios = results.Count,
        passed = results.Count(result => result.Passed),
        failed = results.Count(result => !result.Passed),
        coverage = "Linked WidgetEditorView layout methods, NativeSettingsEditor serialization and Core layout policy; no WinUI input, rendering, storage or native execution."
    }));
    Environment.ExitCode = results.All(result => result.Passed) ? 0 : 1;
});

static async Task<ScenarioResult> RunScenarioAsync(string name, int column,
    WidgetPlacement[] placements, int expectedColumn, bool queueObstruction = false)
{
    var result = new ScenarioResult(name, column, placements);
    var layout = new WidgetLayout(placements, new(null, null, null));
    var settings = new AppSettings { Widgets = new() { Layout = layout } };
    var main = new MainViewModel(settings);
    var strings = IdentityAppStringLocalizer.Instance;
    var editor = new NativeSettingsEditor(main, strings, NullLogger<NativeSettingsEditor>.Instance,
        new NativeFolderPickerService(), new WindowsNotificationActivityService(), new DownloadManager());
    using var view = new WidgetEditorView(editor, strings);
    view.Select(NativeWidgetId.Clock);
    Task? obstruction = null;
    Task? move = null;
    Task? resize = null;
    try
    {
        // A preceding save moves Calendar into the target after drag preflight,
        // so the queued drag must reject against the transaction's current layout.
        if (queueObstruction)
            obstruction = editor.UpdateAsync(current => current with
            {
                Widgets = current.Widgets with
                {
                    Layout = new(current.Widgets.Layout.Expanded.Select(item =>
                        item.Id == NativeWidgetId.Calendar ? item with { Column = column } : item).ToArray(),
                        current.Widgets.Layout.Compact)
                }
            });
        else move = view.MoveToAsync(column, 0);
        try
        {
            await main.FirstSaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            result.FirstSaveEntered = true;
        }
        catch (Exception exception) { result.Failures.Add("First save barrier: " + exception); }

        // Execute the second actual action even if reaching the barrier failed;
        // assertions below only run after both requested operations are awaited.
        if (queueObstruction) move = view.MoveToAsync(column, 0);
        resize = view.ResizeSelectedAsync(new(1, 1));
        result.MovePendingBeforeRelease = !move!.IsCompleted;
        result.ResizePendingBeforeRelease = !resize.IsCompleted;
        result.SubmissionsBeforeRelease = main.Submitted.Count;
        result.PublicationsBeforeRelease = main.Published.Count;
        result.FirstSubmittedClock = main.Submitted.FirstOrDefault()?.Widgets.Layout.Expanded
            .Single(item => item.Id == NativeWidgetId.Clock);
        main.ReleaseFirstSave();
        await Task.WhenAll(obstruction ?? Task.CompletedTask, move!, resize).WaitAsync(TimeSpan.FromSeconds(5));
        result.BothActionsCompleted = true;
    }
    catch (Exception exception) { result.Failures.Add("Actions: " + exception); }
    finally
    {
        main.ReleaseFirstSave();
        // Retire pending work before observing final state or disposing the real
        // editor. Every persistence wait has its own five-second deadline.
        if (!result.BothActionsCompleted && move is not null && resize is not null)
        {
            try
            {
                await Task.WhenAll(obstruction ?? Task.CompletedTask, move!, resize).WaitAsync(TimeSpan.FromSeconds(5));
                result.BothActionsCompleted = true;
            }
            catch (Exception exception) { result.Failures.Add("Action retirement: " + exception); }
        }
        try { await editor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception exception) { result.Failures.Add("Editor disposal: " + exception); }
    }

    result.FinalLayout = main.Settings.Widgets.Layout.Expanded.ToArray();
    result.PublishedLayouts = main.Published.Select(snapshot => snapshot.Widgets.Layout.Expanded.ToArray()).ToArray();
    result.Error = view.Error;
    result.ErrorTransitions = view.ErrorTransitions.ToArray();
    result.MoveCalls = view.MoveCalls;
    result.ResizeCalls = view.ResizeCalls;
    var clock = result.FinalLayout.SingleOrDefault(item => item.Id == NativeWidgetId.Clock);
    Check(result.FirstSaveEntered && result.MovePendingBeforeRelease && result.ResizePendingBeforeRelease,
        "Both actions must overlap while the first settings publication is blocked.");
    Check(result.SubmissionsBeforeRelease == 1 && result.PublicationsBeforeRelease == 0,
        "The real NativeSettingsEditor queue must keep resize out of persistence until move publishes.");
    Check(result.MoveCalls == 1 && result.ResizeCalls == 1 && result.BothActionsCompleted,
        "Both production actions must run once and finish before assertions.");
    Check(main.Submitted.Count == (queueObstruction ? 3 : 2) && main.Published.Count == (queueObstruction ? 3 : 2),
        "All queued layout actions must reach and complete the settings boundary.");
    Check(result.FirstSubmittedClock is { ColumnSpan: 2, RowSpan: 1 } &&
          result.FirstSubmittedClock.Column == expectedColumn,
        "The first submitted snapshot must reflect the accepted drag or preceding Calendar transaction.");
    Check(clock is { ColumnSpan: 1, RowSpan: 1, Row: 0 } && clock.Column == expectedColumn,
        "Resize must use the latest committed location while applying the requested 1x1 size.");
    Check(result.FinalLayout.Where(item => item.Id != NativeWidgetId.Clock)
            .SequenceEqual(placements.Where(item => item.Id != NativeWidgetId.Clock)
                .Select(item => queueObstruction && item.Id == NativeWidgetId.Calendar ? item with { Column = column } : item)),
        "Drag and resize must preserve the calendar placement from the preceding transaction.");
    Check(string.IsNullOrEmpty(result.Error), "An older rejected move must not overwrite the successful newer resize feedback.");
    Check(view.PendingLayoutEdits == 0 && view.UndoEnabled,
        "Both layout operations must retire and retain an available successful Undo step.");
    Check(main.Published.Count >= 2 &&
          view.UndoSnapshot?.Expanded.SequenceEqual(main.Published[^2].Widgets.Layout.Expanded) == true,
        "Undo must retain the immediately preceding committed layout, not the stale pre-drag layout.");
    Check(!result.ErrorTransitions.Contains("WidgetNoRoom"),
        "A rejected older drag must not publish stale feedback after a newer resize intent.");
    return result;

    void Check(bool condition, string message)
    {
        if (!condition) result.Failures.Add(message);
    }
}

internal sealed class ScenarioResult(string scenario, int targetColumn, WidgetPlacement[] initialLayout)
{
    public string Scenario { get; } = scenario;
    public int TargetColumn { get; } = targetColumn;
    public WidgetPlacement[] InitialLayout { get; } = initialLayout;
    public WidgetPlacement[] FinalLayout { get; set; } = [];
    public WidgetPlacement[][] PublishedLayouts { get; set; } = [];
    public WidgetPlacement? FirstSubmittedClock { get; set; }
    public bool FirstSaveEntered { get; set; }
    public bool MovePendingBeforeRelease { get; set; }
    public bool ResizePendingBeforeRelease { get; set; }
    public int SubmissionsBeforeRelease { get; set; }
    public int PublicationsBeforeRelease { get; set; }
    public bool BothActionsCompleted { get; set; }
    public int MoveCalls { get; set; }
    public int ResizeCalls { get; set; }
    public string Error { get; set; } = string.Empty;
    public string[] ErrorTransitions { get; set; } = [];
    public List<string> Failures { get; } = [];
    public bool Passed => Failures.Count == 0;
}
