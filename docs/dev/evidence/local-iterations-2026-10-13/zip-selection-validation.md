# Round 7 — explicit ZIP directory-link selection

Status: **planned; not run yet**. Wait for the implementation agent's final source and root
review before executing. This local record does not alter the parent iteration ledger.

Authorized scope is one new combined, precisely named Infrastructure test. It must exercise
the real action registry and cover explicit directory-link selection alone and mixed with a
normal source: reject as `ActionInvalidData`, leave no output, preserve source bytes. A normal
selected directory containing a nested link must still succeed under the existing skip-link
policy. Temporary Linux symlinks are sufficient for this managed/filesystem check; this is not
a Windows junction validation.

Reuse the existing restore, perform one normal targeted build/test, and record the exact
filter, raw output, actual TRX and source hashes. Do not run historical Zip tests, especially
the 10,000-directory entry-budget scenario, or any broad suite.
