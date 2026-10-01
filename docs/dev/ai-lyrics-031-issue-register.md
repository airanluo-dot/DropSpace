# DropSpace 0.3.1 Beta 1 issue register

Status: implementation and focused regression work in progress. This is not the completed whole-product audit. No release is authorized by this document alone.

## Required order before publication

1. Finish the approved feature set and integration.
2. Review the complete code, logic, data handling and functional paths, and walk the product as a first-time user. Cover clean install, upgrade, onboarding, settings, model consent/download/cancel/resume, playback, seeking, track/language changes, unavailable resources, failures, restart and exit.
3. Repeat the complete review/fix/retest process for the agreed review rounds. Record scope and evidence; focused tests do not count as complete rounds.
4. Conduct a separate critical product review: discoverability, defaults, terminology, feedback, visual consistency, accessibility, responsiveness, performance and recovery.
5. Consolidate findings here with severity, reproduction, impact, remedy and verification. Fix each issue and repeat affected flows. Explicitly identify unresolved defects and unverified risks.
6. Publish only after these gates pass; do not treat a successful build or a single green run as evidence that every defect has been eliminated.

## Findings already established during implementation

| ID | Priority | Finding / reproduction | Impact | Remedy | Verification / state |
|---|---|---|---|---|---|
| REL-001 | P1 | Beta32 SDK notice, roadmap crash status, displayed Beta number and predecessor disagreed with package/release evidence | Misleading release metadata | Correct scoped documents; add release consistency regressions | Fixed in first PR76 batch; 12 local checks and Windows metadata gates passed |
| REL-002 | P1 | First Beta of 0.3.1 arithmetically selected absent 0.3.0 stable | Upgrade lifecycle did not exercise a real predecessor | Select closest lower documented release; fixture covers prerelease-only predecessor | Implemented; next Windows PowerShell run pending |
| AI-001 | P1 | Unicode source JSON was escaped before tokenization; a Chinese prompt produced unrelated English lines | Plausible but wrong translation | Preserve Unicode in plain model input; regression test | Fixed and live inference retested |
| AI-002 | P1 | Structurally valid output contained replacement characters and leaked prompt fields inside a text value | Corrupt text could be shown as lyrics | Reject observed protocol corruption and retry within bounds | Regression tests pass; semantic correctness remains a separate evaluation |
| AI-003 | P1 | Windows prompt file remained open with DeleteOnClose; runtime file reader does not share delete access | Native inference could not read prompt | Close prompt before child starts, clean in finally, native smoke | Implemented by runtime workstream; Windows actual-model gate pending |
| AI-004 | P1 | Completed multi-batch translation exceeded per-cache-entry cap and threw during optional cache write | Valid translation was lost | Skip oversized optional cache entry while returning verified translation | Focused regression passed |
| AI-005 | P1 | Cache eviction counted only first 10,000 files | Cache could exceed intended total bound | Account for all owned entries | >10,000-entry regression passed |
| AI-006 | P1 | Provider translation target was unknown; TTML mixed romanization and translation | Wrong language could suppress AI or appear as translation | Preserve explicit translation language/provenance; keep unknown unknown; separate romanization | Parser and coordinator regressions passed; full App integration pending |
| AI-007 | P1 | Shutdown while download/inference waited for its semaphore | Queued work could survive disposal | Link queue waits to lifetime cancellation | Download regression passed; new native process tests pending |
| AI-008 | P1 | Candidate small-model tests showed subject/negation errors and runtime EOS metadata defects | Smaller download may have unacceptable accuracy or malformed output | Matched fixed-case evaluation, hash-pinned model-specific handling only if justified | Initial limited-fixture evaluation was insufficient. Full-song and independent single-line tests now show known object/meaning errors. Compact is NOT cleared for release; see AI-009 and semantic review |
| UI-001 | P2 | Changing lyric font/presentation options previously refetched and cleared lyrics | Visible disruption during adjustment | Separate retrieval settings from presentation settings | Core regression + first Windows build passed; full visual audit pending |
| QA-001 | P2 | First en-US standalone smoke timed out capturing mixed clipboard file/folder references; same revision installed smoke and release-lane smoke passed | Intermittent failure warrants investigation | Preserve failure logs; bounded rerun without relaxing assertions | Rerun passed; root cause unproven, watch in subsequent full runs |

