# DropSpace Architecture

## Architecture goals

- Native Windows behavior and predictable resource use.
- One source of truth for items across Space, Clipboard, Pinned, Search, and future Quick Panel.
- Clear boundaries around OS APIs, persistence, payload files, and UI.
- Recoverable migrations and failures without silently losing source files or clipboard history.
- Enough extension points for future actions without introducing an AI abstraction in MVP.

## Technology baseline

- C# and current supported .NET for the chosen Windows App SDK release.
- WinUI 3 / Windows App SDK with one unpackaged self-contained single-file payload delivered by the recommended Inno Setup installer or as a portable EXE, plus a retained MSIX package.
- MVVM with `CommunityToolkit.Mvvm` for observable state and commands.
- `Microsoft.Data.Sqlite` with explicit SQL and repository mappings.
- Microsoft.Extensions dependency injection and logging abstractions only if compatible with the final template.

Exact versions are selected and recorded in Phase 0; do not write “latest” into project files without verification.

## Solution structure

```text
DropSpace.sln
src/
  DropSpace.App/             WinUI startup, views, resources, composition root
  DropSpace.Core/            Domain models, policies, interfaces, pure logic
  DropSpace.Infrastructure/  SQLite, file/cache, Windows and Win32 adapters
tests/
  DropSpace.Core.Tests/
  DropSpace.Infrastructure.Tests/
  DropSpace.App.Tests/       UI-facing integration and automation harness
docs/                        Optional future ADR/archive location
```

Three production projects are sufficient. Do not create a project per service or feature.

## Application layers

```text
Views -> ViewModels -> Use cases/domain policies -> Service interfaces
                                                -> Repositories
Infrastructure implements Windows, file-system, cache, and SQLite boundaries.
```

- Views own visual state and platform events that cannot be bound cleanly.
- ViewModels coordinate user intent, cancellation, and presentation models.
- Core owns item classification, retention, capability decisions, fingerprints, and search contracts.
- Infrastructure owns WinRT/Win32 calls, SQLite transactions, file IO, and clocks.

## MVVM rules

- No database, clipboard, shell, or file-system access from Views/ViewModels.
- Code-behind is allowed for title bars, drag event bridging, focus, window messages, and accessibility wiring.
- ViewModels expose immutable item presentation records where practical.
- Commands are asynchronous, cancellation-aware, and prevent unintended parallel execution.
- Global mutable service locators are prohibited.

## Domain model

Use composition with a single `DropItem` aggregate and type-specific payload records, not a deep inheritance tree.

`DropItem` owns identity and lifecycle:

- identity, source, kind, title, timestamps
- pin and retention state
- availability/status
- normalized searchable text
- metadata version
- optional payload reference

Typed payloads (`FileReference`, `TextPayload`, `ImagePayload`, `UrlMetadata`) are discriminated by `Kind`. Behavior is expressed through services and capability policies rather than virtual methods. This maps cleanly to SQLite, avoids nullable subclass tables, and lets one item surface serve all collections.

## Major services

### ClipboardCaptureService

- Subscribes/unsubscribes to event-based clipboard changes.
- Reads supported formats through a serialized bounded channel.
- Retries transient clipboard-busy errors with short bounded backoff.
- Tags self-authored writes to prevent feedback loops.
- Produces capture candidates; it does not write UI state directly.

### ClipboardIngestionPipeline

- Selects preferred format, enforces size/policy limits, computes fingerprint, classifies content, writes payload/cache, then commits the database record.
- A failed payload write must not create a normal database item.
- Queue capacity is bounded; overflow increments diagnostics and coalesces/skips safely.

### DragDropService

- Converts incoming `DataPackageView` storage items to file-reference candidates.
- Creates outgoing standard data packages for storage items.
- Exposes capability/result objects so ViewModels do not depend on platform data types.

### FileReferenceService

- Normalizes display/canonical paths without assuming case sensitivity rules beyond Windows semantics.
- Checks existence/permissions lazily and before actions.
- Resolves shortcuts only when explicitly needed; retains original shortcut reference.
- Replaces a reference transactionally and preserves audit timestamps.

### ThumbnailService

- Requests system thumbnails for files/folders and decodes clipboard images to bounded sizes.
- Uses memory LRU plus disk cache keyed by item/payload revision, scale, and theme-independent variant.
- Deduplicates concurrent requests and supports cancellation.

### SearchService

- Builds parameterized SQLite queries against normalized fields and optional FTS table after Phase 6 validation.
- MVP may begin with indexed `LIKE` prefix/contains queries if the 1,000-item target meets latency criteria.
- Returns stable result IDs and scores; UI resolves live item state.

### HotkeyService (V1.1)

- Wraps `RegisterHotKey`/`UnregisterHotKey`, detects conflicts, and routes `WM_HOTKEY`.
- Registration failure is a user-visible setting state, not a startup failure.

### WindowService

- Owns the main window and top Overlay windows, activation, bounds validation, per-monitor DPI/display placement, and foreground behavior.
- Coordinates single-instance activation redirection.

