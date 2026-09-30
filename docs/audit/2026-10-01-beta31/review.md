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

## Round 2 — state, concurrency and boundary review (in progress)

## Round 3 — pending

## Release hold

Do not merge or publish until all three passes have completed and necessary Windows,
installer, upgrade and final artifact validation has passed. User requested Beta 31 and
preserved update identity. Physical multi-monitor and actual music-player visual testing
remain distinct from automated validation.
