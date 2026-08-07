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
- Quick Panel cases are V1.1.

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

- Clean install as standard user.
- Upgrade from previous released package/schema.
- Offline start, repair, uninstall/reinstall.
- Verify documented data retention on uninstall.

## Performance tests

Reference device specifications are recorded with results.

- Cold/warm launch to interactive.
- Idle CPU and working set while tray-hidden.
- Clipboard burst queue depth, dropped events, processing latency.
- Search P50/P95 at 1k/10k/50k fixtures.
- Scroll with 500 image/text rows and thumbnail cancellation.
- Large image peak decoded memory.
- Database/payload size after retention cleanup.
- Quick Panel invocation latency in V1.1.

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

