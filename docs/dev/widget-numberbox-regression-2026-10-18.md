# Widget row/column input regression — 2026-10-18

Scope: local code repair only; no push, PR, merge, tag, deployment or release.
Baseline HEAD: `727d78547cc85f05a6aa5dba476a03320d5f78aa`.
Changes remain uncommitted for review.

## History and change

`8c791f93a02428c562ccfe0a22fb004537545374` removed numeric position controls.
Beta 3 (`1e858011b493c1bba10bb918192834fe29257ce5`) retained drag placement/movement,
drag back to the library for removal, and shape buttons for resizing. Beta 4
reintroduced row/column NumberBoxes in `d624782`.

Remove only the NumberBoxes, their container, value-change handlers, synchronization
fields, `RefreshPositionInputs`, and `MoveCoordinateAsync`. Retain Beta 4 drag/button
handling, latest-transaction resize placement, Undo ownership and asynchronous
feedback revision handling. Two obsolete comments are updated. No Core, settings
schema, localization resources, version or release configuration changes.

Align UX.md and the existing focused probe with the restored interaction. The probe
now calls the retained drag placement method instead of the removed numeric method.
Historical numeric-input probe records remain unchanged.

## Actual checks

The probe and targeted compilation each ran once, with no failures or retries:

- Source/history audit: Beta 3 contains no NumberBox; current production diff is
  exactly numeric-control removal plus two comments. All retained production logic
  (including Undo, drag handlers and queued resize/error ownership) is unchanged.
- `dotnet run --project scripts/widget-layout-intent-probe/WidgetLayoutIntentProbe.csproj --no-restore`:
  2 scenarios passed, 0 failed. Pending drag then resize retains the new position;
  queued collision then resize preserves the current position and suppresses stale
  feedback. Both assert Undo availability and the immediately prior committed snapshot.
- `dotnet build artifacts/local-iterations-2026-10-17/continuation/widgets-editor-compile/WidgetEditorCompile.csproj -c Release --no-restore`:
  targeted Windows-reference editor compilation passed, 0 warnings, 0 errors.
- `git diff --check`: passed before and after writing this record (Git reported normal LF/CRLF normalization notices).
- Read-only GitHub release list: latest published prerelease remains `v0.3.2-beta.4`,
  published `2026-10-17T21:03:20Z`; remote tag remains
  `053b6c29c476e9403176d6abb5df1be1da0011a4`.

Raw local logs and original production-file backups:
`artifacts/widget-numberbox-regression-2026-10-18/` (ignored).

## Coverage limits

Linux host: no native WinUI drag/click, visual/accessibility inspection, actual Undo
button invocation, or disk persistence verification. The probe uses real layout
methods, Core policy and NativeSettingsEditor serialization with a stub persistence
boundary. The targeted compile is not a complete App/XAML build or packaging run.
No full matrix, stress tests, CI dispatch or repeated historical checks were run.
Online assets were not downloaded again; no remote writes occurred, and the published
version remains unchanged.
