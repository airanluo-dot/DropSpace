# DropSpace UX Specification

## Experience goal

DropSpace should feel like a compact system utility, not a content dashboard. The common path is: capture silently, locate quickly, act once, disappear.

## Information architecture

- **Space**: user-curated staging area and default page.
- **Clipboard**: automatic chronological history with recording status.
- **Pinned**: saved filter spanning both sources.
- **Settings**: behavior, retention, appearance, and privacy.
- **Global search**: searches all collections while preserving source identity.
- **Quick Panel (V1.1)**: a second access surface over the same repository/search service.

Pinned is not a store and Quick Panel is not a page. This prevents four competing mental models.

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

## Quick Panel (V1.1)

- Opens centered in the active monitor work area, not under the pointer.
- Width 640, maximum height 520 effective pixels; adapts to small work areas.
- Search field receives focus; recent/pinned items appear before typing.
- Enter performs context-aware primary action; Ctrl+Enter opens details in main window.
- Esc dismisses without changing selection in the main window.
- Hotkey default is not `Alt+Space` because PowerToys Run commonly uses it. Recommended default: `Win+Shift+V`, configurable and conflict-tested.

## Settings

- Uses standard Windows settings rows with label, short description, and control.
- Dangerous clear actions live in Privacy, separated from ordinary toggles.
- “Exclude apps” (V1.1) includes the copy: “Best effort. Some clipboard changes cannot be attributed to an app.”
- Changes apply immediately unless a restart is technically required; required restart is stated before saving.

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

## Primary user flows

### Stage and move a file

Explorer → drag to Space → reference appears → later drag item to target app → source remains governed by target operation → record stays until user removes it.

### Recover copied text

Open Clipboard → search/browse → Copy → self-authored clipboard event is suppressed → use in target app.

### Resolve missing file

Open missing item → Locate/Replace Reference → picker returns new path → app confirms identity change → metadata and availability update.

### Pause for privacy

Tray or Clipboard status → Pause → listener stops persisting events → visible paused state → Resume explicitly.

### Clear history

Privacy/Clipboard menu → select range → show affected unpinned count → confirm broad deletion → transactional database delete → asynchronous cache cleanup.

## UX acceptance checks

- A first-time user can stage and retrieve a file without reading documentation.
- Space and Clipboard sources are distinguishable in every combined view.
- Paused recording state is visible in main window and tray menu.
- Removing a record never uses wording that implies deleting the source file.
- All primary flows work at 125%, 150%, and 200% scale with keyboard only.

