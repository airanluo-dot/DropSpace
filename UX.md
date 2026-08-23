# DropSpace UX Specification

## Experience goal

DropSpace should feel like a compact system utility, not a content dashboard. The common path is: capture silently, locate quickly, act once, disappear.

## Information architecture

- **Space**: user-curated staging area and default page.
- **Clipboard**: automatic chronological history with recording status.
- **Pinned**: saved filter spanning both sources.
- **Settings**: behavior, retention, appearance, and privacy.
- **Global search**: searches all collections while preserving source identity.
- **Top Overlay**: one Dynamic Island over the same Temporary Space state and actions.

Pinned is not a store and the Overlay is not a second product or page. Clipboard items never make the Overlay persist.

## Updates

Settings exposes automatic check/download toggles, Stable/Preview channel, current version, last check, state, manual check, download, install/open-location, and release-notes actions. Automatic checking belongs to the process lifetime and runs at most once after normal Tray/Clipboard/Overlay/database startup; reopening a window never checks again. A failed startup check is quiet and visible only as status. Manual checks can repeat and share an in-flight request.

Stable accepts only final releases. Preview accepts both release kinds and chooses the highest SemVer above the running build, so a Preview user receives a newer Stable and never downgrades after switching channels. Unsigned builds disable unattended installation. Portable shows a verified-download workflow; Package/MSIX says Windows manages updates.

## Main window

- Default size: 980 × 680 effective pixels; minimum 720 × 520.
- Restores last normal bounds only when they intersect a current display work area.
- Custom title bar contains app identity, drag region, search entry, and caption buttons.
- Left `NavigationView` contains Space, Clipboard, Pinned; Settings is footer-aligned.
- Content header contains page title, item count/status, view/filter controls, and one contextual action.
- List is the default density; an image-friendly grid may follow after MVP evidence.

## Navigation

- Selection persists per session.
- Back returns from detail/preview, not between top-level pages.
- Ctrl+1 Space, Ctrl+2 Clipboard, Ctrl+3 Pinned, Ctrl+, Settings.
- Search results open in place and keep the query when returning.

## Space

- Whole content well accepts drops, but interactive controls do not.
- Empty state teaches exactly one action: drag files or folders here.
- A new batch appears together when validation completes.
- Default primary action: Open. Folder opens in Explorer; file uses system association.
- Remove is named “Remove from DropSpace”; no source-delete command exists.
- Missing items retain title/path context and expose Locate, Replace Reference, Remove.

## Clipboard

- Top status strip always says Recording, Paused, or Error.
- Items group into Today, Yesterday, Earlier without hiding exact timestamps from accessibility or details.
- Clicking text selects the row; explicit Copy is the primary action to avoid accidental clipboard loops.
- Images open a local preview dialog/page with Copy and Export.
- Clear menu offers Last hour, Today, All; All requires confirmation and states that pinned items are kept by default.

## Pinned

- Same item components and actions as source pages.
- Every item displays a small Space or Clipboard source label.
- Unpin does not remove the item.

## Search

- Ctrl+F focuses global search; Esc first clears query, second returns focus.
- Query runs after a short debounce and Enter acts on the selected result.
- Result rows highlight matched title/domain fragments without altering accessible names.
- Filters: All, Space, Clipboard; type; pinned; available/missing.
- Search never indexes bytes inside arbitrary files in MVP.

## Top Overlay

- With zero Temporary Space items and no drag, Smart mode leaves every visual Overlay hidden with an empty region and no registered OLE target. It does not create a top-edge input window. Exact Explorer/Desktop item evidence and strong accessibility drag-start evidence take the fast path. An unknown/non-exact press crossing the Windows drag threshold begins speculative reveal about 76 physical pixels below the edge and concurrently creates one 60 ms hollow local OLE verification ring. File evidence commits the matching reveal; non-file/timeout reverses it. Traditional top-edge mode restores the former 1/255-alpha 12-physical-pixel compatibility band only when the user explicitly selects it.
- A valid storage-item drag enters `DragApproaching`, grows to `DragReady`, and states that dropping adds references without moving originals.
- A successful drop becomes Compact. One item shows a short title; several show a count. Clipboard captures do not affect visibility.
- Clicking Compact opens a bounded Expanded surface with up to five recent items, Open, Pin, Remove Reference, external drag-out, and Open DropSpace.
- Removing the last Temporary item enters an interruptible dismissal and returns to Hidden. A new drag or item can reverse the target before dismissal completes.
- Dynamic Island has an 8-DIP top gap and full corners. Its Compact, Drop Ready and Expanded states morph without recreating business state.

## Settings

- Uses standard Windows settings rows with label, short description, and control.
- Dangerous clear actions live in Privacy, separated from ordinary toggles.
- “Exclude apps” (V1.1) includes the copy: “Best effort. Some clipboard changes cannot be attributed to an app.”
- Changes apply immediately unless a restart is technically required; required restart is stated before saving.
- Top interface settings provide Smart/Traditional/Disabled file-drag wake, System/Full/Reduced motion, Automatic/Primary monitor, and display language: System default, English, or Simplified Chinese. The visual surface is always Dynamic Island. System motion follows Windows `UISettings.AnimationsEnabled`; System language uses the Windows display language, maps Chinese to Simplified Chinese, and falls back to English for the other shipped-language cases. The language selection is announced to assistive technology and takes effect after restart.

