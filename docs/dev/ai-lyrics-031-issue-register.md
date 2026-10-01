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
| AI-008 | P1 | Candidate small-model tests showed subject/negation errors and runtime EOS metadata defects | Smaller download may have unacceptable accuracy or malformed output | Matched fixed-case evaluation, hash-pinned model-specific handling only if justified | Compact IQ3_S accepted for experimental opt-in after matched evaluation and hash-specific EOS correction; Windows gate pending |
| UI-001 | P2 | Changing lyric font/presentation options previously refetched and cleared lyrics | Visible disruption during adjustment | Separate retrieval settings from presentation settings | Core regression + first Windows build passed; full visual audit pending |
| QA-001 | P2 | First en-US standalone smoke timed out capturing mixed clipboard file/folder references; same revision installed smoke and release-lane smoke passed | Intermittent failure warrants investigation | Preserve failure logs; bounded rerun without relaxing assertions | Rerun passed; root cause unproven, watch in subsequent full runs |

## Checks pending after integration

- Actual Windows model download, native offline inference, prompt cleanup, cancellation and enforced resource limits
- Small/standard selection, both-model installation, correct defaults, and no unconsented downloads
- Outward halo native alpha, z-order, click-through, DPI/multi-monitor, mode reversal, pause/end, interlude and no lingering color
- Font clipping, text scaling, responsive layouts, keyboard/screen-reader behavior, high contrast and reduced motion
- Full first-time-user review and separate critical product review have NOT started or passed yet

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
