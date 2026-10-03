# AI semantic publication gate

The committed `release-approval.json` remains **pending** until the final production
capture and release review exist. Native startup, valid output, a passing build,
or a candidate experiment does not create semantic approval. Previously retained
candidate runs keep their original protocol, source, runtime and verdict.

## Owner-accepted experimental v0.3.1-beta.1

For this release only, `status: "owner-accepted-experimental-beta"` can authorize
publication without claiming semantic approval. Ordinary releases and Stable still
require the existing complete native capture and approved semantic review.
The owner accepted the disclosed timeout, incomplete 7B validation, default-off AI
and source/provider fallback in thread `01a0fe5b-d1da-7716-a8b3-b368757920f3`:
“不用添加别的什么了，直接就按照现在的AI模型发布吧”; the later request increased the
processing ceiling to ten minutes. This does not approve fabricated passes or waive
code, safety, cleanup, installation, upgrade, uninstall or package checks.

Prepare a normal non-publishing Release validation from the final exact candidate
with `capture_ai_evidence=false`. It builds and retains a fresh runtime artifact
and runs the existing native smoke/cancellation, managed tests, WinUI build and
installer/portable/MSIX gates. Do not reuse the runtime artifact from the failed
300-second capture run: artifact retrieval still requires a successful producer
run/attempt. A failed optional capture remains a failed run, with its output intact.
No extra model tuning or capture is required to accept unverified model limitations.

After that successful validation, record a repository review with the existing
exact `scope`, timestamps/expiry, reviewer, summary, `openDefects: []`, limitation
list, `runtimeManifest` and immutable `runtimeArtifact`. Set both `kind` and
`verdict` to `owner-accepted-experimental-beta`, and `semanticApproved: false`.
`userAcceptance` must contain the actual reference/time, `releaseVersion:
"v0.3.1-beta.1"`, and `acceptsIncompleteModelValidation: true`.

Every shipping model retains its exact ID/hash/size, `verdict: "unverified"`, a
limitation summary, and a `validation` array in this order: baseline/en,
baseline/zh-Hans, avx2/en, avx2/zh-Hans. Each observation has `variant`,
`targetLanguage`, and one honest `status`: `not-run`, `timed-out`, `incomplete`, or
`complete-unreviewed`. `not-run` requires null `configuration`, `runnerOutput` and
`technicalResults`. Executed observations require exact-byte `{path, sha256}`
references to their original configuration, runner output and operational results.
Keep historical source/runtime fingerprints and the actual 300-second budget;
never rewrite old evidence to 600 seconds. Timeouts/incomplete outputs must retain
`complete: false`. Cancellation, cleanup and unchanged-input checks must be true;
accepted limitations cannot waive those failures. The known 1.8B baseline English
32/43 and Chinese 33/40 timeouts remain failures; 7B completion remains unverified.

The live approval remains pending until this real record and successful runtime
artifact exist. This preparation changes no approval to a synthetic pass. The
workflow records `ai_publication_authorized` separately from
`ai_semantic_approved`; the experimental path sets only the former true. Final
runtime inventories, release file hashes, exact commit, main/actor restrictions,
expiry and immediate pre-publication checks apply to both paths. The existing
publication bridge remains the sole bridge to the canonical Release workflow.

## Shipping scope

The scope is derived from the actual catalog `All` list. It currently contains
`AiLyricsModelCatalog.ExperimentalPlain` (`hy-mt2-18-q8-plain-beta`) and
`ExperimentalLargePlain` (`hy-mt2-7b-q8-plain-beta`), the existing pinned Tencent Hy Q8 files. `Standard` and `Compact` remain legacy descriptors for local
inspection/removal; they are not shipping selections and need no new model approval.

The production contract is explicit:

- backend: `hy-q8-plain-beta-v1`
- prompt profile: `production-plain-hy`
- prompt version: `official-plain-per-line-v1`
- host mapping: `host-mapped-id-text-v1`
- acceptance: `unknown-copy-neutral-complete-song-v1`
- native caller: `PlainHyLyricsBackend` → `PlainHyLyricsCoordinator` →
  `PersistentPlainLyricsRunner.RunPlainAsync`

The gate reads the model ID/SHA-256/size, the plain protocol constants, the actual
shared resident `BuildArguments` implementation and compiled helper sampler vector, resource limits, runtime source,
release version and original `inputs/source48.json`. Resident startup arguments normalize only the model path to `$MODEL`.
The separately recorded `samplerArguments` come from the fixed native helper
source; its LF-normalized CMake/header/C++ source digest is also manifest-bound. No JSON grammar is
used by this profile. The current whole-song processing limit is 600 seconds, independent of audio duration; each native
call remains bounded at 60 seconds with the production 3 GiB (1.8B) or 12 GiB (7B) model budget.