## System tray

- Left click opens/activates the main window.
- Right click opens a native menu: Open, Pause/Resume, Clear history, Exit.
- Closing to tray shows a one-time explanation; it never repeats unless reset.
- Exit is final and stops clipboard capture.

## Context menus

Order: primary action; Copy/Open variants; Pin; Locate/Replace when relevant; Remove last. Use standard icons only for familiar actions. Keyboard invocation opens at focused item.

## Drag and drop

### Drag in

1. On enter, validate advertised formats without reading full payloads.
2. Show a page-level target and action wording.
3. On drop, read storage items, normalize references, and create one batch.
4. Report accepted and rejected counts without blocking successful items.

The main Space well continues to use WinUI `StorageItems`; every native target shares one classifier for `CF_HDROP`, Shell IDLists and virtual-file descriptors. File-system paths and Shell items that resolve to paths converge on `MainViewModel.AddPathsAsync`; Preview.1 recognizes but does not materialize virtual-only data. On multiple displays, the candidate/host receiving the drag becomes the active Overlay display unless Primary is selected.

### Drag out

1. Pointer movement crosses drag threshold.
2. App builds a standard data package containing storage items.
3. Windows owns the cross-process drag loop.
4. UI reports completion/cancellation; the stored reference remains unchanged.

Copy is the advertised operation. DropSpace never claims the target moved the source unless the system result proves it.

## Keyboard model

- Tab follows visual order; arrow keys move within item lists.
- Enter primary action; Space selects; Ctrl+C copies selected item/payload; Delete opens remove confirmation only when ambiguity exists.
- Ctrl+P pin/unpin, Ctrl+L focus search alias, F5 refresh availability.
- Access keys are added to menus and settings labels.
- No keyboard shortcut acts on a hidden or stale selection.

## Mouse and touch

- Single click selects; double click opens files/folders only.
- Hover reveals secondary actions without making them mouse-only.
- Minimum pointer target 32 × 32 effective pixels; primary touch targets aim for 40 × 40.
- Horizontal scrolling is avoided in standard window sizes.

## Preview and details

- Use a right-side details pane on wide windows and a full content page on narrow windows.
- Text preview is selectable but not editable.
- URLs show full destination before Open.
- File details show path, availability, source, dates, and safe actions.
- Image preview loads a display-sized decode rather than the full bitmap when possible.

## Window behavior

- Main window activation redirects to the existing instance.
- Hidden-to-tray is distinct from minimized.
- Modal dialogs are limited to destructive clear operations, unrecoverable migration recovery, and file replacement confirmation.
- Theme, scale, display changes update without requiring relaunch where supported.
- Closing the main window may hide it to the tray; it does not stop the Overlay or clipboard listener. Only Exit DropSpace ends the process.
- Ordinary visible, uncloaked fullscreen application windows suppress non-drag Overlay presentation. Desktop/Shell classes (`Progman`, `WorkerW`, taskbars), shell identity, hidden/cloaked/iconic/tool windows never count as fullscreen. An explicit storage-item drag into the activation zone is allowed to reveal the target.

## Primary user flows

### Stage and move a file

Explorer → drag to Space → reference appears → later drag item to target app → source remains governed by target operation → record stays until user removes it.

### Recover copied text

Open Clipboard → search/browse → Copy → self-authored clipboard event is suppressed → use in target app.

### Resolve missing file

Open missing item → Locate/Replace Reference → picker returns new path → app confirms identity change → metadata and availability update.

### Pause for privacy

Tray or Clipboard status → Pause → listener stops persisting events → visible paused state → Resume explicitly.

Explorer Copy → `StorageItems` snapshot → configurable item/byte/folder policy → Clipboard file references persisted with `Source=Clipboard` → Copy again emits standard storage items and self-write suppression prevents an immediate duplicate.

### Clear history

Privacy/Clipboard menu → select range → show affected unpinned count → confirm broad deletion → transactional database delete → asynchronous cache cleanup.

## UX acceptance checks

- A first-time user can stage and retrieve a file without reading documentation.
- Space and Clipboard sources are distinguishable in every combined view.
- Paused recording state is visible in main window and tray menu.
- Removing a record never uses wording that implies deleting the source file.
- All primary flows work at 125%, 150%, and 200% scale with keyboard only.

## Drop Tray compatibility and visible drop

When Compact is visible, its complete black surface is a direct file target: valid DragEnter morphs continuously to DragReady, DragLeave reverses to Compact and Drop gives a short confirmation before Compact. When Expanded is visible, its geometry remains Expanded; a contained highlight says “放到 DropSpace”, the list updates immediately after Drop, and the panel remains open.

Settings explains that Windows 11 Drop Tray can own the same top edge. “打开 Windows Drop Tray 设置” opens System → Multitasking. DropSpace never guesses the toggle state. Trusted identity builds additionally state that Windows Share is registered; unsigned/portable deployments state that Share integration is unavailable without weakening the two native drag paths.

The Smart verification ring is never a visible product surface: it must not flash, focus, enter taskbar/Alt+Tab, block the source loop, or remain after its 60 ms budget. A brief forbidden-cursor change from `DROPEFFECT_NONE`, third-party provider coverage, mixed-DPI placement, and false-reveal latency remain explicit hands-on Preview checks rather than automated claims.
