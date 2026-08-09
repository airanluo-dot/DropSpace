# DropSpace Test Plan

## Test strategy

Test pure policies heavily, OS adapters with integration harnesses, and a small number of critical end-to-end flows. Manual compatibility testing remains necessary for cross-process drag, tray, display/DPI, and clipboard behavior.

## Quality gates

- Build succeeds; every warning is reviewed, fixed, or documented.
- Unit and integration suites pass.
- Schema migration fixtures pass from every supported prior version.
- No high-severity privacy/security finding remains unexplained.
- Critical manual matrix has evidence for the release candidate.

## Unit tests

### Domain and classification

- Item capabilities by source/kind/status.
- URL, color, JSON/code/path hints with false-positive cases.
- Search normalization and ranking.
- Duplicate-window/fingerprint policy.
- Retention age/count/pin ordering.
- File availability state transitions.
- Clear-range date/time-zone boundaries.

### Safety

- Record removal has no source-delete dependency/call.
- Payload relative-path validation rejects absolute/traversal/device paths.
- Log redactor removes raw text, paths, tokens, and URL queries.
- Size/pixel/text limits handle integer boundaries.
- Pause generation invalidates in-flight commits.

### ViewModels

- Command enablement and concurrency.
- Empty/loading/error/missing states.
- Focus/selection preservation after item removal.
- Settings validation/revert on failure.
- Overlay state transitions for empty/drag/compact/expanded/dismiss/mode-transition paths, including interruption and count preservation.
- One hundred Reveal/Hide and Compact/Expanded cycles return to stable Hidden state without retained scheduler state.

## Integration tests

### SQLite and payloads

- Create/open/restart CRUD.
- Transaction rollback on injected failure.
- Foreign keys and cascade behavior.
- WAL/read concurrency if enabled.
- Retention and clear remove search projections.
- Logical delete plus eventual payload/thumbnail cleanup.
- Orphan reconciliation/quarantine.
- Disk-full, read-only, missing-payload behavior.

### Migrations

- Empty → current.
- Each released schema fixture → current.
- Interrupted/failing migration preserves original and backup.
- Newer schema refuses unsafe downgrade.
- Idempotent startup after migration completion.

### Clipboard adapter/pipeline

- Text and image standard formats.
- Multiple formats and precedence.
- Clipboard locked/transient retry.
- Rapid 1,000-event burst with bounded memory.
- Delayed content replaced before read.
- Self-copy loop suppression.
- Pause/resume and clear races.
- App exit with queued candidates.

### File/drag adapters

- Multi-file/folder incoming package.
- Unsupported virtual/mixed package partial success.
- Outgoing package advertises storage items and copy operation.
- Missing/permission/network errors map to correct domain state.

## UI automation

Automate only stable critical flows:

1. Launch and single-instance activation.
2. Navigate Space/Clipboard/Pinned/Settings by keyboard.
3. Add a prepared file through test hook or controlled drop harness, pin, restart, remove record, verify source exists.
4. Inject clipboard text, find it, copy again, verify no duplicate loop.
5. Pause/resume and clear history.
6. Missing-item Locate/Replace with controlled picker abstraction where automation allows.
7. Theme change and core automation properties.
8. Dynamic Island/Notch setting persistence and immediate state-preserving transition.

External drag-out remains a manual/adapter-assisted compatibility test because end-to-end pointer automation across processes can be brittle.

## Manual test matrix

### Clipboard

- Sources: Notepad, browser, Office, terminal/editor, screenshot tool, password manager if safely available, Remote Desktop scenario.
- Payloads: short/large Unicode text, URL, colors, JSON, transparent/large image, file list in V1.1.
- Behaviors: rapid copy, source closes, clipboard busy, DropSpace hidden, session restart.

### Drag and drop

- In from Explorer/Desktop/network/OneDrive/removable drive.
- Out to Explorer/Desktop, browser upload, Office, VS Code, Photoshop/available editor.
- Single/multiple, file/folder, missing during drag, cancellation, elevated boundary.
- Record actual target version and result.

### File missing/resilience

- Move, rename, delete, permissions, unplug USB, disconnect NAS, offline OneDrive.
- Verify Missing vs Unavailable language and Locate/Replace.

### Displays and DPI

