# Beta11 final reverse review and minimum cloud native build

## Reverse review result

The final pass covered candidate admission versus publication, selection/cache deadlines,
preview closure, request/cleanup ownership, preparation/configuration maintenance and default
Rules behaviour. Uncertain artists are retained only as selection candidates; the source winner,
preview and source cache still require strict matching. Full selection can compare different
artist credits supported by title/version plus duration or album evidence. This is not proof
that a model cannot choose a cover or different artist: real model identity accuracy is unknown.
The opt-in UI labels that boundary experimental, and never labels rules fallback AI-selected.

Rules retains the existing three-second provider budget/cache/early-return behaviour and does
not load or verify a model. AI modes reserve at most 500 ms within the same absolute three
seconds; Full starts no later than its first comparable fetched candidate. No source/candidate
change restarts the timer. Instant successful-decision cache reuse can work with no inference
budget left; it does not start another model operation. All preview success/cancel/error paths
close the per-query fence, and UI guards execute after dispatch.

The final pass found and fixed busy preparation doing an unnecessary complete 1.9/8 GB model
hash before rejecting admission. A cheap cold/busy/cleanup hint now precedes verification;
Prepare still performs the authoritative atomic admission check afterward. A translation arriving
during verification can race that hint; verification remains cancellable maintenance work and
does not establish admission. The runtime independently caps any selection request at 500 ms.
Presentation can return rules before native cleanup, whose owner retains operation/global gate
until exit and cleanup settle. No force-release was introduced.

New selector/KRC/artist-folding sources and the pinned dictionary/license are now mandatory
code-owned shipping-source fingerprint inputs. This strengthens coverage; it does not extend
prepare-corrective-release's reviewed-source override or edit approval/evidence records.
The prior approval is intentionally stale and must be rebound by the parent to the final source.

Actual checks: final Core/Infrastructure and actual AiLyricsService compile at zero warnings/
errors; gate script syntax and current scope extraction succeed. The three earlier new runtime
contract checks remain the recorded fixture checks, not rerun and not model evidence. No actual
Windows/WinUI/native build, real model decision or real lyric API request was performed here.

## Can unchanged engine libraries be reused?

Yes only when a trusted, verified Windows build tree still exists for each backend, with the
same pinned llama.cpp source, MSVC/runtime settings, CMake configuration and Vulkan SDK. The
worker links statically to llama-common and its variant-specific engine dependencies. With such
a tree, changing only main.cpp requires compiling that translation unit and relinking the worker:

```powershell
foreach ($variant in @('baseline', 'avx2', 'vulkan')) {
    cmake --build (Join-Path $trustedBuildRoot $variant) --config Release --target plain-lyrics-worker --parallel 4
    if ($LASTEXITCODE -ne 0) { throw 'Resident helper build failed.' }
}
```

This is a conditional incremental command, not an existing supported script option. Do not
use clean-first, rebuild completion/tokenizer targets or silently change CMake feature flags.
Verify the retained source tree, libraries/objects, toolchain and producer/cache provenance
before treating them as reusable. A directory containing similarly named libraries is insufficient.

Current retained runtime artifacts contain only six EXEs, LICENSE-llama.cpp and the manifest.
They contain no .lib/.obj/CMakeCache/build.ninja/source tree. BUILD_SHARED_LIBS is OFF, so those
static EXEs cannot be used as linkable engine libraries. Workflows do not retain native build
trees, and Build-AiLyricsRuntime.ps1 clears source/build/output and requires an explicit build
directory to be fresh. Therefore the current artifacts cannot provide helper-only compilation.

If no verified build tree exists, create the variant static libraries once in a fresh cloud
Windows producer using the exact pinned source/feature flags in Build-AiLyricsRuntime.ps1.
Build only plain-lyrics-worker targets for the three variants. Copy the unchanged baseline/AVX2
completion and tokenizer bytes from the exact previously reviewed artifact after its archive,
inventory and producer verification; do not compile those unchanged executable targets again.
Native provenance, notices and component verification remain required. If the old artifact has
expired or fails verification, this reuse is unavailable; never fetch an arbitrary replacement.

## Minimum producer and manifest route

The accepted producer workflow is specifically .github/workflows/release.yml. Current CI/release
jobs retrieve the exact old reviewed runtime and cannot manufacture protocol 2. A diagnostic-only,
explicitly nonpublishing producer mode in that trusted workflow is the compatible route; it
must skip the ordinary old-runtime consumption/publication jobs so the producer run can actually
finish successfully. A new unrelated workflow is not automatically trusted by the contract.
Implementing this mode is parent build work, not a reason to relax the override or forge evidence.

