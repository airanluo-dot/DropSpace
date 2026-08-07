# DropSpace Product Specification

Status: Draft v1, product and architecture phase  
Target: Windows 11 desktop, local-first  
Last reviewed: 2026-08-07

## One-sentence definition

DropSpace is a local Windows workspace that temporarily holds files and recently copied content so users can find, reuse, and move them without organizing them immediately.

## Product vision

Turn the awkward gap between “I have this now” and “I know where it belongs” into a fast, trustworthy workspace. DropSpace should feel like a native Windows utility: always close, quiet in the background, and obvious when opened.

## Problem statement

Windows users routinely lose context while moving files between windows, overwrite useful clipboard content, and clutter permanent folders with temporary material. Explorer solves permanent organization and the Windows clipboard solves only the current transfer. Neither provides a durable, searchable staging area that combines both workflows.

## Why the fusion is coherent

File staging and clipboard history share one job: preserve a temporary item until the user is ready to use it. The fusion is valuable when the two sources use one item model, one search, and one quick-access surface. It fails if users cannot tell what they deliberately saved from what the app recorded automatically.

Therefore:

- **Space** is deliberate and durable until removed.
- **Clipboard** is automatic, chronological, retention-limited, and privacy-sensitive.
- **Pinned** is a cross-source saved state, not a duplicate storage location.
- **Quick Panel** is an access surface, not another collection.

## Most valuable qualities

1. One interruption-free staging workflow for files and copied content.
2. Reliable external drag-out that makes the app more than a bookmark list.
3. Fast unified retrieval from a keyboard-first Quick Panel.
4. Clear local-only ownership and user-controlled retention.

## Main failure modes

- Shipping two unrelated products behind one navigation shell.
- Capturing sensitive clipboard data without visible, reliable controls.
- Treating paths as permanent and silently breaking file references.
- Building an oversized MVP whose tray, hotkey, drag-out, clipboard, and packaging risks arrive simultaneously.
- Looking like a web dashboard instead of a compact Windows utility.
- Promising reliable source-app exclusion when Windows only exposes best-effort clipboard ownership signals.

## Target users

- Students and knowledge workers moving text, links, screenshots, and files across apps.
- Creators moving assets between Explorer, browsers, editors, and design tools.
- Developers frequently reusing code, paths, JSON, URLs, and downloaded files.
- Anyone who wants temporary organization without committing to a folder or note system.

## Jobs to be done

- When I do not yet know where a file belongs, let me hold it safely without moving it.
- When I overwrite the clipboard, let me recover a recent useful item quickly.
- When I switch apps, let me retrieve or drag an item without reopening several windows.
- When an item matters, let me keep it beyond automatic cleanup.
- When recording feels unsafe, let me pause or clear it immediately.

## Core use cases

1. Drag files or folders into Space, close the window, and later drag them into Explorer or another compatible app.
2. Find a copied URL or text record and copy it again.
3. Preview a copied image and export it as a file.
4. Search Space and Clipboard from one query.
5. Pin an item without changing its source or duplicating its payload.
6. Resolve or remove a missing file reference.

## Value proposition

Compared with Explorer, DropSpace avoids premature organization. Compared with a clipboard manager, it handles deliberate file staging. Compared with note apps, it preserves native file transfer and clipboard semantics.

## Product principles

- Local first; no account or network dependency.
- References, not custody: never move or delete source files by default.
- Explicit source boundaries: manual and automatic content remain visibly distinct.
- Fast before clever: simple filters and indexed search before AI.
- Honest degradation: show missing, unavailable, unsupported, or too-large states.
- Privacy is a control surface, not a settings footnote.
- Keyboard and pointer are first-class peers.

## Final MVP scope

The first shippable MVP is deliberately smaller than the original list.

### Included

- Single-instance packaged WinUI 3 app shell.
- Space: file and folder references, multi-file drag-in, open, copy, remove record, pin, missing-state handling.
- Real external drag-out of file/folder references.
- Clipboard: event-driven plain text and image capture while the process is running.
- URL and basic color recognition as presentation metadata.
- Pinned filter/view across Space and Clipboard.
- Unified keyword search over stored metadata and text.
- SQLite persistence, schema migrations, and thumbnail/payload cache.
- Retention by age and count; pause, resume, clear-last-hour/today/all.
- Light/dark/system theme and accessible keyboard navigation.
- Tray menu and close-to-tray behavior.
- Logging, recoverable startup, basic migration backup, and MSIX packaging.

### Deferred to V1.1

- Quick Panel and global hotkey. They require separate window focus, multi-display positioning, conflict handling, and performance work.
- Clipboard file-list capture. Clipboard format and delayed-rendering behavior need broader compatibility testing.
- App exclusion UI. Source attribution is best-effort and must be validated before presenting it as a privacy guarantee.
- Rich code detection, HSL editing, favicon download, Explorer context-menu integration.
- Automatic startup toggle and update mechanism beyond installer-supported updates.
- UI automation suite breadth beyond critical smoke paths.

## V1.1

- Quick Panel with configurable hotkey and keyboard actions.
- Best-effort source-app labels and exclusions with explicit limitation copy.
- File clipboard history, duplicate controls, richer type filters.
- Manual Space intake for pasted/dropped text and URLs, after file staging semantics are stable.
- Startup preference, improved tray flyout, multi-monitor restore rules.
- Optional local-at-rest protection for image/text payloads after threat and usability validation.

## Future

- OCR and local content extraction.
- Optional AI actions behind explicit invocation and clear data-boundary consent.
- Semantic search as an optional index.
- Browser or Explorer integration only if the base app proves useful.
- Optional sync only as a separate, consented architecture.

## Non-goals

- Replacing Explorer, a permanent document library, or a password manager.
- Moving, deleting, backing up, versioning, or syncing source files.
- Claiming comprehensive sensitive-data detection.
- Running a hidden service when the user has exited the app.
- Cloud accounts, teams, mobile clients, OCR, AI, or browser extensions in MVP.

## Success measures

- Median open-to-item-action time under 2 seconds on the reference device.
- Quick return to idle after clipboard events; no polling loop.
- Zero source-file deletion or movement caused by record removal.
- Crash-free migration and recovery tests pass for every released schema.
- All primary actions are possible by keyboard and exposed to accessibility tools.
- Users can always identify whether an item came from Space or Clipboard.

## Open product assumptions

- Windows 11 is the initial supported OS; Windows 10 compatibility is not an MVP promise.
- The app remains running in the tray only when the user selected that close behavior.
- Clipboard capture stops when the process exits.
- Default clipboard retention: 30 days or 1,000 items, whichever limit is reached first; pinned items are exempt.
- Default image limit: 25 MB encoded payload or 50 megapixels; larger items are skipped with a local notice.