### Overlay services and state

- `OverlayStateMachine` in Core is the single lifecycle authority: `Hidden`, `DragApproaching`, `DragReady`, `Compact`, `Expanded`, `Dismissing`, and `ModeTransition`.
- `OverlayViewModel` projects that state over the existing `MainViewModel`/repository; it does not create a second Temporary Space store.
- `OverlayWindowService` creates one no-taskbar visual window and one independent native activation host per monitor, selects the active monitor, and rebuilds both after display-topology changes.
- `MonitorLayoutService` converts physical monitor bounds and effective DPI; `OverlayWindowInterop` contains all HWND styles, topmost/no-activate behavior, and shaped regions.
- `OleDragDropService` creates a visually imperceptible 1/255-uniform-alpha native activation HWND and registers a managed `IDropTarget` with `RegisterDragDrop`. Uniform alpha zero is deliberately avoided because Windows target discovery omits a fully transparent layered HWND. Idle it exposes a 960-DIP-wide, 12-physical-pixel top safety band with `HTCLIENT`; after `DragEnter` the same HWND expands to 840 × 144 DIP, remains topmost and owns that OLE operation through `Drop`/`DragLeave`, then contracts. Once `CF_HDROP` is accepted, the owner never rejects the final sample merely because an animated inner Ready rectangle moved. The visible shaped Overlay has a separate target for direct drops while Compact/Expanded. Both converge on `MainViewModel.AddPathsAsync`.
- The activation host uses tool-window/no-activate/layered styles and no `HTTRANSPARENT`/`WS_EX_TRANSPARENT`. It has no XAML, backdrop, DWM border, shadow, paint loop, taskbar entry, or Alt+Tab entry. The bounded edge band is the reliability/input trade-off required for cross-process OLE target discovery without polling or hooks.
- The visual Overlay HWND is genuinely hidden and assigned an empty region in `Hidden`; it is never reused as the activation strip.
- `OverlayMotionController` continuously integrates width, height, offset, both radii, opacity, content reveal, and drop feedback toward replaceable spring targets. `CompositionTarget.Rendering` is attached only while a target is unsettled and removed at rest.

### Clipboard notification and capture

- `ClipboardNotificationService` subclasses the stable main-window HWND and registers `AddClipboardFormatListener`; it converts `WM_CLIPBOARDUPDATE` plus `GetClipboardSequenceNumber` into a content-free notification.
- `ClipboardCaptureService` owns the bounded serialized channel, pause generation, self-write fingerprint, finite transient retry, WinRT snapshot read/normalization, repository commit, retention, and health metrics. `StorageItems` is selected before bitmap/text for Explorer copies; file and folder references use `Source=Clipboard`, never enumerate folder contents, and share the existing file-reference table without entering Temporary Space.
- Listener registration failure is an Error state rather than a false “Recording” state. Diagnostics expose registration, last notification time, observed/captured/failed/dropped counts without payload text or file paths.

### TrayService

- Wraps notification-area icon lifecycle, native menu commands, Explorer restart recovery, and teardown.
- Contains no business logic; commands call application use cases.

### SettingsService

- Typed validated settings with defaults and versioned migration.
- UI candidates are preflighted through the running Overlay before atomic persistence. Invalid/malformed UI preferences quarantine only `settings.json`, preserve valid non-UI preferences when possible, fall back to Dynamic Island/System motion/Automatic monitor, and never delete the SQLite database or payloads.
- `--reset-ui-settings` and `--safe-mode` reset only UI/Overlay preferences.
- Small preferences use app-local settings; complex policies can live in SQLite.
- Never stores clipboard payloads in settings.

### Installation and maintenance

- `DropSpaceSetup.exe` is produced by pinned Inno Setup 7.0.2 from the exact portable `DropSpace.exe` payload. Stable AppId `E11EC281-BCE7-4F98-8EEF-2387E202CF0F` and `UsePreviousAppDir` make later installers the same per-user application and preserve a custom path.
- A named maintenance event requests the normal disposal path; Setup/uninstall wait for Overlay, clipboard, tray, settings, SQLite and logging teardown instead of using forced process termination.
- Program files live below the selected install root; mutable state remains below `%LOCALAPPDATA%\DropSpace`. Normal uninstall preserves that data. Complete uninstall deletes only this exact app-owned root, install files, shortcuts and DropSpace registry keys; it never opens the database to discover paths.
- `StartupRegistrationService` owns the single per-user `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\DropSpace` value. Settings persistence applies/rolls back that external state transactionally, every launch reconciles moved portable paths, `--startup` starts hidden to tray, and uninstall removes only that value.

### RetentionService

- Deletes expired/unpinned records in bounded batches and schedules cache cleanup.
- Runs at startup after migration and at low-frequency safe checkpoints, not continuously.

## Repositories and unit of work

