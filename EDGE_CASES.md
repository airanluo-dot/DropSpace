# DropSpace Edge-Case Catalogue

Each case defines the expected safe behavior. “Skip” means no durable normal item is created.

## Files and folders

| Case | Expected behavior |
|---|---|
| File moved/renamed | Mark Missing on next check/action; offer Locate, Replace Reference, Remove. Do not guess silently. |
| File deleted | Same Missing state; record remains until removed. |
| Folder moved/deleted | Same behavior; do not recursively scan for a match. |
| Permission denied | Status Unavailable, distinguish from Missing, allow retry/copy path. |
| Network drive/NAS offline | Timeout/cancel metadata work; show Unavailable/Offline without blocking list rendering. |
| USB removed | Update lazily to Unavailable; recover automatically when the same path becomes available. |
| Drive letter changed | Do not remap automatically in MVP; Locate/Replace. |
| OneDrive placeholder | Keep reference; opening may trigger provider hydration. Do not force hydration for thumbnails/search. |
| Offline file | Show provider/system availability where obtainable; operations may fail recoverably. |
| Shortcut `.lnk` | Store shortcut reference; show target only as best-effort metadata; drag/open the shortcut unless user explicitly chooses target. |
| Symbolic link/reparse point | Treat as reference; avoid recursive traversal and do not claim target identity. |
| Very long path | Use long-path-capable APIs; UI middle-ellipsizes display only, never stored path. |
| Special/Unicode characters | Preserve exact display path; parameterize database queries; test RTL/combining characters. |
| Reserved names/device paths | Reject unsupported/non-file-system references safely. |
| Duplicate path in Space | Focus existing record or ask to add another only if separate history is useful. |
| Same file through different spelling | Normalize best effort; never merge solely on unreliable case/string rules across providers. |
| File changed in place | Refresh metadata/thumbnail by revision; reference remains valid. |
| Huge folder | Do not enumerate contents; store only the folder reference. |
| Mixed drop batch | Add supported items; summarize rejected/unavailable entries. |
| Virtual file from mail/browser | Accept only if a stable storage item/path is supplied; otherwise explain unsupported virtual item. |
| Elevated target/source | Drag/open can be blocked by integrity boundary; show failure, do not retry with elevation automatically. |
| Source removed during drag | Drag may fail/cancel; retain record and refresh status. |
| Target reports move | Do not proactively delete the Space record; validate reference afterward. |

## Clipboard

| Case | Expected behavior |
|---|---|
| Rapid changes | Serialize through bounded queue; process latest valid candidates, record dropped-count diagnostics. |
| Content changes before read | Treat read failure/stale view as skipped; no corrupt item. |
| Clipboard locked | Short bounded retry with jitter; then skip and remain healthy. |
| Extremely large image | Check metadata/stream bounds; skip over configured byte/pixel limit with unobtrusive notice. |
| Image decompression bomb | Bound dimensions, decoded bytes, time, and decoder concurrency. |
| Huge text | Enforce size threshold; optionally store bounded/truncated item only if clearly labeled—default skip above hard cap. |
| Unsupported format | Ignore unless a safe text/storage fallback exists. |
| Multiple formats | Select by explicit precedence; avoid creating multiple records for one event. |
| Empty content | Ignore. |
| Duplicate item | Coalesce within duplicate window; never overwrite pinned item silently. |
| DropSpace copies an item | Self-write guard prevents a new history loop. |
| Another app rewrites same item | Fingerprint/time coalescing reduces noise without assuming authorship. |
| Delayed rendering | Await with cancellation/timeout; skip if source exits or fails. |
| App exits mid-capture | Complete only bounded committed transaction; temporary files are recovered/cleaned on next launch. |
| Windows restarts | Capture resumes only when app starts; no claim to recover events while absent. |
| Session lock | No special sensitive detection guarantee; optional future pause-on-lock requires validation. |
| Remote Desktop clipboard | Treat as ordinary clipboard with source often Unknown; exclusion cannot be guaranteed. |
| Password manager copy | May be captured; product relies on pause/retention, not detection claims. |
| Image exported | Export copy becomes user-owned; history remains until retention/removal. |

## UI and windows

