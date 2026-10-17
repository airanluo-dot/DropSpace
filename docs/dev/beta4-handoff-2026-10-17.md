# Next-turn Beta release handoff

The latest owner instruction ends this development turn at a saved checkpoint.
Publication must resume only in a new turn configured by the owner for Medium
reasoning. Do not continue feature development, start an extra test matrix, or
silently fall back to unrestricted validation. This turn did not prepare a new
version, create a release PR/tag, run a release build, or publish anything.

## Workspace and scope

- Repository: `/workspace/DropSpace`.
- Branch: `codex/beta3-app-evolution-20261017`; save this document and current
  changes as its final checkpoint. All product work is on this branch.
- Remote main and current public App Beta baseline:
  `1e858011b493c1bba10bb918192834fe29257ce5`, `v0.3.2-beta.3`.
- Beta 3 publication was confirmed at 2026-10-17 17:14:33 UTC. The prospective
  next version is `v0.3.2-beta.4`; recheck live collisions before preparation.
  `RELEASE_VERSION` and AI approval still name Beta 3.
- Existing open PRs 118, 119, 120, 121 and 123 are separate work; do not merge them
  as part of this release. Main requires a PR and the required build check.
- Current work includes widget timer controls/icons/placement, selection and
  navigation ownership, clipboard-card reuse and error retirement, download
  recovery, island input retirement, cold presentation and reusable-window closure.
  The final WidgetEditor partial fixes queued resize position and stale feedback.
  No model/CUDA bytes, lyrics inference or default channels were changed.
- The two latest investigations (clipboard-pause widget feedback and download
  cancellation-checkpoint cleanup) produced no edits or test executions. They
  remain deferred findings, not additional publication scope.

## Existing verification to reuse

The complete chronological ledger is `local-iterations-2026-10-17.md`; immutable
checked receipts are under `evidence/local-iterations-2026-10-17/`. Preserve its
failed, unknown and successful attempts; do not rewrite them as final-tree passes.

- Last complete Windows Debug App/XAML build: run
  [39067718911](https://github.com/airanluo-dot/DropSpace/actions/runs/39067718911),
  commit `e41a703cbfb2b32a40198c42b1f50ddc1159c22f`, zero warnings/errors.
  One native first-presentation/system-close scenario passed all implemented
  assertions. The diagnostic workflow still failed: two RTB pixel readbacks were
  blocked under the unchanged opacity predicate (App exit 2). Desktop PNGs and
  frozen layer opacity demonstrate visible timer content, and the recorded native
  close/reopen/shutdown lifecycle passed. This is not complete visual approval,
  physical input/DPI coverage, or release-producer qualification.
- Previous run 39067212418 remains unknown because launcher summary serialization
  failed. The two-line summary fix has its own before-failure/after-pass helper
  evidence. Do not retry that old run or erase its failure.
- Final WidgetEditor slice: two source-linked races reproduced before and passed
  after, each once. Its narrow WinUI reference compilation passed after one local
  fixture correction; that initial fixture compile failure is retained. It was
  not in the last full Debug build and must enter the normal required PR build.
- No development check is still running. No new release check was started.
- Artifact-download redirects in this workspace return 403. Do not repeat those
  downloads. Completed job logs are accessible using `gh run view RUN --job JOB
  --log`; direct job-log REST redirects can fail and expose signed URLs in errors.
  Original bounded PNG bytes and SHA/header bindings are already retained.

## Minimal publication entry for the new turn

Use `.agents/skills/dropspace-maintainer/SKILL.md` and the existing next-Beta route.
The owner has authorized next-Beta publication, necessary integration and minimal
validation, but deferred all such execution to the next turn.

1. Confirm the saved branch and current remote main/releases. Prepare English
   Beta 4 notes with Beta 3 as immediate upgrade baseline, update README/ROADMAP,
   and use `scripts/prepare-corrective-release.mjs` for the exact version and fresh
   owner-decision/review binding. Its explicit reviewed-source-path list must
   cover changed fingerprint inputs. Preserve historical model limitations and
   `semanticApproved=false`; do not manufacture new model acceptance. Package
   versions derive from `RELEASE_VERSION`; do not hand-edit Directory.Build.props.
2. Open the release-preparation PR and use its required complete Release App/XAML
   build. The normal next-Beta PR route executes zero functional cases. Do not
   bypass branch protection. Reuse already-passed focused checks above.
3. Merge the reviewed PR, then dispatch `ci.yml` against exact merged main with
   `compile_only=false` and `full_tests=false`. The existing next-Beta producer
   requires the identical-tree PR receipt, produces the packages, runs one
   isolated installer-payload scenario and performs required resource/runtime/
   approval/version/hash checks. Debug evidence cannot replace this producer.
4. Only after the producer succeeds, dispatch `release.yml` with `publish=true`,
   `expected_commit=<exact main SHA>` and `allow_unrestricted_fallback=false`;
   all diagnostic/model-capture flags remain false. Reuse the validated bundle;
   preserve required signing/deployment, approval and collision gates.
5. Verify actual public downloads, hashes/update manifests, Release prerelease
   status, website/Pages and updater API. Report the exact version, usable links,
   necessary check results and material limits in Chinese, then stop development.

No publication gate was relaxed in this turn. A later genuine release blocker may
be fixed only within that blocker; do not resume open-ended improvements.
