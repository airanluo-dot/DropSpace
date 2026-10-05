# Beta11 candidate selection: feasibility and boundaries

Status: design, not an enabled feature. User requested three modes and general artist handling;
the one-artist mapping in commit 1f07f5a must not be the final release solution.

## Existing model and runtime

The selected Hy-MT2 models are translation models. Tencent's model card describes translation
and translation instruction following, not music-recording entity resolution or verified artist
identity classification: https://huggingface.co/tencent/Hy-MT2-1.8B . Their entity-selection
accuracy is therefore unestablished. Downloaded weights do not imply a currently resident worker.

`PlainHyLyricsProtocol` has a frozen translation-only template, 1800-byte prompt ceiling and
2048-generation-token sampler. `PersistentPlainLyricsRunner` serializes operations, retains an
idle session for 60 seconds, allows 70 seconds including admission and 60 seconds per operation,
and shares the process-wide one-slot `LocalInferenceProcess.InferenceGate`. Switching model or
GPU setting drains the session; cancelling a running operation kills and drains its child.
Cold load, CPU fallback, concurrent translation, and cleanup can exceed three seconds. Reusing
this runner with a ranking prompt would be a new, unreviewed protocol and could kill a useful
resident translation session on a short ranking deadline. The existing translation prompt,
parser, and reviewed-source override must remain intact.

Consequently: optional local ranking is technically implementable, but the current translation
model cannot yet be promised accurate or fast for it. No ranking inference was run in this cloud
review and no timing or accuracy numbers are claimed. A separate reviewed selection protocol
and small adversarial fixtures are prerequisites, even if the same downloaded weights are used.

## Three modes

1. Rules only (default): deterministic identity evidence, version compatibility and duration
   constraints; then available target-language translation; then word timing. No model required.
2. AI assisted: keep an unambiguous rule winner; submit only unresolved, bounded alternatives to
   the local selector. Unavailable, busy, late, invalid or abstaining model results retain rules.
3. AI selection: compare every candidate retained from each allowed source's bounded catalogue
   response, after transport, format and unequivocal version/duration safety checks. A strict
   allowlist of source-local candidate IDs prevents invented recordings or extra network access.
   This does not mean waiting for every provider, loading every catalogue lyric, bypassing source
   permission, or accepting an explicit wrong artist because a model prefers it.

All modes use the requested priority: same recording/version, target translation, word timing.
The host validates identity and compares translation/timing capabilities lexicographically;
the model may rank identity ambiguity, but cannot trade away known identity conflicts for a
better lyric format. Ambiguous or conflicting recordings must be allowed to abstain.

## General artist rules

Keep raw publisher credits and provider artist IDs. Normalize Unicode compatibility forms,
case, permitted punctuation and whole-credit separators; never use a name substring or blindly
remove Latin prefixes. Use a pinned, licensed general traditional/simplified conversion table
with provenance (for example the relevant OpenCC dictionary), not a per-artist table. Conversion
can merge distinct names, so it supplies orthographic equivalence, not proof that two differently
credited artists are one entity. Title/version, known duration and authoritative identity still
apply. Japanese names and ambiguous shortened forms need conservative handling.

Read full-name aliases from the same provider artist record (`alias`/`alia`/translation-name
fields when supported), keeping the aliases attached to that artist ID. An alias from one
recording must never become a global name equivalence. Compare each complete credit against the
corresponding bounded provider alias set, preserving collaboration membership. Do not generate
an unbounded Cartesian product of alias variants or additional HTTP search variants. Structured
cross-script names can be accepted through provider-asserted aliases; an arbitrary shared Han
suffix is insufficient evidence. Unsupported aliases remain ambiguous and may use opt-in AI.

## Required architecture changes and budget

Current `ILyricsProvider`/`IProgressiveLyricsProvider` expose a selected document rather than
catalogue candidates. NetEase/QQ/AMLL prefilter and fetch at most three lyric candidates. A UI
setting alone cannot implement full AI selection: add a bounded candidate contract with raw
metadata, IDs, aliases, evidence, provider priority and discovered format capabilities; expose
catalogue collection separately from lyric fetch; retain source-local request/error policies.
Unfetched candidates have unknown translation/timing capability, not false capability claims.
Do not increase the existing lyric-fetch bound merely because more catalogue items are ranked.

Continue displaying a rule-validated original as soon as it exists. The existing three-second
native-translation budget starts at that moment and does not restart. AI runs, if eligible,
only inside remaining budget; use nonblocking admission and skip a busy/cold worker rather than
queue ahead of translation. A proposed subbudget is at most 500 ms for a warm idle selection
worker, subject to measurement; it is a product ceiling, not a demonstrated latency. Collection
and optional ranking must be concurrent where safe, cancel on track generation changes, and
never defer publication while cleanup completes. Late output cannot update a different track.
At expiry choose the best validated result already present, preserving the original. AI may
not extend the three seconds or create lyrics, translations, artist aliases or time stamps.

Cache selection separately with full track identity, mode, source configuration, target,
candidate evidence/revisions and selection protocol/model hash. A rules fallback after AI
failure must not become a permanent successful AI choice. Translation cache identity remains
separate. All logs remain metadata/content-free.

## Release implication

This is a substantial feature across contracts, all five adapters, native inference scheduling,
settings/UI and cache identity. It is not a safe small patch to quietly include in Beta11.
General authoritative-alias support and existing lyric defects can be fixed independently;
the AI modes require an explicit selection protocol, accuracy/latency evidence and Windows
runtime validation before release. No new feature is enabled by this document.
