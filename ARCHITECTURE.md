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

## Windows compatibility boundary

The supported runtime baseline is 64-bit Windows 10 version 1809 (Build
17763) or later, including Windows 11. The app continues to compile against
the pinned Windows SDK Build Tools 10.0.26100.8249; compile-time API availability
is not treated as proof that an API exists on the current OS.

`DropSpace.Core.Compatibility` owns the platform-neutral policy constants,
capability states, and contracts (`IOsVersionPolicy`,
`IApiAvailabilityService`, `IRuntimeDependencyProbe`, and
`IWindowsCapabilityService`). `DropSpace.App` provides the Windows adapters:
it reads the real kernel build, probes the Windows App SDK runtime and optional
WinRT types, and gates Mica, modern DWM attributes, PDF/media preview, and the
Windows Share contract. `DropSpace.Infrastructure` uses the same minimum-build
policy when parsing update manifests. No platform capability check is allowed
to move business ownership out of Core or to create a second drop/persistence
path.

On Windows 10, the main window uses its solid theme visual, modern DWM corner
and border attributes are skipped, and optional APIs report an unavailable
capability while normal Clipboard, Dynamic Island, visible OLE drop, and
Classic/Smart Drag paths remain available. A direct Portable launch below
Build 17763 exits with a diagnostic marker; MSIX and Inno Setup also declare
the minimum so ordinary installation is blocked before launch. See
[compatibility-baseline.md](compatibility-baseline.md) for the required OS,
DPI, monitor, deployment, and evidence matrix.

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

### GlobalQuickPanelHotkeyService

- Owns a dedicated message thread around `RegisterHotKey`/`UnregisterHotKey` and dispatches `WM_HOTKEY` back to the UI queue.
- Uses configurable `Win+Shift+Space` by default; registration failure does not prevent pointer access to the Island.

### WindowService

- Owns the main window and top Overlay windows, activation, bounds validation, per-monitor DPI/display placement, and foreground behavior.
- Coordinates single-instance activation redirection.

### Overlay services and state

- `OverlayStateMachine` in Core is the single lifecycle authority: `Hidden`, `DragApproaching`, `DragReady`, `Compact`, `Expanded`, and `Dismissing`.
- `OverlayViewModel` projects that state over the existing `MainViewModel`/repository; it does not create a second Temporary Space store.
- `OverlayWindowService` creates one initially hidden no-taskbar visual window per monitor, selects the active monitor, and rebuilds surfaces after display-topology changes. A zero-sized, never-shown message recipient replaces the former activation-host dependency for display broadcasts.
- `MonitorLayoutService` converts physical monitor bounds and effective DPI; `DisplayIdentityService` maps each runtime HMONITOR/GDI name to a stable DisplayConfig target identity when available. `MonitorDescriptor.Id` is the persistent settings key and `Handle` is process-lifetime native state; `OverlayWindowInterop` contains all HWND styles, topmost/no-activate behavior, and shaped regions.
- `DragSessionDetector` is the default Smart candidate detector. A dedicated message thread registers documented out-of-context `EVENT_OBJECT_DRAGSTART/CANCEL/COMPLETE`, provider-supplied `EVENT_SYSTEM_DRAGDROPSTART/END`, and read-only low-level mouse/keyboard observers. Lossy pointer motion is coalesced independently from an unbounded reliable critical-signal lane; only post-shutdown critical write failures are diagnosed, and the reader preserves timestamps across both lanes so a pre-release threshold move cannot be overtaken. `DragIntentConfidence` and `PayloadConfidence` are independent: accessibility proves drag intent, never file payload. Pointer release enters `AwaitingOleCompletion`; a new pointer-down supersedes the prior session immediately and stale callbacks are session-gated.
- `OverlayWindowService` begins speculative reveal as soon as a session starts. A generic threshold candidate simultaneously creates one `EphemeralOleDragProbe`: a 144-physical-pixel, 1/255-alpha, `NOACTIVATE|TOOLWINDOW|TOPMOST` popup with a real center Region hole and a bounded lifetime. Cleanup uses `PostMessage`, owner-context work-queue fallback, and a forced watchdog; every path revokes OLE, destroys the HWND, removes registry ownership, and disposes timers. Placement is physical-monitor-aware and biased inward at edges/negative coordinates.
- `OleFileDataClassifier` is the single query/parse authority for the probe, Classic host, and visible Island. Classification exposes `IsFileLikeEvidence`, `CanAcceptNow`, and `CanMaterialize`. Shell data prefers `SHCreateShellItemArrayFromDataObject` and uses bounded CIDA only as fallback. `VirtualFileMaterializer` starts async-capability ownership inside Drop, returns the OLE callback promptly, then copies `FileGroupDescriptorW`/`FileContents` in bounded yielding chunks on the supplying COM apartment into a confined staging batch. Imported bytes move into app-owned payload storage so record removal reclaims them; failure rolls back the batch.
- The legacy 1/255-alpha, 960-DIP × 12-physical-pixel `HTCLIENT` activation host remains only for user-selected `ClassicTopEdge` mode. `Disabled` creates neither the detector nor classic host. Mode changes create/destroy native resources immediately.
- `OverlayMotionController` continuously integrates Dynamic Island width, height, offset, radii, opacity, shadow, content reveal, and drop feedback toward replaceable spring targets. `OverlayVisualPhase` distinguishes Invisible/Entering/Visible/Exiting/Reversing; hide waits for opacity and geometry settlement. Settings schema 9 stores the global hotkey, best-effort exclusions, and per-monitor `OverlayPlacements`; legacy schema-8 global placement fields are deserialized only for one-time best-effort migration. Transient clamping never mutates saved coordinates, and the no-activate Adjust Position interaction suppresses Smart candidate creation until commit or Escape.

