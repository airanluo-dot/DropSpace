# Beta 31 three-round repository review

## Scope and method

Requested: three complete repository-wide scan/review/fix passes before publishing Beta 31.
The root assistant performs the reviews directly; no external coding agent is used.
A full pass means the tracked source/configuration inventory is scanned, all applicable
available checks are run, and findings are traced through affected callers/tests. It does
not claim that every line has received an independent formal proof or that all possible
bugs are eliminated. Automated coverage and manual inspection are recorded separately.
Historical audit JSON is evidence, not application source.

## Round 1 — correctness and integration (review complete; final Windows gate outstanding)

- Inventoried 503 tracked source/configuration/evidence files; 415 C# files parsed without syntax errors.
- Scanned application/infrastructure async, blocking, process-launch and file-deletion sites (222 matching lines); these are review leads, not confirmed vulnerabilities.
- Manually inspected lyric parsing/display/timeline, media interpolation, widget persistence/layout/countdown, page animation, foreground/background material selection, settings normalization, clipboard capture/propagation, payload containment/reconciliation, file availability cache, updater download/integrity and release ordering.
- Existing candidate Windows validation found a widget drag-preview lambda parameter named `_` capturing an `out _` argument as `object`. Fixed with a distinct out variable (commit e603f79). Syntax-only Linux checks had not found this type error.
- Repaired release README/roadmap metadata and retained the explicit modern-window capability gate alongside Desktop Acrylic capability. No check was removed or bypassed.
- Found URL user-info credentials surviving log redaction. Added a regression that fails on the old code, then redacted URL user-info before query/fragment cleanup. Host/path diagnostics remain useful.
- Final local Core: 298 passed. Worker: 21 passed. Website: 34 passed. All inventoried JSON/XML/JavaScript parsed successfully; C# syntax checked for all 415 original files. Windows native checks remain a release gate, not represented as locally passed.

## Round 2 — state, concurrency and boundary review (review complete; final Windows gate outstanding)

- Repeated repository-wide C# syntax and state/lifetime/process/TLS boundary scans after Round 1 fixes. 417 C# files parsed; source/config inventory remains covered mechanically.
- Traced clipboard queue cancellation, media worker/session teardown, bounded metadata calls, settings write serialization and recovery, database transaction durable tails, shell argument construction, peer-certificate pinning, update manifest validation and staged encrypted uploads. No speculative broad refactors were made.
- Found repeated enhanced-LRC lines reusing the first occurrence's absolute word timestamps. Reproduced with a failing test; shift word start/end timestamps for each repeated line while preserving inferred end timing. Correct word highlighting now starts at each occurrence.
- Core: 299 passed; Worker: 21 passed; website: 34 passed. The earlier Windows candidate reached installer packaging after App/native-boundary and WinUI compile steps passed; these are not yet evidence for the final post-audit commit.


## Round 3 — regression and delivery-chain review (review complete; Windows release validation pending)

- Repeated the full tracked source/configuration structural scan and C# syntax parse after fixes, plus all local Core, Worker and website suites. Verified localized resource keys remain unique and changes pass whitespace checks.
- Reviewed installer identity, in-place update parameters, version comparison, downgrade protection, manifest asset matching, hash verification, official asset URL restrictions, publish bridge commit binding, and data-preserving uninstall defaults.
- Added deterministic tests for Beta30 → Beta31 → Beta32 upgrade ordering and 200 malformed widget-layout inputs, verifying bounded non-overlapping layouts and normalization idempotence.
- Core: 301 passed; Worker: 21 passed; website: 34 passed; C# syntax: 418 files, zero syntax errors. No additional reproducible production defect was found in this pass. No claim of zero bugs or exhaustive manual reading is made.
- Final Windows checks must run on the exact post-audit commit. Earlier successful build/package steps cannot substitute for them.


## Release hold

Do not merge or publish until all three passes have completed and necessary Windows,
installer, upgrade and final artifact validation has passed. User requested Beta 31 and
preserved update identity. Physical multi-monitor and actual music-player visual testing
remain distinct from automated validation.