Freeze final checkout/source inputs before build. Verify pinned llama.cpp commit
7fe450e19305b828c199d602c23a8337aaa1f03b. Retain logs showing which engine targets were reused
or compiled and that each new worker was actually compiled/relinked. Before manifest emission,
repeat the existing frozen-input hash checks; run Test-AiLyricsRuntime.ps1 without ModelPath
for payload/hash/PE/startup verification. That check alone does not perform resident handshake.

The existing manifest schema/runtime ID/source commit, resident protocol 1/profile and build
flags stay intact. Protocol 1 remains the translation request/ready contract. Protocol 2 is an
additional per-request selection capability explicitly advertised as selectionProtocol:2 by the
new worker; the host refuses helpers without it. The capability is bound by the worker hashes
and resident.sourceSha256, not by changing old manifest metadata to claim support.

Update resident.cpu/avx2/vulkan sha256 and bytes from actual new EXEs, resident.sourceSha256 from
the existing ordered normalized CMakeLists.txt/gpu-policy.h/main.cpp hashing rule, and producer
to the actual new run/attempt/head/checkout. Preserve verified unchanged completion/tokenizer
digests rather than inventing new ones. Upload the exact eight-file inventory as
ai-candidate-runtime-<runId>-<runAttempt>; retain the new artifact ID/ZIP digest/manifest digest,
successful producer verification and file inventory. The parent approval must reference these
new facts and the final mandatory shipping-source fingerprint. Installed bundle inventories must
match the new artifact. Old failed runs and old model observations retain their original status.

## Contract and model-evidence delta

Unchanged: model IDs/files/hashes, llama.cpp commit, GPU policy, 4096 context allocation,
translation prompt and protocol-1 limits/sampler. Changed: native selector protocol 2 with
8192-byte metadata input, 32 generated tokens, 128-byte output; a separate prompt/output parser;
warm-only scheduler/preparation/cancellation state; decision cache and candidate identity/adapters.
Each request still clears complete KV/recurrent state and recreates sampler/history.

The new helper source digest is 64230bcee62d9b4598734a784af7b8285209189ad3c20dd8f4820cd48636c0c6.
The prior translation-model quality observations remain historical model/template evidence,
not successful observations of this new helper, selector task or current Windows runtime bytes.
The existing whole-manifest translation cache identity will change with the new helper inventory.
Mandatory new source inputs include LyricsCandidateSelection, ILyricsSelectionRuntime,
LyricsCandidateSelector, KugouKrcParser, ArtistCreditOrthography and its dictionary/license.

## Minimum real-model verification, still pending

Use the production PersistentPlainLyricsRunner and LyricsCandidateSelector on cloud Windows,
not a mock or an alternate raw Process.Start loader. Verify the selected installed official
1.8B Q8 file's size and SHA256 through the model package service. Prepare asynchronously, record
the actual backend and successful ready frame with selectionProtocol:2, then confirm warm
admission. An old helper's --version output or a fixture handshake does not establish capability.

Before invoking the model, independently annotate a tiny representative set from accurate
recording metadata: one general/cross-script artist-credit case with a real alternate recording,
one same-title different-artist or version case, and one uncertain/no-match case requiring
abstention. Preserve actual duration/album/aliases; do not invent aliases or edit duration just
to pass candidate admission. Include available translation and word-timing flags from the actual
fetched documents. Keep hard-excluded versions out of model input and record that exclusion.
These are selection metadata checks; do not send whole-song lyrics or redo translation benchmarks.

Run each annotated case once with a warm verified worker, unique candidate sets/IDs so the
decision cache cannot substitute for inference. Record exact input/protocol/model/runtime hashes,
raw output, expected vs actual ID/abstention, elapsed prefill/decision time if observable, and
whether the 500 ms deadline caused fallback. A returned Selected fixture is not this evidence.
Add one real in-flight cancellation after confirming a request was sent, followed by observation
of owned process exit/gate release and another permitted operation. Distinguish not-started,
timeout, unavailable and actual inference; do not report fallback as model success.

This small check can demonstrate executable capability and expose obvious quality/latency
failures; it cannot certify all artists, languages, CPU/GPU profiles or rapid-switch stability.
No such model experiment ran in the current cloud. Parent must report these limits and decide
whether to keep AI choices explicitly experimental, delay exposure, or collect further evidence.