The model sees only the actual official target/source template. Host IDs are
attached after inference. Unknown or same-target copied lines remain neutral;
the gate does not invent source-language labels or reject copying as a semantic
score. A complete source-only/no-useful result is technically recordable, while
its usefulness still requires review.

A code-owned allowlist hashes the relevant input/provider/identity/settings,
inference/cache, app composition, display, diagnostic-startup and packaging
sources. It also covers the actual production evidence harness. Tests require
coverage of every C# file in Core/Lyrics, Infrastructure/Lyrics,
App/Services/Media and App/Services/Diagnostics. New dependencies elsewhere must
be added during code review. Source text uses `sha256-utf8-lf-v1` for equivalent
Windows/Linux checkouts. Evidence and runtime hashes cover exact bytes.

## Finite final production capture

Create the bounded `qa/final-ai-review-031` branch at the exact final candidate
commit to start the existing Release workflow without publication. An optional
manual `capture_ai_evidence=true, publish=false` run has the same capture behavior.
Normal PR validation does not automatically repeat the larger capture. The QA
branch cannot enter signing or publication.

That run builds and retains a new complete runtime artifact, including
`LICENSE-llama.cpp` and producer metadata. After the ordinary app/native gates it
runs `scripts/plain-hy-production-evidence/Run-WindowsProductionEvidence.ps1`
against that runtime and the verified shipping Q8 model. It captures the complete
source48 fixture to English and Simplified Chinese on baseline and AVX2, cold
results, zero-inference cache replays, and real cancellation with confirmed
cleanup. Production time/resource limits apply; a failed/incomplete variant is
not silently omitted or retried into a pass.

The capture snapshots source scope, configuration, model identity and runtime
bytes before execution and verifies them again afterward. It preserves complete
runner-returned strings and the actual prompts/host IDs. The production runner removes
the runtime terminator, so these files are honestly called `runner-output`, not
unaltered process stdout. The capture writes no approval or semantic verdict.
Evidence, including failures, and the exact runtime artifact are retained for 60
days. The legacy diagnostic artifact without LICENSE is never a shipping input.

A change to the production model, GPU/runtime selection, streaming/progressive
behavior, argument builder, source scope or package after capture makes the old
capture insufficient for that new scope. Do not claim an untested runtime mode
was covered by a CPU run. This packet forces CPU selection for both CPU variants;
Vulkan binary hashes remain package-bound, but physical NVIDIA/AMD execution is
not asserted by a hosted CPU capture. Finish the selected implementation before the final
capture; this is one bounded reproduction pass, not an open-ended model search.

## Real review record

After reviewing actual outputs and clearing known defects, a trusted maintainer
can record a semantic review under `scripts/ai-model-qa/evidence/` with:

- `schemaVersion: 1`, `kind: "semantic-review"`, `verdict: "approved"`
- the complete current `scope`
- actual nonempty `reviewedBy` and `summary`
- `reviewedAt` and `expiresAt` as `YYYY-MM-DDTHH:mm:ssZ`; expiry is at most 30 days
  after the review, and neither future-dated nor expired at publication
- `openDefects: []`; accepted model limitations do not waive code, cleanup, data,
  lifecycle, audit or build defects
- `acceptedLimitations: []`, or concrete `{id, kind, summary}` entries where
  `kind` is `quality` or `latency`
- when limitations are accepted, a Beta release and `userAcceptance` containing
  the actual `reference` and `acceptedAt`; never invent user acceptance
- `runtimeManifest`: exact-byte repository evidence `{path, sha256}`
- `runtimeArtifact`: the immutable producer/artifact contract below
- `models`: exactly the shipping model list, in scope order, with identity,
  `verdict: "approved"`, rationale and native evidence references

This permits an honest Beta decision with disclosed limitations without claiming
perfect translation or turning accepted quality limits into a blanket bug waiver.
The validator checks the record's consistency, not the truth or authorship of the
judgment. There is no automatic review writer.

Each model's evidence references use `kind: "native-output"`, repository evidence
`path`, exact `sha256` and the pinned `fixtureSha256`. Each referenced schema-2
envelope contains:

- `kind: "native-output"`, exact `model: {id, sha256, bytes}`, `platform: "windows-x64"`
- `fixtureSha256`, `sourceFingerprintSha256`, `promptProfile`, `outputSchema`,
  `promptVersion`, `backendId`, `acceptanceVersion`, `samplerIdentity`,
  `captureMethod` and `executionLimits` matching the current scope
