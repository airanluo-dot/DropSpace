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

The scope is derived from the actual production catalog (every model in `All`,
including its ID, SHA-256 and byte count),
the runtime build script's ID and immutable source commit, the prompt/cache
version, the release version, and the original synthetic `inputs/source48.json`.
An approval cannot omit Compact while continuing to ship it.

A code-owned source allowlist hashes the complete current lyrics input and
display chain: provider adapters/HTTP/registry/service, source parser, matcher and
normalization, SMTC session selection and bounded reads, track/query/cache
identity, target-language and settings resolution, MediaExperience/ViewModel,
the app composition root, OverlayWindow's dedicated expanded-island consumer,
next-line selection, Music/island secondary-text presentation, and the bounded
media soft-restart/retirement helpers. It also includes the prompt,
output parser, translation policy/circuit, runner/native boundary, coordinator,
work lifetime, cache path, runtime package/build inputs and fixed QA harness.
Regression tests mutate every listed real source file and independently require
coverage of every C# file in Core/Lyrics, Infrastructure/Lyrics and App/Services/Media.
Adding a helper or provider there therefore requires updating the code-owned list;
it cannot silently remain outside a prior approval. Other semantic dependencies
moved or introduced outside these directories must be added during code review.
Some related UI/settings changes conservatively invalidate approval too.
The record cannot select or remove entries from that list. Individual hashes plus
an aggregate SHA-256 over the ordered JSON file list must match exactly. The
source algorithm is `sha256-utf8-lf-v1`: read UTF-8, normalize CRLF to LF, and hash
without otherwise changing text. This handles the repository's Windows checkout
rules. Fixture, review JSON and raw evidence hashes cover their **exact bytes**.

Editing a prompt without bumping its version still invalidates approval. Editing
runner/service/work-lifetime/parser/cache behavior, provider response extraction,
query selection, media identity, language resolution or secondary presentation
also invalidates it. Changing
the model, runtime source, fixed fixture or release version requires a matching
new semantic review. Unrelated edits do not require a self-referential manifest
commit. Existing `expected_commit` binds the final reviewed `main` commit at
publication; the approval's scope binds the relevant code/data within that commit.

## Trust boundary and limitations

This gate trusts the maintainers who can submit and merge repository code. Its
purpose is to prevent accidental unapproved publication and reuse of stale or
mismatched quality evidence within that trust boundary. It is **not** a defense
against a malicious repository writer: that actor can change the manifest,
evidence, hashes **and the validator/workflow itself**.

`reviewedBy` is nonempty accountability metadata supplied by those trusted
maintainers. It is **not authenticated reviewer identity**, a digital signature,
proof of who authored a verdict, or proof that a human actually ran the review.
The publication actor check authenticates who starts that workflow; it does not
authenticate the semantic reviewer. Hashes and provenance envelopes establish
internal consistency, not truth, authorship or semantic quality.

No signer keys, secrets, external approval service, new permissions or additional
user approval ceremony are introduced. The actual authorized reviewer must be
recorded honestly. Do not invent a reviewer identity or user approval.

The final evaluated-runtime-to-shipping-runtime binary link is still incomplete;
see the explicit remaining work below. Current `pending` remains release-blocking.

## Recording a real review later

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
   - `runtimeManifest`: a repository-relative `path` and exact-byte `sha256` for
     the actual reviewed runtime manifest, containing pinned identity/source and
     positive byte counts/SHA-256 for baseline, AVX2 and tokenizer components
   - `reviewedAt` and `expiresAt` in `YYYY-MM-DDTHH:mm:ssZ` UTC format
   - `models`: one entry per shipping model, in scope order, each with its `id`,
     `sha256`, `bytes`, explicit semantic `verdict: "approved"`, and nonempty `summary`
   - each model's nonempty `evidence` list contains references with
     `kind: "native-output"`, repository-relative `path`, exact-byte `sha256`, and
     `fixtureSha256` matching the pinned original fixture. The referenced file
     must be a native provenance envelope, not an arbitrary nonempty text file
5. Set the manifest's `status` to `approved`, retain the exact scope, and set
   `review` to `{ "path": "scripts/ai-model-qa/evidence/<actual-review>.json",
   "sha256": "<actual-review-file-sha256>" }` only after that real review passes.
   Do not regenerate hashes merely to make a failed/old review pass. No approved
   example file or automatic approval writer is supplied.