## Checks pending after integration

- Actual Windows model download, native offline inference, prompt cleanup, cancellation and enforced resource limits
- Small/standard selection, both-model installation, correct defaults, and no unconsented downloads
- Outward halo native alpha, z-order, click-through, DPI/multi-monitor, mode reversal, pause/end, interlude and no lingering color
- Font clipping, text scaling, responsive layouts, keyboard/screen-reader behavior, high contrast and reduced motion
- Full first-time-user and separate critical product review started on candidate 49ab7ef; not yet passed

## First-round remediation checkpoint — 2026-10-01 20:03 UTC

The complete first round is still pending integrated Windows verification. Source partitions and exact inventories are in `round1-app-review.md`, `round1-core-infrastructure-review.md`, and `round1-release-web-review.md`; focused green checks are not a whole-round completion.

| ID | Severity | Finding and implemented remedy | Verification boundary |
|---|---|---|---|
| R1-App-1/4/5/7 | P2 | Preserve clipboard paging cursor after live eviction; resolve image drag payloads; guard compatibility-report copy; route Music Ctrl+F to search | Implemented; integrated Windows tests pending |
| R1-App-2/6/9/13 | P1/P2 | Serialize dialogs and contain callback errors; recover orphaned Undo; bound delayed clipboard providers; retain actual remote image format | Pure helper/selected repository tests passed; native cases pending |
| R1-App-3 | P2 | Recover handoff peer list from persisted trust and current fingerprint-matching discovery; persist explicitly selected clipboard peer modes, preserve them across settings transactions, refresh endpoints | New settings round-trip regression; native discovery/restart tests pending |
| R1-App-8 | P2 | Give legacy settings controls localized UI Automation names | Implemented; native UIA/Narrator verification pending |
| R1-App-10/11 | P2 | Revalidate native configuration/geometry after recoverable failure; retire activation hosts from owner collection and reject late OLE callbacks | Implemented; native regression execution pending |
| R1-App-12 | P2 | Correct recovery message; generic startup failure no longer falsely claims database failure or stopped writes | Both languages updated; no runtime shutdown behavior changed |
| R1-Net-1 | P1 | Host pairing confirmation returns responder identity expected by Client | Full loopback TLS Client↔Host test added, including bilateral trust and equal secrets; Windows DPAPI execution pending |
| R1-Net-2 | P2 | Separate Base64 clipboard HTTP budget from smaller transfer/handoff limits | Full serialized 13MiB/50MiB fixtures and route aliases pass locally |
| R1-Share-1 | P2 | Persist encrypted revocation handle before first upload; mark incomplete uploads and recover cleanup after restart; retain capability if revoke fails | Pure lifecycle order/failure test passed; DPAPI marker round-trip pending |
| R1-Lyrics-1 | P2 | Canonical NetEase version evidence cannot be hidden by an alias | Live/Remix fixtures passed |
| R1-Lyrics-2/3 | P2 | Explicit TTML timing dialect replaces numeric guessing; expired negative-offset LRC endings cannot revive | Core tests passed including parent-relative boundaries; Apple absolute fixture retained |
| R1-Data-1 | P2 | Explicit ordinal-ignore-case Unicode path collation for Space deduplication; no deletion of existing rows | Repository Unicode regression passed |
| R1-Cache-1 | P2 | Strict cache clear retains invalidation on deletion failure; Undo contains cache-only reparse failures | Selected cache/Undo tests passed; lock-file Windows regression pending |
| R1-Worker-1 | P2 | Expired R2 orphan is removed after failed retry when no committed/in-flight owner remains | Both lifecycle-present/absent races pass; Worker total 22 passed |
| R1-Web-1 | P3 | Static manifest omits unresolved release version placeholder | Website build/test pending final rerun |
| R1-Runtime-1 | P1 | Runtime license collection reads pinned source notices, independent of disabled llama-app target | Prior exact f727ce1 Windows/model gates passed; standalone source fixture/missing-notice regression now added for next Windows run |

Current completed full rounds: 0/3. Website final visual preview was approved by the owner at 19:37 UTC; do not redesign it. Publish both sites only alongside the final App. GPT Site root, language routes and assets must pass actual access verification, not merely a successful deployment status. Release version has not been advanced.