- `IItemRepository`: CRUD, page queries, pin/status updates, clear ranges.
- `ISearchRepository`: search projections only.
- `IMigrationRunner`: ordered transactional schema migrations.
- `IPayloadStore`: atomic write/read/delete/export for image or large-text payload files.
- `IFileReferenceService`: asynchronous local file inspection and availability checks behind the platform boundary.
- `ILocalStorageMetrics`: user-scoped storage location and asynchronous byte accounting without filesystem access from ViewModels.
- `IThumbnailCache`: replaceable derived cache.

Use short-lived connections and explicit transactions. Serialize writes through one application-owned queue to reduce lock contention; reads can use separate connections under WAL mode if verified.

## Database and storage

```text
%LOCALAPPDATA%/DropSpace/
  data/dropspace.db
  payloads/images/<sharded-id>.bin
  payloads/text/<sharded-id>.txt   only above inline threshold
  cache/thumbnails/<key>.img
  backups/pre-migration-<version>.db
  logs/
```

- Database stores metadata and small text.
- Clipboard image originals live as app-owned payload files; thumbnails are disposable cache.
- Source files are never copied into this structure merely because they are in Space.
- Writes use temp-file + atomic rename where supported.

## Dependency injection

- Compose services once in `App` startup.
- Singleton: clock, settings, window/tray coordination, capture listener.
- Scoped/unit-of-work: repository connection boundaries where useful.
- Transient: ViewModels and use-case handlers unless state must persist.
- Interfaces exist for OS/storage boundaries and meaningful substitution, not every class.

## Threading and async

- UI dispatcher is used only for observable/UI updates.
- Clipboard, database, hashing, image decode, and file metadata run off the UI thread.
- No `.Result`, `.Wait()`, or `async void` except platform event handlers that immediately delegate and contain exceptions.
- All long operations accept `CancellationToken`.
- Bounded channels protect memory during clipboard bursts and thumbnail scrolling.
- State updates carry item IDs/revisions to avoid stale completion overwriting current state.

## Caching

- Memory cache has item-count and byte budgets.
- Disk thumbnail cache is derived and can be completely rebuilt.
- Original clipboard image payload is not “cache”; retention deletion must remove it.
- File availability cache has a short TTL and is invalidated before operations.
- No `FileSystemWatcher` over arbitrary user folders in MVP.

## Logging and diagnostics

- Structured local logs with event IDs and rolling size limits.
- Default logs exclude clipboard content, full text, URLs with query strings, and full file paths; use item IDs and coarse error categories.
- Crash marker records startup stage/schema version, not payload.
- Diagnostics export is explicit and redacts user content.

## Error handling and recovery

- Boundary exceptions map to domain errors and user-safe messages.
- Clipboard failure pauses/degrades capture; Space remains operational.
- Database open/migration failure starts a recovery screen, not a blank database overwrite.
- Pre-migration backup is retained until the next successful launch.
- Corrupt derived thumbnails are deleted and regenerated.
- Disk-full errors stop ingestion before committing orphan metadata and present cleanup guidance.

## Future action extensibility

Define an `IItemAction` discovery model only when a second non-core action provider exists. Future AI actions consume an explicit user-selected snapshot and must declare network/data handling. MVP uses direct application use cases to avoid premature plugin architecture.

## Architecture acceptance criteria

- Core tests run without WinUI or Windows APIs.
- Record removal cannot invoke source-file deletion through any interface.
- Clipboard capture is event-driven and bounded.
- Every schema change has forward migration and failure test.
- Main window and Overlay share repositories and use cases, not duplicate stores.
- Hidden idle owns only an invisible event-driven activation surface and has no continuous frame timer.
- Logs contain no raw clipboard payload in automated redaction tests.

## Preview.5 drag ownership and projection serialization

The visual Overlay and passive activation host have exclusive input ownership. Hidden/Dismissing uses the bounded top-edge `DragActivationHost`. Stable Compact/Expanded hides that host and exposes the XAML `Surface` (`AllowDrop`) plus the root native OLE registration. This matters because WinUI content can be hosted by a descendant HWND: a successful `RegisterDragDrop` on the top-level HWND alone did not prove that `WindowFromPoint` over visible pixels reached that target. Compact uses the common DragReady geometry; Expanded preserves its geometry and projects `ExpandedDropActive` as an in-place highlight. Both call `MainViewModel.AddPathsAsync`.

Temporary Space mutations are authoritative in `MainViewModel`. After a repository mutation it increments `SpaceRevision` and publishes `SpaceProjectionChanged`. `OverlayViewModel` owns one `SerializedProjectionRefreshCoordinator`: concurrent requests are coalesced, repository load/apply pairs never overlap, stale results are discarded, and collection mutations run on the UI Dispatcher. `ProjectionCollection.SynchronizeById` performs identity-based incremental updates instead of concurrent `Clear/Add`. No view queries the repository as an independent mutation owner.

Windows Share activation is a separate input contract, not Clipboard capture. `ShareTargetActivationService` receives `StorageItems`, reports the Share lifecycle, dispatches to the existing main instance and calls the same `AddPathsAsync`. It never writes Clipboard History.