### Native evidence envelope

Each `native-output` reference resolves to hashed JSON with:

- `schemaVersion: 1`, `kind: "native-output"`
- `model`: exact shipping `id`, `sha256`, `bytes`
- `fixtureSha256`, `promptVersion`, `sourceFingerprintSha256` matching the current
  approved scope, and `platform: "windows-x64"`
- `promptProfile: "production"` and
  `outputSchema: "production-id-text-json-v1"`; these code-owned identities also
  appear in the derived approval scope
- `configuration`: a repository-relative `path` and exact-byte `sha256` of the
  actual saved native-run configuration JSON. Its explicit `promptProfile` and
  `outputSchema` must match the two production identities above; `loadOnly` must
  be `false`. Its `modelId`, `modelSha256`, `modelBytes`,
  `executableSha256` and `tokenizerSha256` must match the envelope/runtime
- `executedAt`: explicit UTC timestamp no later than the actual semantic review
- `runtime.manifestSha256` matching the reviewed runtime manifest
- `runtime.variant`: `baseline` or `avx2`
- `runtime.completion` and `runtime.tokenizer`: actual `{ "sha256", "bytes" }`
  component identities matching the reviewed manifest and the stated variant
- `outputs`: nonempty references with `kind: "raw-output"`, `targetLanguage`
  (`en` or `zh-Hans`), repository-relative `path` and exact-byte `sha256`

`minimal-target-only` is a non-production prompt ablation. Even if its schema,
JSON structure or translation looks successful, it cannot support shipping
approval. Both the envelope and the separately hashed raw configuration are
checked: relabeling only the envelope as `production` cannot promote a diagnostic
run. Missing profile/schema identities fail closed instead of assuming defaults.
A future release-grade capture step must record these fields at execution time;
older diagnostic configurations missing the explicit output-schema identity are
not release-ready evidence. Do not modify retained raw configurations afterward
to manufacture this provenance. The candidate experiment registration JSON is
bound as a QA source dependency, but it is never an approval input.

Both target languages and both shipping runtime variants must be represented for
every shipping model. An AVX2 diagnostic cannot approve baseline support. Native
stdout is retained unchanged and checked for existence, nonempty content and exact
hash; changing a hash in an outer reference does not excuse inconsistent internal
model/runtime/prompt/fixture identities. The reviewer still reads that output for
meaning. Empty output, loader-only data and structural success are not approval.

This validates a retained evidence packet; it does not run inference or generate
an envelope, verdict or approval. The current candidate harness does not generate
release-ready envelopes automatically. A future capture step must snapshot the
actual source scope/configuration, runtime manifest and raw outputs together at
execution time; do not fabricate missing provenance afterward.

### Remaining evaluated-runtime to publication link (not implemented)

The build-time gate still compares only runtime schema, ID and source commit with
the reviewed scope. It **does not yet prove** that fresh shipping PE binaries are
the exact ones in the reviewed evidence packet. Rebuilding the same llama.cpp
commit can change baseline/AVX2/tokenizer bytes due to toolchain or build inputs.
Blindly comparing old and freshly rebuilt hashes could make legitimate publication
impossible; it must not be presented as reproducible without evidence.

The minimal proposed follow-on is to preserve one trusted runtime artifact from
the release-validation build, run the full required QA against those exact three
binaries, and publish using that already-evaluated runtime artifact rather than
rebuilding it. The consuming publication run would verify its originating
repository/commit/run/artifact identity, retained manifest and all component
hashes/byte counts, then verify the packaged runtime is unchanged. Missing,
expired, wrong-run or changed artifacts would stop publication. The review and
final `expected_commit` must also remain bound to the actual source scope. Artifact
reuse, retention and final payload checks require a separately reviewed workflow
change; none are silently simulated by this validator.

Until that link and genuine semantic QA are complete, the production record must
remain pending. The envelope consistency improvement alone does not close the
shipping-runtime provenance finding and does not approve either production model.

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
  scope; this remains a source-level check, with the binary reuse gap above

There is no manifest commit hash that would refer to itself. There is no automatic
semantic scoring, external approval service, CI trigger, or production-code change
in this gate.