| Case | Expected behavior |
|---|---|
| Multiple displays | Restore window only inside an active work area; otherwise center on primary display. |
| Mixed DPI | Recalculate bounds and thumbnail scale when crossing displays. |
| 125/150/200% | No clipped commands/text; move secondary actions to overflow. |
| 200% text scale | Reflow settings/details; avoid fixed-height text containers. |
| Taskbar top/side/auto-hide | Use display work area, not raw bounds. |
| Full-screen foreground app | Quick Panel V1.1 avoids aggressive focus stealing; user can reopen main window. |
| Quick Panel off-screen | Clamp to active work area every invocation. |
| Quick Panel invoked twice | Toggle/dismiss or focus existing panel; never create duplicates. |
| IME composition | Search waits for composition changes and preserves candidate UI. |
| High contrast | Solid surfaces/system colors; all state legible without material/color alone. |
| Reduced motion | Disable nonessential transitions. |
| Screen reader | Do not announce every new clipboard payload; announce recording/errors and focused content only. |
| Explorer restarts | Re-add tray icon after taskbar recreation notification. |
| Window closed while dialog open | Resolve/cancel safely before hide/exit according to dialog semantics. |

## Data and storage

| Case | Expected behavior |
|---|---|
| Database corruption | Stop writes; recovery screen offers backup/diagnostics/reset only with explicit confirmation. |
| Migration failure | Roll back transaction, preserve original and pre-migration backup, do not open as empty. |
| Disk full | Reject new payload atomically, pause capture if repeated, keep existing data readable. |
| Database read-only | Enter read-only/recovery mode; actions requiring writes disabled with reason. |
| Thumbnail missing/corrupt | Delete derived entry and regenerate; item remains valid. |
| Payload file missing | Item status Error/Unavailable; allow removal; do not invent content from thumbnail. |
| Orphan payload | Quarantine during bounded startup reconciliation; delete only after grace/verification. |
| Orphan database payload row | Mark item error and schedule safe cleanup. |
| Clock changes/time zone | Store UTC; date grouping recalculates with current local zone. |
| Abrupt power loss | WAL/transactions protect database; temp/orphan reconciliation runs next start. |
| Old app opens new schema | Refuse destructive downgrade; explain incompatible version. |
| Retention during browsing | Remove by ID; UI handles item disappearance and moves focus predictably. |
| Clear and pin race | Serialize writes; operation order is deterministic and surfaced. |

## Search and classification

- Query is empty/whitespace: show collection recents.
- Query contains SQL wildcard/control characters: parameterize and escape according to search semantics.
- Unicode case folding varies: define invariant normalization and test Turkish/Greek/CJK.
- Classification ambiguous: keep Kind Text and show only a presentation hint.
- URL with dangerous/unregistered scheme: display as text; Open disabled or confirmed by policy.
- Path-looking text does not exist: keep as Text/Path hint, not a file reference.
- Search result deleted/expired before action: show “Item no longer available” and refresh.
- FTS/index inconsistent: rebuild from canonical items table; canonical data wins.

## Lifecycle and integration

- Second launch while hidden: redirect and show existing window.
- Shutdown while database busy: bounded cancellation/flush; OS shutdown wins.
- Startup entry disabled by Windows: reflect actual state rather than toggling repeatedly.
- Hotkey conflict: feature disabled with setting error; app remains usable.
- Tray icon unavailable: closing cannot silently strand the process; default to exit or keep window visible.
- Unhandled service exception: boundary logs redacted error, UI receives safe state, process-level handler writes crash marker.
- Package update during schema change: migration is idempotent and version-checked.

## 3.0 Preview network/preview cases

- Corrupt/oversized preview: provider returns unknown fallback; source is untouched and cache entry is not retained.
- Pairing SAS mismatch, expired hello, duplicate nonce, blocked peer, unsupported platform, or missing certificate pin: fail closed.
- Transfer cancellation/reconnect: receiver reports accepted chunks; sender resumes only those chunks and final whole-file hash remains mandatory.
- Traversal, reparse point, duplicate path, source mutation, empty file, destination collision, or disk-full receive: stage/rollback and return a coarse failure.
- Clipboard stale reconnect or echo: per-peer mode and 10,000-entry/24-hour content guard prevent stale overwrites/loops.
- Nearby private address missing, token expired, receiver cap reached, invalid range, or revoke: do not fall back to a public interface.
- Internet Worker missing, expired, wrong object, browser key absent, AES-GCM failure, or SHA-256 mismatch: show unavailable/integrity failure, never success.