## 2026-10-01 21:02 UTC gate update

- `70fc2569` completed the Windows CI/Release validation matrix successfully; publication was skipped. This verified the existing native UIA/lifecycle, bilateral TLS pairing, DPAPI recovery and actual two-model inference regression tests, but does not prove real multi-device, Narrator, multi-monitor or physical-player behavior.
- `49ab7ef55423456d16560309c78d288d44b53655` adds the token/memory/circuit changes below; its Windows checks are pending. Previous green runs do not validate these new changes.

| ID | Severity | Reproduction / impact | Remedy / status | Evidence |
|---|---|---|---|---|
| AI-009 | P1 quality gate | Standard Chinese “leave the key” becomes “keep”; compact Korean scarf becomes another object. Errors recur without background or structured output | Unresolved model-quality limitation; publication held. Asked owner whether a model-plan change is allowed; no approval inferred | `round1-full-song-semantic-review.md`, full-song and single-line outputs reviewed |
| AI-010 | P1 plan gap | Byte-only batching did not implement the approved actual-token context budget | Embedded source-pinned tokenizer; hash verification, 1800-token input budget, remove optional context then split complete requested rows, fail closed on oversized single line; cache version advanced | Core 360 and focused AI Infrastructure 23 passed; new Windows/native gates pending |
| AI-011 | P2 plan gap | Both models previously shared 3GiB job budget despite approved compact 1.5GiB initial ceiling | Compact hash selects 1.5GiB before Windows process launch and in working-set watchdog; standard remains 3GiB | Budget unit test passed; actual Windows compact gate pending |
| AI-012 | P2 recovery | Repeated runtime/output failures had no three-failure automatic pause | Session circuit pauses after three consecutive failed song attempts; cancellation excluded; explicit accessible manual resume reloads current song and invalidates prior generation | Circuit tests and actual service/card projection compile passed; native UI verification pending |


## First-time-user/product remediation — 2026-10-01 21:21 UTC

The separate product review is `round1-product-journey-review.md`. Repairs are in the working tree and need a new native Windows validation; they do not count as a completed full round.

- PJ-01/02: Enabling AI also enables lyric/secondary display after any necessary online-source disclosure; Music and island display an `AI ·` source marker without changing stored text.
- PJ-03: Generation-scoped translation activity/failure/completion states; new state events replace stale download-success feedback. Paused-circuit recovery remains explicit.
- PJ-04/05: Model-specific deletion (including corrupt/partial files after restart) and separately named AI-cache cleanup. Maintenance cancels and drains inference before file operations; new inference is held out during maintenance. Disk cleanup is background work, errors remain visible, and other model/source/cache ownership is preserved. A failed settings save stops deletion.
- PJ-06: Confirmation shows bundled-runtime extraction estimate from actual embedded executable sizes; model disk preflight includes that amount plus 64 MiB recovery headroom and rechecks after a server restarts a ranged download.
- PJ-07: Fresh lyrics defaults are disabled; saved explicit preferences remain intact. Music explains metadata transmission. Enabling AI from disabled online mode first asks to enable online lyrics, then obtains any model-download consent. Native provider test fixtures now explicitly enable lyrics rather than relying on old defaults.
- PJ-08: Reconciled against the approved plan; any matching target-language provider translation keeps the entire source document unchanged. AI does not fill missing source-translation rows.
- PJ-09: First-run clipboard/startup choices are saved before initialization, with both checkboxes off initially. Existing valid explicit legacy choices migrate unchanged; empty/corrupt settings do not imply consent. App shutdown cancels the dialog/save/initialization sequence, and redirected activations wait for startup readiness. CI uses explicit isolated fixture settings, not a production bypass flag.
- PJ-10: Installation instructions no longer label the current installer as v0.1.0; first-run instructions match the actual choice flow.

Local evidence: Core 361 passed; focused Infrastructure lyrics/model/settings tests 83 passed, 3 native-only tests skipped. Actual AI service/settings-card projection compilation passed with zero warnings/errors. App/MainWindow generated-XAML build and actual first-run Windows UI are not validated by that projection build. Model semantic quality remains independently blocked.