- `executedAt` in explicit UTC seconds, no later than the real review
- `runtime: {manifestSha256, variant, completion: {sha256, bytes}}`, with the
  actual resident `baseline` or `avx2` component from the reviewed manifest,
  plus `mode: "cpu"`, `profile`, `protocol` and `residentSourceSha256`
- `configuration: {path, sha256}` for the exact saved schema-2 configuration
- `technicalChecks`: both `coldTargets` and `cacheTargets` equal
  `["en", "zh-Hans"]`, `cacheAdditionalInferenceCalls: 0`, plus true
  `cancellationObserved`, `cleanupConfirmed`, `sourceIdentityUnchanged`,
  `modelIdentityUnchanged` and `runtimeIdentityUnchanged`
- exactly two `outputs` references, one per target, with `kind: "runner-output"`

Configuration records repeat the actual scope identities, `loadOnly: false`,
model fields, executable hash/bytes, runtime manifest/variant, fixture/source
fingerprints, `executionLimits`, actual startup `nativeArguments`, source-derived
`samplerArguments`, and `gpuEnabled: false` for this explicit CPU capture. A production label alone
cannot promote old candidate data. Schema-1 JSON/loader/candidate evidence is not
accepted as a schema-2 production capture. Missing provenance is never fabricated
afterward.

Each output file is schema 1, `kind: "production-runner-output"`, its target,
actual complete production outcome (`Translated` or `NoUsefulTranslation`) and
`calls`. Every nonblank fixture line occurs once in order with `lineId`,
`sourceText`, exact `prompt`, full `output` and captured timing information. The
gate verifies fixture/prompt completeness, not translation meaning. The plain
production path does not invoke the tokenizer; its bytes remain package-bound,
without claiming a native tokenizer call occurred.

Keep original files unchanged when copying a completed packet from CI into its
recorded logical evidence directory. The narrow
`scripts/ai-model-qa/evidence/** -text` Git attribute preserves native byte hashes.
Use only original synthetic fixture data here, never real user/provider lyrics.
Finally set the live approval's `review` reference and `status: "approved"` only
when the real review and final release conditions are complete. Root review owns
that decision; these tools leave the live record pending.

## Exact runtime and publication bytes

`runtimeArtifact` contains `schemaVersion: 1`, this `repository`, the allowed
`workflowPath`, positive `runId`, `runAttempt`, `artifactId`, `artifactName`,
`archiveSha256`, full `headCommit`/`checkoutCommit`, and a complete `files` inventory
of canonical path/SHA-256/byte-count entries, including manifest and license.
The manifest's `producer` must agree. `headCommit` is the Actions source head;
`checkoutCommit` is the actual checkout, which can be a PR merge commit. The later
review/publication commit can differ without changing the reviewed source scope.

Publication validates live approval before retrieving only that artifact ID.
It checks producer/head repository, workflow, successful run/attempt, source,
archive digest and expiry. Extraction rejects missing, extra, duplicate,
noncanonical or linked entries and verifies all extracted bytes. There is no
latest-artifact or rebuild fallback. The inventory core is backend-neutral; the
current explicit llama adapter requires the three legacy CLI binaries, the
three resident CPU/AVX2/Vulkan workers, manifest and license. A new runtime mode must explicitly extend its adapter/evidence coverage.

Portable smoke inspects embedded resources from a fresh private .NET extraction.
MSIX inspection reads the actual final packaged assembly. Installer lifecycle
requires its installed executable to equal the release portable. The internal
`runtime-publication.json` binds these observations to all three package hashes
and source commit. Stable signing verifies the unsigned record and repeats
inspections after signing/rebuilding. Immediately before publication, the gate
compares live approval, runtime inventory, final commit and downloaded package
bytes. Existing main-branch/actor/expected-commit/version/signing checks remain.

## Trust model and checks

This control trusts repository writers. A malicious writer could change code,
evidence and the validator; hashes are consistency checks, not authenticated
attestations. `reviewedBy` and user-acceptance references are honest accountability
metadata, not identity authentication. Retrieval uses the existing per-run Actions
read token; no signer keys, persistent credentials or external approval service
are introduced.

Useful local checks:

- `node scripts/test-ai-release-approval.mjs --print-scope`
- `node --test scripts/test-ai-release-approval.test.mjs scripts/test-ai-runtime-publication.test.mjs`
- `scripts/Test-AiRuntimePayloadInspector.ps1`
- `scripts/Test-ReviewedAiRuntimeArchive.ps1`
- Normal validator invocation must fail while the live manifest is pending
- `--runtime-manifest PATH` verifies exact runtime files; `--release-bundle DIR`
  verifies final byte bindings against `GITHUB_SHA`

A successful structural check never supplies semantic approval. A successful
capture is a review input; the final requested audit/build/release decision is
still separate.
