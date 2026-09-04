# DropSpace Product Specification

Status: v0.3 Preview product contract
Target: 64-bit Windows 10 version 1809 (Build 17763) and later desktop, including Windows 11; local-first
Last reviewed: 2026-08-27

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

- Local first; no account or content network dependency. The opt-out updater reads only public official GitHub Release metadata/assets.
- References, not custody: never move or delete source files by default.
- Explicit source boundaries: manual and automatic content remain visibly distinct.
- Fast before clever: simple filters and indexed search before AI.
- Honest degradation: show missing, unavailable, unsupported, or too-large states.
- Privacy is a control surface, not a settings footnote.
- Keyboard and pointer are first-class peers.
- Windows 10 is the minimum runtime baseline; Windows 11-only visuals and
  contracts are optional capabilities with explicit fallbacks.

## Final MVP scope

The first shippable MVP is deliberately smaller than the original list.

### Included

- Single-instance packaged WinUI 3 app shell.
- Space: file and folder references, multi-file drag-in, open, copy, remove record, pin, missing-state handling.
- Real external drag-out of file/folder references.
- Clipboard: event-driven plain text, image, and Explorer file/folder reference capture while the process is running.
- URL and basic color recognition as presentation metadata.
- Pinned filter/view across Space and Clipboard.
- Unified keyword search over stored metadata and text.
- SQLite persistence, schema migrations, and thumbnail/payload cache.
- Retention by age and count; pause, resume, clear-last-hour/today/all.
- Light/dark/system theme, System default/English/Simplified Chinese display language, and accessible keyboard navigation.
- Tray menu and close-to-tray behavior.
- Logging, recoverable startup, basic migration backup, and MSIX packaging.

### Added in v0.3.0-preview.2

- The existing Dynamic Island is the keyboard-first Quick Panel, opened by a configurable global hotkey (default `Win+Shift+Space`).
- Best-effort Smart Drag process exclusions include explicit limitation copy; Pause remains the reliable privacy control.
- Manual Space intake and Windows Share accept files, images, text, and URLs without writing Clipboard History.
- Real virtual-file Drop streams `FileGroupDescriptorW`/`FileContents` into confined staging with cancellation, limits, duplicate-safe names, and rollback.
- One Drop becomes one `DropBatchId`/`DropSessionId` group with expand/collapse, group drag, pin, and remove operations.
- Dynamic Island placement supports Automatic/Custom mode and original per-monitor X/Y DIP values that survive transient clamping and topology changes.

### Still deferred
- Rich code detection, HSL editing, favicon download, Explorer context-menu integration.
- Background update timers/services, forced updates, and portable self-replacement. v0.1.0 supports process-start/manual discovery, verified download, and installer-owned upgrade only.
- UI automation suite breadth beyond critical smoke paths.

## V1.1

- Further Quick Panel actions beyond the shared Open/Pin/Remove/drag-out surface.
- File clipboard history, duplicate controls, richer type filters.
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

- 64-bit Windows 10 version 1809 (Build 17763) or later is the minimum supported OS; Windows 11-only visuals and contracts are optional capabilities.
- The app remains running in the tray only when the user selected that close behavior.
- Clipboard capture stops when the process exits.
- Default clipboard retention: 30 days or 1,000 items, whichever limit is reached first; pinned items are exempt.
- Default image limit: 25 MB encoded payload or 50 megapixels; larger items are skipped with a local notice.

## v0.3.0-preview.9 product boundary

Preview.9 makes intake and item recovery safer without changing the local-first product boundary. Installer-only per-user Explorer and SendTo entries are direct executable integrations, while Portable stays registration-free. Quick Actions are capability-driven projections of the existing action registry, with four content profiles and three primary slots. Undo is a single short-lived inverse operation: it restores DropSpace metadata/state or pin state, and it never restores or changes an external source file.

## v0.3.0-preview.10 product boundary

Preview.10 makes Smart Drag fail closed until positive file-like OLE evidence is verified, hardens the Dynamic Island's native borderless HWND path across the Windows 10/11 baseline, and completes the source-safe Quick Action export flow. Image resize/convert/metadata actions use explicit parameters, dedicated user-writable exports, collision-safe output names, and actionable localized errors. No action mutates or replaces an external source file, and no Smart candidate becomes visible from pointer, accessibility, Explorer-surface, or drag-threshold evidence alone.

## v0.3.0-preview.14 product boundary

Preview.14 makes the Dynamic Island motion system explicit and channel-specific. Geometry morphs, surface opacity, content choreography, interaction feedback, and elevation use separate bounded profiles; reduced motion removes bounce, overshoot, noticeable scale, and unnecessary translation. Visual-only opacity/content/hover/press work may run on the Windows compositor, while UI-thread layout and the exact native OLE region remain authoritative. Windows 11 may use a bounded Desktop Acrylic backdrop for the transient island; Windows 10 Build 17763, unsupported systems, disabled advanced effects, and high contrast use the solid fallback. This is a presentation change only: Smart Drag remains fail-closed, the Dynamic Island remains the only overlay surface, hidden state owns no input, and source files/data/network contracts do not change.

## v0.3.0-preview.7 3.0 boundary

The 3.0 Preview keeps local-first storage while adding explicit Windows-to-Windows handoff and sharing. Quick Preview is bounded and uses native PDF/media surfaces only after explicit user action; Quick Actions never mutate source files; DropLink pairing uses bilateral ECDH/SAS/certificate confirmation and certificate pinning; text/URL handoff is separate from Clipboard History; cross-device clipboard is opt-in, pause-aware, and loop-guarded; Nearby links are private-LAN and expiring; Internet Share is client-encrypted, revocable, and unavailable without an explicitly configured HTTPS Worker. These features do not change the non-scope for native Apple/Android/Linux clients, cloud accounts, WebRTC, or automatic public sharing.

## v0.3.0-preview.8 compatibility boundary

Preview.8 lowers the runtime declaration to Windows 10 version 1809 while
keeping the 26100 compile-time SDK. Runtime OS/API probes select Windows 11
Mica/DWM enhancements and report optional Share/PDF/media capabilities; the
Windows 10 base visual and local drop/clipboard paths remain the supported
fallback. The [compatibility baseline](compatibility-baseline.md) is the
release evidence contract, not a claim that every historical OS/DPI/monitor
row has already passed.