### Clipboard notification and capture

- `ClipboardNotificationService` subclasses the stable main-window HWND and registers `AddClipboardFormatListener`; it converts `WM_CLIPBOARDUPDATE` plus `GetClipboardSequenceNumber` into a content-free notification.
- `ClipboardCaptureService` owns the bounded serialized channel, pause generation, self-write fingerprint, finite transient retry, WinRT snapshot read/normalization, repository commit, retention, and health metrics. `ConsecutiveClipboardCaptureCoordinator` atomically compares the current canonical fingerprint with the immediately previous observed and successfully persisted identities: only an adjacent successful repeat is suppressed, while policy-rejected/intervening observations end the run and repository failures remain retryable. Clear History and Resume reset the process-local state. `StorageItems` is selected before bitmap/text for Explorer copies; file and folder references use `Source=Clipboard`, never enumerate folder contents, and share the existing file-reference table without entering Temporary Space.
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

### Localization

- `AppSettings` schema v9 persists language plus Quick Panel, exclusion, and per-monitor placement preferences; legacy settings migrate with safe defaults and best-effort active-monitor mapping.
- `AppLanguageService` applies the preference before the main window, Dynamic Island, tray, and services are created. System reads the Windows display-language preference, selects `zh-CN` for `zh-*`, and uses the shipped `en-US` base resource set for every other language.
- `Strings/en-US/Resources.resw` is the complete base resource set; `Strings/zh-CN/Resources.resw` has an exact matching key set. Dependency-object XAML uses `XamlResourceOverride.Uid`, which reapplies every supported property and automation name from the same explicit localizer context instead of WinUI's implicit `x:Uid` lookup; `Window` roots use the same override directly after XAML initialization, while the `TitleBar` waits for `Loaded` because it needs a `XamlRoot`. Imperative UI, service status, error prompts, and update feedback use the narrow Core `IAppStringLocalizer` contract.
- `App` registers the custom XAML attached property in its constructor before any page is parsed, as required by WinUI; its provider is a `DependencyObject` service type with static attached-property accessors, and the app then initializes the resource-backed localizer before creating the first window.
- Portable/unpackaged publishing stages `Strings`, XAML, and assets while excluding the MSIX manifest (so it keeps the unpackaged `Application` root), regenerates a single packaging-free `DropSpace.resources.pri` with MakePri, bundles it into the self-extracting EXE, and opens it only through an explicit `ResourceManager` context. The non-default filename avoids replacing WinUI's framework resource index while preserving the single-EXE distribution contract.
- The portable app does not call `ApplicationLanguages.PrimaryLanguageOverride`: that Windows App SDK API is unsupported for unpackaged processes. The app-owned resource context therefore supplies both the selected XAML surface and imperative text after startup, while native tray and service strings use the same localizer.
- Language selection is deliberately startup-scoped: Settings persists the next choice and presents a restart-required confirmation instead of mutating a live visual tree, tray menu, or resource context midway through a process.

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
- Smart/Disabled Hidden idle owns no top-edge or OLE target window and has no continuous frame timer. Classic mode's disclosed compatibility host is the only exception.

## v0.2.0 → v0.3 Smart drag evolution

v0.2 intentionally chose false negatives over false positives. Explorer/Desktop file-item classification and system drag thresholds were the bounded fallback because accessibility providers are not required to raise every Drag event; third-party and virtual-file providers were not inferred from mouse movement alone. Accessibility/mouse signals only revealed a temporary target and `CF_HDROP` was the final content boundary. Escape, pointer release, accessibility completion, OLE completion and a 30-second watchdog converged on one idempotent session close. No Explorer injection, Shell modification, undocumented Drop Tray state, input suppression, cursor polling, permanent render loop or source-path diagnostic was used.

v0.3 revises the conservative source gate without weakening the content boundary. Unknown pointer origins may form only a generic threshold candidate and therefore require the ephemeral OLE probe; documented accessibility drag-start evidence may take the strong fast path without provider-name rules. Real `IDataObject` format evidence is authoritative. `CF_HDROP` and resolvable Shell IDLists are accepted, virtual descriptors are recognized but remain unsupported until streaming materialization ships, and non-file/timeout results cancel speculative reveal.

