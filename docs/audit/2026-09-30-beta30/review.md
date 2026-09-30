# Beta 30 bounded corrective review

Baseline: `a5801884073e82ee02b68ee402df4538172eeece` (Beta 29).
Work was performed directly in the review workspace. No separate coding-agent run
was used. The user authorized a ten-hour maximum and discouraged unrelated
refactors, repeated large test matrices, and unnecessary storage growth.

## Coverage and limits

The tracked source inventory contains 267 production C# files (53,048 lines at the
baseline), plus the Worker, website, packaging and test sources. Inventory and
compilation are not a claim that every line was semantically audited. Manual
review concentrated on transfer ownership/finalization, replay/admission, URL
capture/handoff/share actions, diagnostics, updater integrity/state, settings,
clipboard coordination, payload/staging cleanup, quick actions, preview caches,
media/lyrics policies, and overlay/drag state. Existing tests cover additional
implementation paths. No claim of zero remaining defects is made.

## Confirmed corrections

1. **Receive assembly ownership.** The old destination + session-ID temporary name
   collided with valid final filenames. Two regressions reproduced loss of an
   earlier completed item and of a pre-existing temporary-looking file. Assembly
   now uses a random same-directory filename with `CreateNew` and records ownership
   before cleanup. Original files are never truncated by assembly name reservation.
2. **Share creation admission ordering.** A source already over its allowance
   consumed the global allowance before being rejected. The focused regression
   failed before the change. Per-source admission now precedes global admission.
3. **Functional URL fragments.** Classification and explicit URL handoff removed
   fragment data used by hash routes and encrypted-share keys. Newly captured and
   handed-off URLs retain their fragments; display metadata remains concise.
4. **Diagnostic fragment redaction.** The URL log redactor left fragment secrets
   in its output. Two cases with/without query strings failed before the fix and
   pass after it. Diagnostic URLs now remove both query and fragment contents.

## Local executed evidence

- Core baseline: 278 passed. After URL classification correction: 280 passed.
  Final local Core suite after fragment-log regressions: 282 passed.
- URL classifier regression before correction: 3 failed, 4 passed.
- Explicit URL handoff focused checks: 2 passed.
- URL-fragment redaction before correction: 2 failed; redactor checks after: 8 passed.
- Transfer assembly before correction: 2 failed; after: 2 passed.
- NetworkRoundTwoRegressionTests after correction: 15 passed.
- Worker focused admission regression failed before correction. Full Worker suite
  after correction: 21 passed.
- Website source build/tests: 34 passed.
- Full Infrastructure suite on Linux: 264 passed, 8 failed, 6 skipped. Seven failures
  require Windows DPAPI; the local receiver-composition test attempts to create
  the current user's Downloads directory outside this workspace's write access.
  These are not reported as Windows passes. Final Windows execution is required.

## Publication gate

Pending: real Windows CI and release validation on the final commit, protected
merge, official release workflow, tag/asset/checksum/manifest and website checks.
No failed mandatory check may be waived merely to meet the time limit. The
supplemental mass clipboard investigation remains opt-in as in Beta 29.

Manual multi-device/provider/deployed-Worker checks are not established by this
review. Worker fixes ship as source and require backend deployment separately.
Existing previously stripped URL metadata is not migrated. Both peers should
update before using fragment-preserving explicit URL handoff.

## Media/lyrics priority follow-up (14:50 UTC)

User reports persistent Apple Music recognition failure since an earlier algorithm
change, and more frequent NetEase tracking loss after beta29. These runtime causes
are not yet proven on the user's device. Beta28-to-beta29 did not change the main
SMTC selection/experience or matcher implementation; this does not invalidate the
reported regression or rule out indirect changes.

- SMTC metadata reads previously compared a revision incremented by every timeline
  and playback event. Frequent events could exhaust both attempts and repeatedly
  retain the previous track. Reads now validate metadata changes only. Three focused
  App tests cover continuous unrelated updates, one actual track change, and bounded
  rejection of repeatedly stale reads. Windows execution is pending.
- A transient refresh exception now requests at most three delayed retries, instead
  of depending entirely on a later publisher event. It does not start continuous
  polling; cancellation terminates the delayed retry with the consumer.
- Lyric version parsing previously found `live` inside `Oliver` in a featured credit.
  A deterministic regression returned score 0 before the fix. Whole-word version
  matching and removal of featured credits pass that regression while keeping actual
  live/remix rejection. All 284 Core tests pass locally after the changes.

Prior Windows checks passed for 9215e79; they do not validate these newer changes.
Real Apple Music / NetEase runtime reproduction remains unverified. Do not describe
synthetic tests as proof that either player is fully fixed.
