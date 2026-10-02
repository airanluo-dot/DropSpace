# DropSpace Privacy and Threat Model

Beta 28 passive NetEase enhancement inspection is offline: it reads the local player path,
DropSpace receipt and installed component files only. Network access occurs only after an
explicit install, reinstall or update action. Generic online lyric fallback still transmits only
the current public music metadata already listed below, and only to sources selected by the
preferred/backup/remaining-provider policy.

## Explicit NetEase enhancement boundary

Only a user-confirmed enhancement/update/reinstall downloads independent third-party components.
Requests contain public release/asset identifiers, not clipboard/file payloads or playback metadata.
GitHub serves upstream BetterNCM and InfLink-rs artifacts; Microsoft serves required VC runtimes.
Integrity checks, bounded downloads and file ownership are required before deployment. Local receipts
contain installation/profile paths and hashes needed for recovery; logs use category codes rather
than full paths. The player may restart and briefly change playback during verification. InfLink's
upstream default enables SMTC and disables Discord RPC; existing plugin preferences are not silently
rewritten. DropSpace uses no plugin JavaScript API, adds no telemetry, and cannot guarantee the
privacy behavior of an independently installed third-party plugin. Removing enhancement preserves
unrelated existing plugins and user configuration.

## Beta.25 bug-fix privacy boundary

Beta.25 keeps the same local-first and opt-in online lyrics boundary while
requiring candidate identity/metadata validation before a provider result is
shown. Track identity, cache keys and asynchronous result acceptance include the
current media session rather than trusting a title-only match. Clipboard, file,
OLE and updater hardening adds bounds and recovery without uploading user data,
logging raw payloads, or changing source-file ownership.

## Preview.24 lyrics and audio boundary

When enabled in online mode, lyrics lookup sends only the current music title,
artist, album and duration to the user-selected NetEase, QQ Music, Kugou, LRCLIB
or AMLL provider. Beta 28 queries the preferred source, then an optional backup;
only the explicit remaining-provider switch can query the other unselected online
sources, with a bounded concurrent quality window and an eight-second per-provider
budget. A valid result cancels and drains the remaining work. It does not send clipboard text,
staged filenames or file bytes.
The historical source-lyrics cache in this slice is process memory only (32 entries,
bounded text size, two-hour age). The 0.3.1 Beta 1 boundary below adds persistent lyric caching; this historical
limit does not describe the current release candidate's total lyric storage.
Local LRC mode reads only a selected folder with a bounded, nonrecursive scan.
If every online provider fails, display track metadata. Local mode never invokes
online fallback. The integration must cancel
lookup on track/settings/lifecycle changes before release acceptance.
Process-loopback audio is intended only for the live spectrum; PCM must never be
saved or uploaded. System notification and volume observation remain opt-in.

## 0.3.1 Beta 1 lyrics and AI boundary

This section describes the v0.3.1-beta.1 release candidate; it is not evidence of
publication or completed native acceptance. The optional AI feature is explicitly Beta:
first generation can take several minutes, and translation can contain meaning errors.
Complete review, release-blocking bug remediation and native build/package checks remain
required. Current evidence is in the [issue register](docs/dev/ai-lyrics-031-issue-register.md)
and [release checklist](docs/release/v0.3.1-beta.1-checklist.md).

- **Separate choices:** fresh settings leave lyrics and AI translation disabled. Existing
  explicit preferences are preserved. With Online mode selected, enabling AI while lyrics
  are disabled first asks to enable online lookup; downloading a missing model requires its own confirmation.
  Online lookup follows the preferred/backup/remaining-provider controls above. Local LRC
  does not invoke provider fallback. Turning AI off does not itself disable online lyrics.
- **Model downloads:** the only selectable model is Hy-MT2 1.8B Q8_0 (about 1.9 GB).
  Only the consent flow fetches its catalog-pinned weights from Hugging Face and allowlisted HTTPS redirects. Requests identify the model artifact and, when
  resuming, its byte range; they do not contain the lyric prompt or clipboard/file payloads.
  The host still observes normal HTTPS connection/request metadata. Size and SHA-256
  verification precede installation. The runtime is embedded in the App and extracted
  locally; playback does not silently download a missing model.
- **Local inference:** the plaintext model request contains the requested lyric line and
  target language. The current runner sends it to the bundled local process with controlled
  offline arguments, without a hosted AI fallback or an HTTP listener. Online source-lyrics
  requests and explicitly enabled sharing remain separate network paths; this is not an
  absolute network-isolation guarantee for the App or its Windows account.
