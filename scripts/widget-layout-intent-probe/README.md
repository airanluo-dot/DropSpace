# Widget layout intent probe

This portable .NET probe links the actual `WidgetEditorView.Layout.cs` methods,
`NativeSettingsEditor.cs`, and the Core project. It runs exactly two scenarios:

1. With only a 2×1 Clock at column 0, block the first settings publication, enter
   one-based Column 5, request a 1×1 Clock while the move is pending, then release
   persistence. The final Clock must be 1×1 at zero-based column 4.
2. With a 2×1 Clock at (0,0) and a 2×2 Calendar at (2,0), block the rejected
   one-based Column 3 move's no-op save, request a 1×1 Clock, then release
   persistence. The Clock must resize, the Calendar must remain unchanged, and
   the older move must not leave its error over the newer successful resize.

Both actual actions finish before state assertions. A failed first scenario does
not skip the second. JSON lines contain the first submitted snapshot, publication
history, action counts, final state, feedback transitions, and failures. The
process exits 1 if either scenario fails.

`MainViewModel` supplies only an in-memory persistence/publication boundary with
a bounded gate on the first call. The production `NativeSettingsEditor` owns the
serialization semaphore; Core owns layout validation and placement. A reused
single-thread managed dispatcher preserves UI continuation order. Visual refresh
hooks and unrelated constructor dependencies are lightweight fixtures. This does
not validate WinUI controls, input, accessibility, rendering, disk persistence,
or native application execution.

For a before/after comparison, `ProductionLayoutSource` can point at an absolute
path containing the original methods extracted verbatim from the baseline view,
wrapped in the same partial class. The same fixture supports both old and new
feedback-revision fields. The normal default links current production source.
Keep the baseline extraction, logs, hashes, and execution receipts in the ignored
task artifact directory; record actual attempts in the project-wide ledger.

Example commands for the caller that owns validation:

```sh
dotnet run --project scripts/widget-layout-intent-probe/WidgetLayoutIntentProbe.csproj -p:ProductionLayoutSource=/absolute/path/to/WidgetEditorView.Layout.before.cs
dotnet run --project scripts/widget-layout-intent-probe/WidgetLayoutIntentProbe.csproj
```

Preparation of this probe does not imply that either command has been run.
