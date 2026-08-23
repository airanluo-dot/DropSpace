# DropSpace Privacy and Threat Model

## Overview

DropSpace stores sensitive classes of data by design. “Local only” reduces network exposure but does not make clipboard history safe by default. The product must minimize capture, make recording state obvious, bound retention, and avoid claims that content classification or source-app exclusions are complete.

This threat model covers the Windows desktop runtime described by `ARCHITECTURE.md`, including its narrow public GitHub Release update boundary. Cloud sync, accounts, browser extensions, telemetry, and AI remain outside it.

## Data lifecycle

```text
Clipboard event
  -> bounded in-memory candidate
  -> format/size/policy check
  -> optional app-owned payload file
  -> SQLite metadata transaction
  -> visible history
  -> retention/user deletion
  -> logical delete + physical payload cleanup
```

Space file records store references and metadata only. Clipboard images and large text become app-owned local payloads. Thumbnails are derived cache.

## Threat Model, Trust Boundaries, and Assumptions

### Assets to protect

- Clipboard text, images, and copied file/folder references.
- File paths, names, timestamps, and user work patterns.
- Pinned items and retention preferences.
- Database, payload files, backups, logs, and exported diagnostics.
- Integrity of actions that open, copy, replace, or drag referenced content.

### Trust boundaries

1. Other applications → Windows clipboard → DropSpace.
2. Explorer/other apps → drag data package → DropSpace.
3. DropSpace → target applications during copy/drag/open.
4. User-controlled paths/removable/network/cloud storage → file services.
5. UI process → SQLite/payload/cache directories.
6. Future network/AI providers are outside MVP and require a new explicit boundary.
7. Public DropSpace website/GitHub Release metadata and GitHub downloads → bounded update parser/cache → optional installer execution.

### Assumptions

- The Windows account and OS are not already fully compromised.
- DropSpace runs as a standard desktop user, not elevated.
- Source files remain owned and protected by their existing file-system/provider permissions.
- Windows, WinUI, clipboard, image codec, SQLite, and shell components are trusted platform dependencies but can fail on malformed or unavailable input.
- Same-account malware or an administrator can generally access local app data; MVP does not claim protection from that attacker.
- Ordinary content features make no network calls. If update checks are enabled, DropSpace contacts only the public versioned DropSpace website API, its GitHub Pages mirror, the GitHub Releases API, and official GitHub asset URLs without a user or device identifier.

### Threat actors and conditions

- Another local process places malformed, enormous, rapidly changing, or misleading clipboard/drag data.
- A local user or malware with the same account reads DropSpace local files.
- Crafted paths, shortcuts, URLs, or metadata trigger unsafe parsing/open behavior.
- Disk corruption, rollback, or migration failure exposes stale/deleted content.
- Logs, crash reports, notifications, or accessibility announcements leak payloads.
- The user assumes exclusions caught password-manager content when attribution failed.

### Security invariants

- Removing a DropSpace record never deletes or moves its referenced source file.
- All app-owned payload paths resolve inside one controlled root; traversal is rejected.
- Clipboard events are bounded by count, bytes, time, and concurrency before expensive decoding. Clipboard folders are stored only as references and are never recursively enumerated for capture-size accounting.
- Raw payloads, full paths, and URL query strings do not enter logs by default.
- Pause means no new clipboard item is persisted after the pause transition completes.
- Exit removes tray/hotkey/listeners and ends capture.
- Pinned status affects retention only; it does not grant extra execution capability.
- Opening a URL or file requires an explicit user action.
- Database migrations never silently replace a failed store with an empty one.
- Update metadata is bounded and fail-closed; executable URLs cannot come from a manifest; cached installers require exact size and SHA-256 and never execute unattended without the exact trusted DropSpace Authenticode publisher.

## Attack Surface, Mitigations, and Attacker Stories

### Sensitive-data risks

Potential captures include passwords, tokens, one-time codes, financial data, private chats, medical information, and screenshots. Pattern detection produces false positives and false negatives; it cannot be a security boundary.

MVP response:

