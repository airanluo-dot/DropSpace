# DropSpace Feature Catalogue

This document is the behavior contract. MVP and V1.1 boundaries come from `PRODUCT.md`.

## Shared item behavior

### User behavior

Open, preview, copy, drag, pin/unpin, search, or remove a record.

### App behavior

Every item shows type, title, source collection, creation time, status, and available actions. Actions are capability-driven; an action is hidden or disabled when the payload cannot support it.

### States

- Normal: compact row/card with primary action and overflow menu.
- Selected: accent indicator and keyboard focus remain distinct.
- Loading: skeleton only for content that truly requires asynchronous loading.
- Error: inline status with retry or corrective action.
- Missing: file reference retained, destructive-looking actions avoided.
- Unsupported: metadata is retained only when safe and useful.

### Edge cases

Duplicate events, stale paths, unavailable drives, inaccessible payloads, large images/text, database read-only mode, and clipboard re-entrancy.

## Space

### User behavior

Drag one or more files/folders into the page; open, copy, drag out, pin, or remove them.

### App behavior

Store normalized references and metadata without copying source content. Validate existence lazily and before operations. Never interpret record removal as source deletion.

### States

- Empty: one short explanation and a real drop target.
- Drag over: page-level border and copy/link wording; no layout jump.
- Importing: per-batch progress only when metadata work is noticeable.
- Partial success: accepted items appear; rejected items are summarized.
- Missing: “File no longer exists” with Locate, Replace Reference, Remove.

### Edge cases

Mixed files/folders, shortcuts, duplicate paths, long paths, removable media, network drives, cloud placeholders, permission denial, symlinks/reparse points, and drag cancellation.

Manual text and URL intake into Space is V1.1. In MVP those types enter through Clipboard; this avoids inventing ambiguous “drop text versus paste command” behavior before the file-staging workflow is proven.

## Clipboard

### User behavior

Browse by date, search, preview, copy again, export an image, pin, delete, pause, or clear history.

### App behavior

Subscribe to clipboard change events. Snapshot supported formats off the UI thread through a bounded queue, assign a content fingerprint, suppress self-authored loops, enforce limits, and persist only accepted payloads. Collapse only adjacent identical snapshots (`A, A → A`); an intervening observation restores normal history (`A, B, A → A, B, A`).

### States

- Empty enabled: “Copied text and images will appear here.”
- Empty paused: strong paused status and Resume action.
- Capturing: no global spinner; new record appears after persistence.
- Skipped: local diagnostic/optional transient notice for too-large or inaccessible content.
- Error: recording can degrade to paused while Space remains usable.

### Edge cases

Bursts, delayed rendering, clipboard locked by another process, empty formats, multiple simultaneous formats, duplicate content, self-copy loop, huge payload, source owner unavailable, session lock, restart.

## Type presentation

### Text

- Plain preview, length, copy time.
- Size limit and truncation indicator; full payload retained only within policy.
- Monospace presentation when basic heuristics label code or JSON.

### URL

- Domain plus safe display URL.
- Open only after explicit action.
- No favicon network request in MVP.
- Explorer file, folder, multiple-item, and mixed file/folder copy capture as Clipboard references, separate from Temporary Space.
- User controls for image enable/byte/pixel limits and file enable/folder/single-byte/total-byte/item-count limits.

### Image

- Locally generated thumbnail, dimensions, size, timestamp.
- Copy again and Export As.
- Corrupt or missing cached image shows a recoverable placeholder.

### Color

- Recognize strict HEX and RGB patterns.
- Show swatch and conversions computed locally.
- Recognition never changes the original text payload.

### File/folder

- Name, path, kind, availability, and optional system thumbnail.
- Open, show in folder, copy path, drag out, locate/replace.

## Pinned

### User behavior

Pin from any item menu or keyboard command; browse all pinned items in one view.

### App behavior

Pinning changes retention state on the same item. It does not create a second item or copy a source file.

### States and edge cases

- Empty: explain pinning without a separate onboarding flow.
- Missing pinned file remains pinned until user resolves/removes it.
- Unpin returns the item to its original collection and retention rules.

## Search

### User behavior

Type a query, filter by source/type/status/pinned, open or act on results.

### App behavior

Debounce briefly, query indexed normalized fields, rank exact title/domain matches before body/path matches, and preserve source labels.

### States

- Empty query: recent items for the current collection.
- Loading: only after 150 ms to prevent flicker.
- No results: show active filters and Clear filters.
- Error: retain query and offer retry.

### Edge cases

Unicode, case folding, paths, punctuation, very long text, stale index after migration, and deleted items during results display.

## Top Overlay

### User behavior

In experimental Smart mode, a recognized Explorer/Desktop file drag reveals a temporary Dynamic Island below the top edge without a permanent activation window. Drop into Temporary Space or click Compact to expand recent items. Traditional top-edge and disabled automatic-wake modes remain available.

### App behavior

Keep the surface hidden when Temporary Space is empty, reveal from standard file drag events, remain Compact while items exist, and expose at most five recent items with Open, Pin, Remove Reference, drag-out, and Open DropSpace. It shares repository/use-case state with the main window and does not react to Clipboard item count.

### Edge cases

Cancelled drag, last-item dismissal interrupted by a new drag, rapid mode retargeting, full-screen apps, elevated integrity boundaries, multiple displays/DPI, display removal, remote desktop, and system Reduced Motion.

## Settings

### General

Close behavior, run in background explanation, tray availability, default-on per-user startup, launch page.

### Clipboard

Master toggle, retention age/count, image capture, size limits, duplicate policy, file capture (V1.1), exclusions (V1.1).

### Appearance

System/light/dark, System/Full/Reduced Dynamic Island motion, Automatic/Primary monitor, and material fallback. Mica is automatic preference, not a user performance promise.

### Privacy

Current recording state, pause/resume, data location, clear ranges, exclusion limitation, optional diagnostics export without payloads.

### States

Settings save immediately after validation. Failed saves revert the control and show an inline error.

## Tray

- Open DropSpace.
- Pause/Resume Clipboard with visible checked state.
- Clear clipboard history with confirmation for broad deletion.
- Exit, which stops recording and removes the icon.
- Recreate the icon after Explorer restarts.

## Window and lifecycle

- Single process/instance owns the database and clipboard listener.
- Close follows the explicit setting: hide to tray or exit.
- OS shutdown uses a bounded flush; uncommitted queue entries may be lost rather than blocking shutdown.
- Crash restart verifies schema and cache consistency before recording resumes.

## Notifications and feedback

- Prefer inline InfoBar or tray status over toast spam.
- Notify only for actionable failures: recording paused, migration recovery, hotkey conflict, storage full.
- Never display clipboard payload content in notifications by default.
