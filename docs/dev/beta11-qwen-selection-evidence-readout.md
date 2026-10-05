# Qwen identity-evidence qualification: abstention alone is not a usable selector

The owner authorized continued **selection-only** investigation on 2026-10-05T02:21:54Z, Sentinel `fadd56401f7081919b24de527731e5c5`. Both existing translation models/user choice remain intact; Qwen lyrics translation is not opened. Production enrollment is conditional on actually reliable selection, not a workflow exit code.

## One frozen contract and discriminatory cases

The diagnostic now separates target from candidate data, quotes metadata values, identifies unknown explicitly, and requires recording title/artist/explicit version compatibility. Album and duration only corroborate identity; they do not establish an unknown cross-script artist relationship. All candidates may be rejected. The single instruction is unchanged across the positional-table, named-field and native-ID controls. No artist-name whitelist, case-specific hint, expected-answer injection, grammar, ID prefix repair or host rejection is counted as an AI answer. The official model, non-thinking template and recommended sampler are unchanged.

Six synthetic metadata families were declared before inference, each in original/reversed order. Equal durations and compilation names are deliberate ambiguity, not claimed provider captures. Actual `LyricsMatcher.Score` / `CandidateScore` are recorded before generation but never shown to the model:

| Family | Strict / collected scores | Required decision | Both actual orders |
|---|---|---|---|
| Artist missing; same Hello / Greatest Hits / 240 seconds, different artists | 9 / 9 for both | NONE: identity is not uniquely supported | NONE / NONE |
| Unproved artist relationship despite same title/duration | 0 / 4 for both | NONE | NONE / NONE |
| Independent Jay Chou / 周杰倫 alias, competing 林俊傑 | 0 / 4 for both | c106 | NONE / NONE |
| Known Ed Sheeran identity, optional album/duration unknown | wrong 0 / 0; correct 10 / 10 | c108 | NONE / NONE |
| Explicit Live target, only Studio/Remix | 0 / 0 for both | NONE | NONE / NONE |
| Explicit Live target with one exact Live candidate | wrong 0 / 0; correct 13 / 13 | c112 | NONE / NONE |

All twelve answers were EOS-complete exact `NONE`, successful native exit and confirmed cleanup. The two production-admitted ambiguity families really abstained correctly. **All six positive calls failed to select**, including an exact complete recording match; a degenerate all-abstain response does not establish useful order stability or AI selection value. The version-rejection boundary is outside production admission and is not presented as gain from a host guard.

A read-only host replay additionally verified full collection, rather than claiming numeric scoring alone proves actual admission. The immutable fixture metadata was projected onto one original line with the normal nonempty source CandidateId and current TrackIdentity that providers attach. Actual `LyricsService.QueryDetailedAsync` with a progressive fixture provider/AiRanked collected both candidates for each of the first three families (2/2/2), without network or inference. The model-visible metadata was unchanged. This supports that the first two abstention challenges and the alias-positive challenge are genuine eligible metadata situations, not merely candidates the source pipeline would discard.

## Controlled representation and production-ID checks

Because every table answer was NONE, three controls kept the identical instruction and fixture definitions while making each field explicit. Artist-missing still returned NONE correctly; known optional-metadata and exact Live positives also returned NONE incorrectly. Instruction equality and fixture equality were checked against the first downloaded contract. This did not establish a fix from clearer field binding.

A final single positive control replaced long diagnostic identifiers with the actual production c0/c1 spelling, while retaining the instruction, named-field representation and fully matching recording metadata. c0 was wrong Studio, c1 exact Live. The answer was again complete NONE, **not c1**. ID projection was ordinal and independent of expected annotations; the annotation was mapped only after generation. The qualifier therefore does not blame task failure solely on long diagnostic IDs or positional columns.

The preceding looser representation produced useful alias positives but guessed on missing identity and accepted a conflicting version. The new general contract avoids those observed unsupported acceptances yet rejects every positive control. The evaluated Qwen3-0.6B Q8 profiles have **not simultaneously demonstrated reliable acceptance and abstention**. This is evidence about the evaluated model/task representations, not a proof that no possible training or prompt could ever work. Rewriting instructions against these expected answers, forcing an ID, extracting prefixes, or calling a safe rules fallback “AI success” is not a justified enrollment route. No production Qwen selector/three-mode ranked protocol is added on this evidence.

## Actual speed and resource observations

The twelve-call table run took 2.415–8.083 seconds per call including fresh load; prompt evaluation alone was 1.472–6.603 seconds, output evaluation 21–149ms. Named-field controls took 8.842 / 5.930 / 2.591 seconds, and the production-ID control 8.734 seconds. The table prompts were 602–637 bytes, named-field controls 641–676 bytes; shorter bytes do not guarantee subsecond prefill. A resident worker can avoid startup, but cannot repair decision semantics or promise to remove measured prompt computation. Runs were on exposed 2-core/4-thread cloud EPYC 7763 or Xeon 8573C hosts, so these are observations, not controlled speed regressions or desktop guarantees. Sampled RSS remained below about 913MiB with the 2048 context and unchanged 3GiB job cap/1GiB host reserve.

## Immutable results and deliverables

* [Table run 37255404860](https://github.com/airanluo-dot/DropSpace/actions/runs/37255404860), head `e2e0f72e2a8e6793032703b857442f63588e2bb9`. Artifact 11321699183, 69,469 bytes, ZIP SHA `614f53f7f4d391d562d8b8e2e2d046bd2378775c11d1687bfc1a3d37598dab6e`.
* [Named-field run 37255753564](https://github.com/airanluo-dot/DropSpace/actions/runs/37255753564), head `5638c8d2a4654567e771a74d05bb9372ff253e0a`. Artifact 11322646890, 29,214 bytes, ZIP SHA `7b1b600dde58cd8953a3e79f151e8979a005d6183c7647c11de7006ae8f0560e`.
* [Production-ID run 37256012932](https://github.com/airanluo-dot/DropSpace/actions/runs/37256012932), head `e7c15bfd8fc1171df1279df65b12af22228165aa`. Artifact 11323150230, 20,185 bytes, ZIP SHA `d64c0424a1fba15571d97f3d366b16584cc9bb0e28ae915805216f4ab20e1ad5`.

Each archive was size/SHA-verified before safe extraction and is retained unchanged under `artifacts/beta11-qwen-identity-evidence-37255404860/`, `artifacts/beta11-qwen-labelled-control-37255753564/` and `artifacts/beta11-qwen-native-id-control-37256012932/`. The first folder also contains `full-admission-replay.json`; its local compile/execution log is `/tmp/dropspace-identity-admission-replay.log`. Only sixteen focused inference calls and the small host admission replay were added. Actual Windows diagnostic builds/execution passed; no existing suites or old passing tests were rerun, no native build or laptop was used, and no production model/default/prompt/privacy/reviewed-source override/publication changed. Existing independent native-three-second/background-decision/UI-retirement fixes remain at 7c1ec87 / 0684c1e.

The next justified direction is either independently qualify a more capable general selector model with explicit resource/download approval, or obtain source-declared recording/artist identity evidence inside the existing source stage to reduce unresolved ambiguity. Neither is silently implemented here. Current two-model lyric translation remains separate. General identity confirmation, useful positives, order stability and public source/translation/word-timing priority still need to succeed together before a production selector is claimed ready; a timer increase or new resident profile alone cannot establish that.