- Do not add a “sensitive content detected” promise.
- Provide one-click Pause in Clipboard and tray.
- Provide clear-range deletion.
- Default to finite retention.
- Do not preview clipboard content in notifications.
- Explain that other users/processes with account access may read local history.

### App exclusion

Deferred to V1.1. Windows clipboard ownership can sometimes identify an owner window/process, but this is not reliable for every app or clipboard path. Exclusions therefore:

- apply only when attribution succeeds;
- show Unknown source when it does not;
- never claim to protect password-manager or remote-session data completely;
- are supplemented by Pause and short retention.

### Pause recording

- Available from Clipboard header and tray.
- Persists across window hiding; setting persistence across full restart is a product decision recorded in `DECISIONS.md`.
- UI shows Paused until explicit resume.
- In-flight capture checks a pause generation token before durable commit.
- Pause does not clear existing history.

### Data retention

- Default: 30 days and 1,000 unpinned clipboard items, whichever removes first.
- Pinned clipboard items are exempt until unpinned or explicitly removed.
- Space items do not expire automatically.
- Image/text byte budgets are enforced in addition to item count.
- Retention cleanup is transactional for metadata and eventual for payload files.

### Local storage

- Store under the packaged app's local data location with current-user ACLs.
- Never store payloads in temporary/public folders.
- Backups inherit the same protection and retention.
- Derived thumbnails must be deleted when their source item is cleared.
- Exported files leave DropSpace's protection and the user is told where they were saved.

### Encryption at rest

MVP does not promise a separately encrypted vault. OS account and disk protection remain the baseline. Application-level encryption is deferred because:

- keys available automatically to the same signed-in process do not protect against all same-account malware;
- encryption complicates search, migration, crash recovery, and startup;
- a lock/unlock experience changes the product substantially.

Before V1.1+, evaluate Windows Data Protection APIs for payload keys and define the exact threat being addressed. Do not market encryption until backups, migrations, and deletion are covered.

### Clear history

- Last hour, Today, All.
- Broad clear shows affected count and whether pinned items are preserved.
- Deletion removes search projections immediately and queues payload/cache cleanup.
- Disk-full or IO failures after logical deletion are recorded for cleanup; deleted items do not reappear.
- Secure erasure on SSDs cannot be guaranteed and must not be claimed.

### URL and file safety

- Display full URL destination before opening; allow only registered schemes by explicit policy.
- Never auto-open captured content.
- Treat `.lnk`, executable, script, network, and reparse-point targets as untrusted references.
- Do not parse arbitrary file contents in MVP.
- Locate/Replace Reference requires user picker confirmation and updates the same item intentionally.

### Denial-of-service controls

- Bounded clipboard queue and per-item byte/pixel/text limits.
- Decode images to target size; reject implausible dimensions before allocation where possible.
- Timeouts/cancellation around shell thumbnail providers and network paths.
- Database and cache size budgets with user-visible cleanup path.
- Rate-limited logs and no per-event toast.

### Privacy settings

- Recording enabled/paused state.
- Retention age, item count, and image capture.
- Current local storage size and data location.
- Clear ranges.
- Best-effort excluded apps (V1.1) with limitation text.
- Optional payload-free diagnostic export.

### Threat and mitigation summary

| Threat | Primary control | Residual risk |
|---|---|---|
| Sensitive clipboard capture | Pause, finite retention, clear controls | User may forget to pause; attribution incomplete |
| Local data theft | User-scoped storage, no network, optional future protection | Same-account malware/admin can access data |
| Malformed/huge payload | Size/pixel/time/concurrency limits | Decoder/platform defects remain possible |
| Clipboard feedback loop | Self-write marker + fingerprint/time window | Other apps can rewrite equivalent content |
| Path traversal in payload store | Generated relative paths + root containment check | File-system compromise outside app model |
| Unsafe source reference | Explicit action, capability checks, no auto-open | User can choose to open malicious content |
| Migration/corruption loss | Transactions, backup, recovery screen | Last unflushed events can be lost |
| Privacy leak through telemetry | Local structured redacted logs | User-generated titles can still be identifying if mishandled |
| Compromised update metadata/payload | Fixed official GitHub asset identity, bounded manifest, size/hash, publisher gate | SHA-256 alone does not establish authenticity; unsigned builds require explicit user action |

