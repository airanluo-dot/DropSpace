# Conditional rule-only Beta11 candidate — not accepted or released

The user has not yet accepted deferring the two new AI selection modes. This branch is a reviewable alternative only; no version, release, approval or owner decision is changed. The complete three-mode branch `fix/beta10-chinese-lyrics-regression` remains intact at `12923a51e95c986f8bea339bc6bb48ebf4bad172`.

## Exact source boundary

The last commit with no new AI-selection product code is `f4228531667ff8c9d73dd6c8cf8940c7c3813eb2`. This candidate starts there, inheriting:

* `b6ae1b8`: same-language target-aware reuse, conservative legacy migration and observed A→B→A reload identity.
* `1f07f5a` / `6c36556`: admission diagnostics and stricter no-secondary legacy migration. The rejected artist whitelist from the earlier commit is removed by `f422853`, not resurrected.
* `469b9d0`: bounded native Kugou KRC translation/word parsing and provider business rejection handling.
* `f422853`: official AMLL contract/pagination/immutable TTML cache, general artist orthography plus recording-scoped aliases, QQ/LRCLIB/HTTP validation and cancellation checks.

Additional source transfers:

| Original change | Candidate commit/boundary |
|---|---|
| P2 `2398e18263cc7c8cf7363e350344a32b9aa7ccd9` | `90a8e5e`: semantic cherry-pick resolving the already-added same-language TTL branch; retains no-target **and** same-target complete-original reuse, foreign-target refetch and both original regression checks. |
| General title `fc672e4e4bc3f0dd6bdfec63f7c84c712fb9f44b` | `c0a449d`: cherry-pick only shared title/bilingual-base orthography and identity-guard test. No weak-candidate admission or selector API. |
| Mixed `ec1f0cb` preview changes | Extract only its exact MediaExperienceService original-preview/closed-queue fence hunk; add only the optional first strict-original callback to LyricsService. No candidate snapshots, source-cache bypass, expanded provider downloads, 2.5s reserve, weak matches or selection configuration. |
| `b827520` source binding hardening | Add only the four still-present nonselector input paths: ArtistCreditOrthography, TSCharacters, OpenCC license and KugouKrcParser. Every base mandatory source remains; no approval weakening. |

The callback signals the first strict original outside the candidate lock; MediaExperienceService checks track/settings/generation/token after dispatcher enqueue and closes preview publication after final source completion or cancellation/failure. Existing deterministic preferred→backup→remaining, translation-first policy, cache selection completeness and original-first AI translation fallback remain. The existing three-second translation lookup budget remains unchanged.

Excluded wholesale: selector contracts/settings/UI/localizations/tests, ILyricsSelectionRuntime/LyricsCandidateSelector, background model preparation, protocol-2 worker, producer/long-diagnostic workflow modes and support scripts. Existing opt-in AI **translation** remains as in Beta10; "rule-only" refers to source selection, not removing the established translation feature.

## Runtime and review binding

Static diff against Beta10 `4044baed6b1c393a815b91e1406a83e0995f1f94` is empty for MusicPage, selection-free settings, AiLyricsService, PersistentPlainLyricsRunner, PlainHyLyricsBackend, all helper native inputs, CI/release workflows, model catalog, translation prompt and prepare-corrective-release. Thus this branch needs no new helper build and uses the existing reviewed-runtime retrieval path.

Existing runtime source facts remain:

* Producer run `37095011004`, attempt 1; artifact `11264582019`, `ai-candidate-runtime-37095011004-1`.
* ZIP SHA `9d2b8086347172d12366c19872f815c00f99cc6a521c62f3d03929e575ebe941`.
* Manifest SHA `b492e2f0413449d69e0e37536b941e9e30c2b31c692732a5385ae8c9c6f9deab`.
* These are historical reviewed identities, not a new approval of this candidate. The old approval is stale for the changed source and remains untouched; source/owner approval and final Beta11 metadata must be prepared only after the user chooses the scope.

Current candidate source fingerprint (before any parent integration/version change):
`6bee79f2f6dae0be2e798903d66685119cbc72f1d35bbed6cd630a85a2466242`.
Recompute after integration; do not reuse the three-mode branch's source fingerprint. Historical three-mode design/progress documents inherited from the base are plans, not shipped capabilities; this candidate review defines its narrower implementation scope, conditional on user acceptance.

## Actual checks and limits

One cloud compilation of Core + Infrastructure + the actual linked AiLyricsService passed with zero warnings/errors (`/tmp/dropspace-rule-only-compile.log`). Static selector-symbol scan found no selector/settings/protocol-2 references in product/native/workflow files. The unchanged-runtime/UI/workflow comparisons above and JS syntax/diff checks passed. Previously passed functional/model/native checks were not repeated; no model download, native build, workflow dispatch or publication occurred for this candidate.

Linux cannot validate full Windows WinUI/XAML packaging here. Full App/Windows compilation and real rapid-switch behavior remain unverified on this candidate; no claim that upstream throttling or all real-device cases are solved. The original branch and its failed model evidence remain preserved separately. User scope decision and new source approval are still blockers to publication.