Preview.2 corrected COM ownership but still allowed one exact UIA item hit-test to become an effective gate. Preview.3 separates Shell-surface evidence from exact-item evidence and adds documented object drag events carrying the accessibility source HWND. The event callback performs no classification; the serialized worker validates the HWND as Explorer/Desktop and promotes the retained press surface when necessary. `ElementFromPoint` remains the strict mouse-only fallback and now walks at most sixteen ancestors. Recognition/rejection/COM-failure/object-event counters are path-free diagnostics.

Preview.3 also removes the split placement coordinate system. DragApproaching/DragReady no longer use a private offset that Compact, Expanded, Dismissing, or fullscreen suppression immediately discard. One policy derives the physical Drop Tray offset from monitor scale, and the enlarged fixed host contains the complete surface while HRGN hit testing remains limited to visible geometry.
- Logs contain no raw clipboard payload in automated redaction tests.

## v0.3.0-preview.3 targeted hardening

- Critical Smart Drag lifecycle signals use an unbounded reliable queue; only `PointerMoved` is allowed to coalesce in a one-slot lossy lane, and timestamp merge order remains shared across both lanes.
- `OverlayPlacements` is the schema-9 per-monitor source of truth. Missing entries and `Automatic` entries resolve through the automatic policy; only `Custom` entries project saved DIP coordinates, and runtime clamp never writes back.
- Display settings keys use a normalized, hashed DisplayConfig target device path. HMONITOR values remain runtime handles; legacy schema-8 HMONITOR keys are mapped only when the current enumeration provides a best-effort match.
- Settings can arm direct Dynamic Island placement editing on the selected monitor. The fixed visual host accepts pointer capture, converts physical deltas to DIP, suppresses Smart Drag/Classic activation interference, commits once on release, and restores the prior position on Escape.

## Drag ownership and projection serialization

The visual Overlay and any wake mechanism have exclusive input ownership. Smart/Disabled Hidden and Dismissing states own no top-edge target; Classic alone uses the disclosed bounded `DragActivationHost`. Stable Compact/Expanded disables that Classic host and exposes the XAML `Surface` (`AllowDrop`) plus the temporary root native OLE registration. This matters because WinUI content can be hosted by a descendant HWND: a successful `RegisterDragDrop` on the top-level HWND alone did not prove that `WindowFromPoint` over visible pixels reached that target. Compact uses the common DragReady geometry; Expanded preserves its geometry and projects `ExpandedDropActive` as an in-place highlight. Both call `MainViewModel.AddPathsAsync`.

Temporary Space mutations are authoritative in `MainViewModel`. After a repository mutation it increments `SpaceRevision` and publishes `SpaceProjectionChanged`. `OverlayViewModel` owns one `SerializedProjectionRefreshCoordinator`: concurrent requests are coalesced, repository load/apply pairs never overlap, stale results are discarded, and collection mutations run on the UI Dispatcher. `ProjectionCollection.SynchronizeById` performs identity-based incremental updates instead of concurrent `Clear/Add`. No view queries the repository as an independent mutation owner.

## Stable update architecture

`IUpdateSource`, `IUpdateDownloader`, `IUpdateVerifier`, `ITrustedUpdateVerifier`, `IUpdateInstallerLauncher`, and `IDeploymentModeService` isolate the network-to-execution boundary from `MainViewModel`. `ReleaseVersion` is the single SemVer ordering model used by channel selection and mirrored by `scripts/ReleaseVersion.ps1` for EXE, Inno, MSIX, identity and release automation. Stable filters prereleases; Preview retains both; both require candidate > current.

`ResilientUpdateSource` merges at most 20 releases from each schema-v1 official website replica so a successful but stale static response cannot hide a newer release, then falls back to the official GitHub REST API only when the website replicas fail. Each source is bounded and the website contract accepts only exact official GitHub release/tag/asset identities. `UpdateManifestParser` accepts one bounded exact-schema manifest, fixed executable names, official same-release GitHub asset URLs, exact channel/tag/version metadata, and no remote executable URL field. `HttpUpdateDownloader` streams into `%LOCALAPPDATA%\DropSpace\Updates\<version>\*.download`, computes SHA-256 while copying, verifies size/hash, and atomically renames. Install re-verifies the frozen file. Inno `/UPDATE` requests the existing maintenance handshake and restarts only after success; Portable never invokes Inno and Package/MSIX remains Windows-managed. WinVerifyTrust plus an exact signer allow-list gates future unattended installation.

Windows Share activation is a separate input contract, not Clipboard capture. `ShareTargetActivationService` receives `StorageItems`, reports the Share lifecycle, dispatches to the existing main instance and calls the same `AddPathsAsync`. It never writes Clipboard History.

## v0.3.0-preview.7 architecture additions

Preview providers, item actions, bilateral DropLink pairing/transfer, explicit text/URL handoff, clipboard pause barriers, and share servers are documented in [docs/architecture](docs/architecture). The application layer owns lifecycle and user consent; infrastructure owns HTTPS/mDNS/DNS-SD/DPAPI/R2 adapters; Core owns manifest, limits, handoff policies, crypto-independent domain contracts, and loop policies. The `SqliteDatabase` migration is forward-only from schema 1 to schema 2. PDF rasterization is an App-owned Windows API boundary, and the Worker remains a source-only operator deployment.