### Concrete attacker stories

- A local app copies a bitmap with extreme declared dimensions to force memory exhaustion. DropSpace checks dimensions/byte budgets, limits decode concurrency, and can skip the item.
- A crafted payload record points outside the app data directory. The payload store generates paths itself and rejects any resolved path outside its root.
- A password manager copies a secret while recording is enabled. DropSpace may capture it; finite retention, Pause, and Clear reduce exposure, while exclusions are explicitly not guaranteed.
- A malicious URL is copied and later selected. DropSpace displays it as data and never launches it until the user explicitly chooses Open under an allowed-scheme policy.
- A migration fails after an update. Transactions and the pre-migration backup preserve the prior store; the app enters recovery instead of overwriting history.

Out of scope for MVP severity claims: an attacker with administrator/kernel access, a fully compromised Windows account, or compromise of an AI/cloud provider that does not exist in the MVP architecture.

### Updater privacy and network behavior

Automatic update checking is enabled by default and can be disabled. It runs at most once per process start, with no timer, service, scheduled task, network-change listener, or machine identifier. Manual checks remain user-invoked. Requests contain only normal HTTPS headers and `User-Agent: DropSpace/<version>`. Diagnostics may record endpoint type, version, state, HTTP status, integrity outcome and installer exit status; they never record Clipboard content, Temporary Space paths, filenames, search queries, tokens, or GitHub credentials.

### Smart drag observer and verification privacy

Smart Drag Detection v2 observes documented accessibility drag event identifiers and global mouse/key transition metadata (button, physical screen point, threshold crossing, release and Escape cancellation) only while the process is running. Unknown sources are not identified by application-name telemetry. The detector does not suppress input, inject into another process, record typed keys, poll the cursor, require elevation, upload telemetry, or log dragged file names/full paths.

A generic candidate may create one 60 ms hollow local OLE verification target. Verification calls only `IDataObject.QueryGetData` for bounded file-format evidence and never reads virtual file content, filenames, or full paths. The accepting visible/Classic target reads bounded file-system paths only after OLE Drop and routes them through the existing local Temporary Space reference pipeline. CIDA/PIDL counts, offsets and segment walks are bounded and fail closed. Diagnostics are limited to session/evidence/classification/counter/elapsed metadata; a known path or payload sample must remain absent from automated log scans.

## Severity Calibration (Critical, High, Medium, Low)

- **Critical:** plausible code execution or broad arbitrary-file overwrite triggered by clipboard/drag input without meaningful user action; payload path traversal escaping app storage into user/system files.
- **High:** silent bulk disclosure of clipboard history to a network endpoint; record removal deleting source files; a default-on bypass that reliably captures content despite Pause; recoverable but broad history destruction during migration.
- **Medium:** local payload exposure beyond intended current-user storage; denial of service from a crafted oversized item requiring restart/cleanup; incorrect source attribution causing an exclusion to fail for sensitive data; a clear operation leaving accessible originals behind.
- **Low:** metadata-only leakage in logs, confusing but non-destructive availability state, a transient tray/hotkey issue, or a one-item retention inconsistency without sensitive-content amplification.

Severity depends on reachability and affected data. The same bug is lower when it requires explicit opening of a known untrusted item and higher when ordinary background clipboard capture triggers it automatically.

## Security validation gates

- Property tests for payload-root containment and path normalization.
- Fuzz/limit tests for text classifiers and image metadata handling.
- Automated log scan asserting known secrets/payload samples are absent.
- Pause race test proving in-flight candidates do not commit after pause completion.
- Clear-history integration test proving metadata, payload, thumbnail, and search removal.
- Migration failure tests preserve the prior database.
- App-exclusion feature cannot ship without documented false-negative behavior and UI copy review.

## Incident behavior

If corruption or a privacy-affecting bug occurs, recording defaults to paused, existing data remains recoverable where safe, and the app offers clear/export diagnostics without payloads. The app must not silently upload diagnostics.

Repository: DropSpace
Version: design-baseline-2026-08-07
