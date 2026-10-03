# Repeated process-fixture PID publication repair

Checked on 2026-10-02 UTC against parent commit
`78efb49f9583494444695a9368318a2fd5e50d58`.

## Failure retained

[Windows CI 37035330166](https://github.com/airanluo-dot/DropSpace/actions/runs/37035330166)
failed in both language matrices at
`Ct2HelperAdapterTests.VerifiedHelperCompletesAndReleasesOwnershipRepeatedly`.
The synthetic helper exited with `-532462766`; both matrices had already passed
all 522 Core tests. The zh-CN Infrastructure summary was 651 passed, 1 failed,
4 skipped. App tests were not reached. These results do not qualify this candidate.

The closed-temp-file PID publication repair used `File.Move` without overwrite.
The helper package is intentionally reused for ten launches, so the PID file from
launch one still exists on launch two. Running the actual compiled fixture twice
in an isolated Linux directory reproduced this exact producer failure: first exit
0, then an unhandled `IOException` at the move because the destination exists.
This independently proves the fixture defect; it does not claim observation of
the original Windows child exception text, which the production adapter discards.

## Minimal repair

- Atomically replace the prior PID file with the already-closed sibling temporary
  file. Do not return to writing directly into the parent-observed PID file.
- Strengthen the existing repeated-ownership test by pre-seeding a stale PID file,
  checking that every launch publishes a positive numeric PID, and checking that
  no pending file remains after cleanup.
- Keep all existing output, cancellation, kernel-exit, executable-deletion and
  ownership assertions. No production application or inference code changed.

## Checks actually run

- Actual repaired compiled fixture: 10/10 repeated launches passed in an isolated
  Linux directory, starting with a stale PID signal, with valid output and no
  pending file left behind.
- Focused Infrastructure CT2 adapter tests: 37 passed, 1 Windows-native test
  skipped. This is not Windows validation.
- Before this fixture-only change, independently rechecked the 78ef source:
  Core 522 passed; Node scripts 361 passed; linked actual App service/rasterizer
  harness 113 passed (91 service tests plus 22 rasterizer tests).
- Fresh exact-commit Windows CI remains required. No release, merge, version
  change, semantic approval, or visual acceptance is implied by this repair.