- **Disk and deletion:** models, resumable partial downloads, extracted runtime files,
  prompt staging files and cached translations use App-owned local locations. They are not
  an encrypted vault. Prompt files are removed on ordinary completion/cancellation paths
  on a best-effort basis; a crash or refused deletion can leave local text behind. Separate
  model and cache removal controls exist. Disabling AI does not erase existing files, and
  deleting a model does not mean every cached translation is deleted. Cleanup must preserve
  user LRC files, external source files and unrelated models; secure erasure is not promised.
- **Persistent lyric cache:** original lyrics, provider translations and AI translations
  share App-owned disk caching under a default 1 GiB (1024 MiB) total budget, adjustable
  from 100 MiB to 5 GiB. Model weights, partial model downloads and runtime files are
  separate from this budget. Matching valid entries can avoid repeated lookup/inference
  across restarts. Music's refresh action reconnects media observation without promising
  a cache bypass. Cache cleanup is distinct from model removal. Quota/deletion errors must
  remain visible and must not delete a user's LRC folder or source files. Native integration evidence is tracked in
  the release checklist rather than inferred from this policy description.
- **Logs and resource risks:** diagnostics use failure categories and numeric process exit
  codes rather than prompt, lyric, model-output or stderr text. The runner has bounded
  threads, token/output/time budgets and model-specific memory limits. Windows Job Objects
  constrain process lifetime/resources; they are not an OS security or network sandbox.
  Native parsers, disk exhaustion, residual staging data and failed child cleanup remain
  risks to test. See the [runtime design](docs/engineering/ai-lyrics-runtime.md).
- **Output quality:** bounded plaintext validation preserves the original text/time axis
  and rejects malformed output, but cannot establish translation accuracy. AI can change
  meaning or omit details. The Beta scope accepts these disclosed quality and latency
  limits; the complete review, known-bug remediation and native release gates still apply.

## Overview

DropSpace stores sensitive classes of data by design. “Local only” reduces network exposure but does not make clipboard history safe by default. The product must minimize capture, make recording state obvious, bound retention, and avoid claims that content classification or source-app exclusions are complete.

This threat model covers the Windows desktop runtime described by `ARCHITECTURE.md`, including updates, opt-in lyric providers and sharing, and the local AI Beta boundary above. Cloud account sync, browser extensions, telemetry and hosted AI translation remain outside the implemented scope.

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
- Candidate lyric prompts/translations, local caches, and the integrity of downloaded models and extracted runtimes.
- Integrity of actions that open, copy, replace, or drag referenced content.

### Trust boundaries

1. Other applications → Windows clipboard → DropSpace.
2. Explorer/other apps → drag data package → DropSpace.
3. DropSpace → target applications during copy/drag/open.
4. User-controlled paths/removable/network/cloud storage → file services.
5. UI process → SQLite/payload/cache directories.
6. Selected online lyric providers → untrusted lyric/metadata parsing → local display/cache.
7. Public DropSpace website/GitHub Release metadata and GitHub downloads → bounded update parser/cache → optional installer execution.
8. User-confirmed model host downloads → pinned local model store → controlled native inference process and App-owned prompt/cache files.
9. Explicitly enabled peer/LAN/Internet sharing → the separate [network threat model](docs/security/network-threat-model.md).

### Assumptions

- The Windows account and OS are not already fully compromised.
- DropSpace runs as a standard desktop user, not elevated.
- Source files remain owned and protected by their existing file-system/provider permissions.
- Windows, WinUI, clipboard, image codec, SQLite, and shell components are trusted platform dependencies but can fail on malformed or unavailable input.
- Same-account malware or an administrator can generally access local app data; MVP does not claim protection from that attacker.
- Local Space/Clipboard operations do not require content upload. Online lyrics, model downloads, enhancement management and sharing have the separate opt-in boundaries documented here. The updater itself contacts the public versioned DropSpace website API, its GitHub Pages mirror, the GitHub Releases API and official GitHub asset URLs without a user or device identifier; that updater list is not an allowlist for every optional App feature.

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
| Local data theft | User-scoped storage, explicit network-feature controls, optional future protection | Same-account malware/admin can access data |
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

The historical MVP had no AI provider. The current candidate's model-download and native-inference boundaries are explicitly in scope above. Administrator/kernel compromise and a fully compromised Windows account remain outside the protection promised here; local inference does not remove those risks.

### Updater privacy and network behavior

