# Windows CI build deduplication

## Implemented boundary

`ci.yml` validates every PR to main, the merged main tree, and explicit manual
requests. Agent branch pushes do not start a second Windows CI run. There are no
path filters that could leave required checks pending on metadata or website PRs.

One `produce-windows` job performs the common policy checks, locked restore,
native runtime build, Core/Infrastructure/App tests, WinUI build, portable EXE,
installer and lifecycle test, MSIX, and identity package. Release contract checks
previously covered by the automatic Release PR lane also run here, without model
downloads or production song capture.

The two existing `Build and test (x64, en-US)` / `Build and test (x64, zh-CN)`
required checks depend on that producer and perform their own language smoke.
They run even when the producer fails, is cancelled, or is skipped, and explicitly
fail unless it succeeded. Each downloads the producer's exact artifact ID and
checks a producer-output SHA256 of the handoff record, then the EXE SHA256/size and
repository/workflow/run/attempt/checkout identity. The record is immutable and
both consumer summaries identify the original producer and current consumer.
This handoff accepts only the same run, attempt, and commit; it is not metadata
reuse. Missing artifacts, digests, or identity mismatches fail the check.

`Test encrypted share worker` keeps its existing required name and behavior.
The ruleset and its three required check names are unchanged.

`release.yml` runs only by explicit `workflow_dispatch`. A manual validation
request has its own concurrency group, separate from publication, so it cannot
occupy the publication queue. The published tag's original producer records are
untouched. The explicit publication lane still retrieves the exact reviewed AI
runtime with `Get-ReviewedAiRuntime.ps1`; it does not rebuild or substitute that
runtime. Publication still builds and validates the final packages, installation,
upgrade, uninstall, both locales, runtime payload inspections, hash manifests,
signatures where required, current approval/evidence/expiry, expected main commit,
actor restrictions, immutable tag/release, and final-package byte bindings.

The publication bridge still deduplicates `create` and `push(created=true)`.
Explicit website dispatch remains because releases created with `GITHUB_TOKEN`
do not trigger another release-event workflow. Evidence capture remains available
through `capture_ai_evidence=true, publish=false` rather than an automatic QA
branch push.

## Work eliminated

- An agent branch update with an open PR previously scheduled two Windows CI runs,
  each with two identical builds, plus the Release PR build for matching paths:
  up to five common builds for that update. It now schedules one common producer
  and two locale smoke consumers.
- Each Windows CI run builds the runtime, WinUI, EXE, installer, MSIX and identity
  once and runs each common suite/lifecycle once instead of once per language.
  Package uploads also occur once; locale diagnostics remain separate.
- A main merge no longer starts the automatic non-publishing Release build. The
  merged-tree Windows CI producer remains. A true explicit release still performs
  its full final-byte validation in its separate publication lane.

No wall-clock saving is promised; artifact transfer and the two locale runner
starts remain. There is no cross-run cache hit or metadata-only skip in this patch.

## Why full metadata-only package reuse is deferred

The AI approval source scope is a deliberately limited inference/evidence scope,
not the complete application build and validation input graph. It cannot prove
unchanged application bytes, tests, packaging, governance, or toolchain.
`runtime-publication.json` schema 1 binds final packages to `sourceCommit`, and
the publication gate requires that commit to match the current publishing commit.
Relabelling an old bundle with a new commit would erase its provenance. Stable
signing also changes final bytes and rebuilds the installer, requiring fresh
inspections. Existing release artifacts do not contain a complete package reuse
contract. These constraints require an explicit contract extension, not a path
filter or an early successful exit.

## Next smallest safe increment

First add a record-only full producer contract to the existing producer and
release lanes. It should include two independent input fingerprints (build inputs
and validation inputs), exact toolchain versions plus runner image, architecture,
all package inventories/hashes, and repository/workflow/head/checkout/run/attempt/
artifact ID/archive digest. Start conservatively with the entire tracked tree;
any future metadata exclusion must be proven non-input to both graphs. Source,
resources, version, lock files, project/build/package/test/policy scripts, native
helpers, toolchain and architecture changes must invalidate the relevant reuse.
Runner-label equality alone is insufficient because hosted images change.

After trusted successful producers contain those contracts, add a reader that
verifies the exact same-repository trusted successful run **and attempt**, artifact
expiry, archive digest and extracted file inventory, then compares both complete
input fingerprints and environment identities. Unavailable, untrusted, mismatched,
or corrupt candidates must fall back to a real build. Fork/PR artifacts are not
trusted release producers. Add mutation/fallback tests before enabling reuse.

A separate consumer contract must retain the original bundle `sourceCommit` and
producer unchanged, record the current commit/consumer, and bind that relationship
to the matching input proofs. Extend the publication gate to validate both records;
do not rewrite the old binding. Approval or evidence changes must recheck current
scope, evidence bytes, expiry and authorization; required signing secrets must still
be validated. Release-note changes must regenerate and test the update-manifest
summary from current notes and actual package hashes. Keep final installation,
upgrade, uninstall, final-byte/hash/signature and publication authorization gates.
Enable reuse only after the producer-to-consumer chain is tested end to end.
