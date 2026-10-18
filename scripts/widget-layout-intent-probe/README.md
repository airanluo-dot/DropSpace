# Widget layout intent probe

This portable .NET probe links the actual `WidgetEditorView.Layout.cs` methods,
`NativeSettingsEditor.cs`, and the Core project. It runs exactly two scenarios:

1. With a 2×1 Clock at (0,0), hold the drag placement save to (4,0), request
   a 1×1 size, then release persistence. Resize must retain column 4.
2. Hold a preceding Calendar move from (2,0) to (4,0), queue a Clock drag to
   (4,0) and then a 1×1 resize. The drag passes preflight against the old layout
   but is rejected by the queued transaction. Resize must retain Clock at (0,0),
   preserve Calendar at (4,0), and suppress the older drag rejection feedback.

Both scenarios assert the latest committed Undo snapshot and enabled Undo state.
Both actions finish before assertions; a failed scenario does not skip the next.
JSON lines report publication history, action counts, feedback and failures.
The process exits 1 if either scenario fails.

The fixture replaces persistence with an in-memory boundary and a bounded first-save
barrier. Production serialization and Core layout policy run on a reused managed
single-thread dispatcher. Visual refresh and unrelated dependencies are stubs.
This does not validate native WinUI drag input, rendering, accessibility, the Undo
button click, disk persistence, or native application execution.

```sh
dotnet run --project scripts/widget-layout-intent-probe/WidgetLayoutIntentProbe.csproj
```

`ProductionLayoutSource` can override the linked layout partial. Historical numeric
input before/after evidence belongs to its original probe revision; those records
are preserved and are not evidence for the current drag scenarios.