Automatic update checking is enabled by default and can be disabled. It runs at most once per process start, with no timer, service, scheduled task, network-change listener, or machine identifier. Manual checks remain user-invoked. Requests contain only normal HTTPS headers and `User-Agent: DropSpace/<version>`. Diagnostics may record endpoint type, version, state, HTTP status, integrity outcome and installer exit status; they never record Clipboard content, Temporary Space paths, filenames, search queries, tokens, or GitHub credentials.

### Smart drag observer and verification privacy

Smart Drag Detection v2 observes documented accessibility drag event identifiers and global mouse/key transition metadata (button, physical screen point, threshold crossing, release and Escape cancellation) only while the process is running. Unknown sources are not identified by application-name telemetry. The detector does not suppress input, inject into another process, record typed keys, poll the cursor, require elevation, upload telemetry, or log dragged file names/full paths.

Each candidate may create one 60 ms hollow local OLE verification target. Verification queries advertised formats and, for `CF_HDROP` or Shell items, reads bounded local file-path metadata to confirm acceptance before revealing the target. Those checks can read filenames and full paths; they do not read file contents or materialize virtual-file streams. Virtual-file content is read only after a real OLE Drop into an accepting target. Accepted paths then enter the existing local Temporary Space pipeline. CIDA/PIDL counts, offsets and segment walks are bounded and fail closed. Diagnostics are limited to session/evidence/classification/counter/elapsed metadata; dragged filenames, paths, and payload samples must remain absent from automated log scans.

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

## 3.0 Preview network boundary

Network features are opt-in. DropLink sends only authenticated pairing/transfer data to a trusted Windows peer over pinned HTTPS. Cross-device clipboard is disabled by default and has per-peer modes plus a bounded loop guard keyed by the original device, sequence, event ID, content hash, and byte length. It suppresses retransmission of the same event while preserving separate intentional copies of identical content. Nearby Share exposes only expiring, tokenized ciphertext/plain local items on a private LAN. Internet Share encrypts payloads before upload; the Worker/R2 backend receives opaque objects and never receives the URL-fragment key. A deployment that lacks an explicitly configured backend is reported as unavailable. See [the network threat model](docs/security/network-threat-model.md).

## Preview.9 shell and Undo boundary

Shell intake receives only the filesystem paths supplied by Explorer/SendTo, validates bounded input, and routes references through the existing local file-reference service. It does not copy or move source data, write Clipboard History, or log paths/filenames. The per-user registry verb and SendTo shortcut are the only Installer-owned shell entries; Portable does not create persistent registration.

Undo stores a token and expiry in the local SQLite row rather than making a second source copy. Pending rows are hidden while the eight-second window is active. On finalization, only payload files under the DropSpace-owned payload root are eligible for cleanup; original source files are never passed to the payload deletion boundary. Pin Undo stores only the previous boolean state in memory.

## Preview.10 Smart Drag and export boundary

Smart Drag source inspection and the ephemeral OLE probe never read dragged
payload content. The probe performs bounded `QueryGetData` checks for file-like
formats, remains hollow and non-activating, returns `DROPEFFECT_NONE`, and
records only format categories and numeric timing. A candidate cannot reveal
the Dynamic Island until the positive file-like classification is complete.

Quick Action exports default to a DropSpace-owned `exports` directory or an
explicit folder chosen by the user. Output names are reserved atomically and
the current source is never opened for write, replaced, or deleted. Completion
surfaces show the selected output path; diagnostics retain the existing
path-redaction policy and use localized error categories rather than raw
exception text.


## Preview.17 derived data and staging cleanup

Preview cache has a 24-hour age, 64-entry/64 MiB total and 16 MiB serialized-entry limit. External references are not cached. Record finalization, Undo recovery, clipboard clear and retention invalidate derived previews; cache generations reject late writes after clearing. Locked-file cleanup is logged and retried, not represented as forensic secure erasure. Staged import cleans every admitted app-owned staging path after completion or cancellation, never an external source. Automatic cross-device propagation remains opt-in/event-driven and keeps at most 16 queued items plus one active send, with bounded image reads. No telemetry, account, new network endpoint or collection category is added.
# Preview.24 notification observer

Notification activities are off by default. Enabling the option checks Windows
access without requesting permission automatically. The explicit permission action
uses the Windows notification listener consent dialog. Only newly added events are
read; existing notification history is not replayed. Source names, titles and bodies
are length-bounded, transient in memory, and never written to logs or sent online.
Disabling cancels and drains the listener. Unpackaged or permission-denied hosts
report unavailable/denied instead of fabricating notification activity.