- 1 and 2+ displays.
- 100%, 125%, 150%, 200%; mixed scale.
- Move window between displays; disconnect the last-used display.
- Taskbar on each edge and auto-hide.
- Drag into each monitor's top activation zone; verify the receiving monitor becomes active in Automatic mode.
- Switch Dynamic Island/Notch at every scale and migrate between mixed-scale displays without blur, offset, or crash.
- Verify ordinary fullscreen suppresses passive presentation while an explicit file drag may reveal the target.

### Accessibility

- Keyboard only and visible focus.
- Narrator/UI Automation names, roles, states, reading order.
- High contrast themes, color filters, reduced motion, 200% text.
- Touch target sanity where hardware is available.

### Lifecycle

- Close-to-tray, reopen, Exit, Explorer restart, Windows sign-out/restart.
- Second activation while visible/hidden.
- Crash/injected failure during database write and migration.

### Packaging

- Download only `DropSpace.exe` to Desktop/Downloads and launch as a standard user with no .NET/Windows App SDK runtime prerequisite.
- Confirm file icon and Product Name/File Description/Internal Name/Original Filename/version metadata.
- Replace `DropSpace.exe` and confirm `%LOCALAPPDATA%\DropSpace` database/settings/payload state remains.
- Clean MSIX install as standard user.
- Upgrade from previous released package/schema.
- Offline start, repair, uninstall/reinstall.
- Verify documented data retention on uninstall.
- Confirm unsigned Preview SmartScreen behavior is documented; never disable or bypass Defender policy.

## Performance tests

Reference device specifications are recorded with results.

- Cold/warm launch to interactive.
- Idle CPU and working set while tray-hidden.
- Clipboard burst queue depth, dropped events, processing latency.
- Search P50/P95 at 1k/10k/50k fixtures.
- Scroll with 500 image/text rows and thumbnail cancellation.
- Large image peak decoded memory.
- Database/payload size after retention cleanup.
- 100 Overlay reveal/dismiss and compact/expanded cycles with window count, working set, handlers, and Composition resource observations.
- Hidden/idle CPU and GPU observation: no continuous `CompositionTarget.Rendering`, DispatcherTimer, global input hook, or high-frequency loop.
- Interrupt a width/height/radius/content spring in both directions and prove every channel settles with no retained frame subscription.

No performance number is accepted without hardware, build configuration, dataset, and measurement method.

## Security and privacy tests

- Known secret strings never appear in logs, crash markers, notifications, or diagnostic export.
- Clear history removes canonical rows, search results, payloads, and thumbnails.
- Pause race prevents durable commit after completion.
- Malformed relative payload paths cannot escape root.
- Crafted URL schemes are not auto-opened.
- Huge/malformed inputs cannot cause unbounded allocation or queue growth.
- App exclusions, when introduced, are tested for false attribution and accompanied by limitation copy.

## Release evidence

For each phase/release retain:

- Build/test command and result.
- Supported OS/SDK/package versions.
- Manual matrix with pass/fail/known limitation.
- Migration fixture results.
- Performance measurements.
- Accessibility and privacy review notes.
- Clean workflow SHA, test totals/TRX, exact EXE/MSIX sizes, Authenticode status, SHA-256 manifest, smoke marker result, release URL, and final default-branch SHA.

The portable smoke harness must prove that the published single `DropSpace.exe` starts, initializes Windows App SDK and SQLite, creates/writes the user AppData tree, registers `AddClipboardFormatListener`, writes two unique system clipboard strings through the real Windows clipboard, observes `WM_CLIPBOARDUPDATE`, reads and persists them, verifies Pause/Resume and self-write suppression, removes its test rows, creates one native activation host per monitor, executes 100 real Overlay lifecycle/animation cycles within bounded HWND/GDI/USER/private-byte deltas with no retained frame subscription, redirects a second instance, and exits cleanly. A successful `dotnet publish` without this runtime probe is not release evidence.

Automation does not claim visual quality or Explorer pointer routing. Before Preview sign-off, a real Windows 11 desktop must still verify zero residual pixels in Hidden, Explorer/Desktop `CF_HDROP` entry and Drop, ordinary click-through beneath the activation host, Dynamic Island/Notch motion quality, drag-out, mixed-DPI monitors, and fullscreen behavior.
