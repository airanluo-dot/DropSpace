# AI semantic publication gate

`release-approval.json` is deliberately **pending**. Neither current shipping model
has passed the semantic release bar. Do not turn native loading, JSON validity,
source review, candidate diagnostic completion, or this validator's test success
into approval. This change closes a publication-control gap; it does not fix or
approve translation quality.

## Where the gate runs

- Every normal Release PR/build runs the validator's synthetic regression tests.
  Pending, stale, or expired semantic approval does not prevent ordinary PR work.
- Only `workflow_dispatch` with `publish=true` validates live approval in
  `validate-release`, first before building and again against the build-produced
  runtime manifest. Failure stops that job.
- `publish-release` requires that exact job's `ai_semantic_approved=true` output,
  then rechecks the record and evidence immediately before publishing. No
  diagnostic job can emit that output. Existing version, main-branch, actor,
  signing and `expected_commit` checks remain in place.
- The validator always enforces approval when called normally. There is no
  `--skip`, permissive default, or `--publish=false` bypass in the CLI.

## What is bound

The scope is derived from the actual production catalog (every model in `All`),
the runtime build script's ID and immutable source commit, the prompt/cache
version, the release version, and the original synthetic `inputs/source48.json`.
An approval cannot omit Compact while continuing to ship it.

A code-owned source allowlist additionally hashes the prompt, output parser,
translation policy/models/circuit, runner, native boundary, coordinator, service,
work lifetime, cache path, runtime package/build inputs, and the fixed QA harness.
The record cannot select or remove entries from that list. Individual hashes plus
an aggregate SHA-256 over the ordered JSON file list must match exactly. The
source algorithm is `sha256-utf8-lf-v1`: read UTF-8, normalize CRLF to LF, and hash
without otherwise changing text. This handles the repository's Windows checkout
rules. Fixture, review JSON and raw evidence hashes cover their **exact bytes**.

Editing a prompt without bumping its version still invalidates approval. Editing
runner/service/work-lifetime/parser/cache behavior also invalidates it. Changing
the model, runtime source, fixed fixture or release version requires a matching
new semantic review. Unrelated edits do not require a self-referential manifest
commit. Existing `expected_commit` binds the final reviewed `main` commit at
publication; the approval's scope binds the relevant code/data within that commit.

## Recording a real review later

This is a repository evidence record, not a new user click-to-approve ceremony or
a signing service. `reviewedBy` must identify the reviewer who actually performed
the authorized semantic review; never invent user approval or a reviewer identity.
The ordinary code review/commit trust boundary also applies to this record and
validator. Hashes establish integrity and matching inputs, not semantic truth.

1. Use `node scripts/test-ai-release-approval.mjs --print-scope` to inspect the
   exact current scope. This command is read-only and cannot create approval.
2. Finish the required semantic release QA for **every shipping model**. Read
   meaning and language, including actions/objects, negation, quantities,
   attribution, source-copy behavior and the final full-song/independent-holdout
   evidence. Fixed-screen diagnostics alone remain insufficient. Structural
   checks never decide whether these semantic requirements passed.
3. Retain the actual fixed-fixture native output under
   `scripts/ai-model-qa/evidence/`. Only original synthetic QA input/output belongs
   here: no real user/provider lyrics, private external reports, or model weights.
   Preserve the full authorized semantic review and its other original-fixture
   QA evidence in the same reviewed evidence set. Nothing is downloaded by this
   validator. Missing, empty or modified referenced evidence fails closed.
4. A real semantic review JSON must contain:
   - `schemaVersion: 1`, `kind: "semantic-review"`, `verdict: "approved"`
   - `scope`: the complete current derived scope
   - nonempty `reviewedBy` and `summary` describing the actual semantic judgment
   - `reviewedAt` and `expiresAt` in `YYYY-MM-DDTHH:mm:ssZ` UTC format
   - `models`: one entry per shipping model, in scope order, each with its `id`,
     `sha256`, explicit semantic `verdict: "approved"`, and nonempty `summary`
   - each model's nonempty `evidence` list contains references with
     `kind: "native-output"`, repository-relative `path`, exact-byte `sha256`, and
     `fixtureSha256` matching the pinned original fixture
5. Set the manifest's `status` to `approved`, retain the exact scope, and set
   `review` to `{ "path": "scripts/ai-model-qa/evidence/<actual-review>.json",
   "sha256": "<actual-review-file-sha256>" }` only after that real review passes.
   Do not regenerate hashes merely to make a failed/old review pass. No approved
   example file or automatic approval writer is supplied.

Approval lasts no more than **30 days from the actual review**. This bounded
release window avoids carrying an old verdict indefinitely; source/data changes
invalidate it immediately even within the window. An expired record needs a
renewed real review, not just a changed timestamp. Missing, invalid, future-dated,
expired, or overlong timestamps fail closed. The current pending manifest has no
reviewer or approval timestamps because no qualifying review exists.

## Local checks

- `node --test scripts/test-ai-release-approval.test.mjs` tests valid and invalid
  records in disposable temporary directories using clearly synthetic data
- `node scripts/test-ai-release-approval.mjs` currently **must exit 1** because the
  real manifest is pending
- `node scripts/test-ai-release-approval.mjs --runtime-manifest <built-manifest>`
  additionally compares the build's runtime ID/source commit to the reviewed
  scope; the existing runtime build verification still verifies executable bytes

There is no manifest commit hash that would refer to itself. There is no automatic
semantic scoring, external approval service, CI trigger, or production-code change
in this gate.
