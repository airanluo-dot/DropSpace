# Qwen3-4B Instruct 2507 selection diagnostic

The approved 4B cloud download and twelve selection-only calls completed. This profile produced useful positive decisions, but also one unsupported acceptance, order-dependent alias abstention and one empty answer. **It does not qualify for production enrollment on this evidence.** Both existing Hy translation choices remain intact. No Qwen translation option, model default, native producer, release, privacy text or Hy translation prompt changed.

## Immutable model and runtime

The parent delegation `01a10aae-4c29-707a-a9dd-661ab9ccd0ce` explicitly authorized this download and selection investigation. No approval timestamp or Sentinel identifier was invented. The supplied [GGUF revision](https://huggingface.co/ggml-org/Qwen3-4B-Instruct-2507-Q8_0-GGUF/blob/e6f794d44f9395d0184a966c27b5ae99ea356fcb/qwen3-4b-instruct-2507-q8_0.gguf) pins `e6f794d44f9395d0184a966c27b5ae99ea356fcb`, file `qwen3-4b-instruct-2507-q8_0.gguf`, **4,280,403,520 bytes**, SHA256 `ae916ede1c010a26955ee8ae2e908bf8815a3f135ec860439ab924701c69d5f1`. Actual `download.json` records successful byte/hash verification and 461.969 seconds for download plus verification.

The [official base model](https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507) template was checked at revision `cdbee75f17c01a7cc42f958dc650907174af0554`. This 2507 model is non-thinking: the single-user template ends at the assistant prefix without the older Qwen3 empty thinking block. The diagnostic renders this template explicitly and disables automatic conversation wrapping. It retains the official recommended temperature 0.7 / top-p 0.8 / top-k 20, min-p 0, repeat penalty 1, seed 42, CPU-only 2048 context and 32 output tokens. No grammar, forced prefix, answer repair, expected annotation or case-specific instruction was added.

The original generic completion AVX2 executable remains byte-identical: SHA256 `3fc3bd789f4d5a48eea3674182bbb9908eb7f44ca264f8bbf11c4bab526b9783`, original runtime artifact 11316610580, llama.cpp source `7fe450e19305b828c199d602c23a8337aaa1f03b`. Nothing was rebuilt. Each invocation uses the existing owned Windows Job/cleanup path, a diagnostic-only 6GiB process cap and 1GiB host reserve; production memory constants remain unchanged. Model/executable read leases persist throughout the run.

## Actual decisions

The six frozen families and identity instruction are unchanged from the [0.6B qualification](beta11-qwen-selection-evidence-readout.md). Instruction SHA256 is `abed5ae1373279fc7f2ee014e1f1efb05e8ecb9180a6d659c725943306354edb`. This run uses named fields and actual production spelling c0/c1. IDs are assigned by original ordinal, independent of expected annotations, and remain stable when order reverses. Durations and compilation names are synthetic corroborating metadata, not provider payload captures. Earlier full host replay established that the first three families can enter real candidate collection.

| Family | Expected | Original order | Reversed order |
|---|---|---|---|
| Hello, artist unknown, same compilation and duration; Adele versus Lionel Richie | NONE | **c0 (Adele), wrong** | NONE |
| 唯一, unproved target artist relationship; 王力宏 versus 告五人 | NONE | NONE | NONE |
| 晴天, Jay Chou; 林俊傑 versus 周杰倫 | c1 | c1 | **NONE, wrong** |
| Don't, Ed Sheeran; optional album/duration unknown | c1 | **empty, invalid** | c1 |
| Hello (Live), only Studio/Remix candidates | NONE | NONE | NONE |
| Hello (Live), Studio versus exact Live | c1 | c1 | c1 |

All twelve calls reached the EOS marker, native exit 0, no timeout or resource error, and confirmed owned cleanup. Eleven were syntactically valid; **nine of twelve** matched the declared decision. This is a focused diagnostic count, not an accuracy estimate. Empty output consisted only of a space and the EOS marker: it is not NONE or an accepted ID. The model wrongly accepted a recording when artist identity was missing; that family has strict/collected scores 9/9 for both candidates, so it is a real eligible ambiguity, not solely an outside-admission challenge. The alias family has strict 0 / collected 4 for both candidates and fails order stability. Version-only rejection is outside admission and is not credited as a host guard improvement.

## Observed latency and resources

The cloud Windows host exposed AMD EPYC 7763, 2 cores / 4 logical CPUs, approximately 16GiB physical memory. Per-call fresh-process wall time was **13.532–18.375 seconds**; prompt evaluation alone was **11.316–14.729 seconds**, across 161–182 prompt tokens. Output evaluation was 0–2537ms. The empty result sampled EOS immediately after prompt evaluation. Native performance `load time` is approximately the same as `prompt eval time` in these logs; those counters must not be added or treated as an independently measured model-startup phase.

Sampled peak RSS was 4,460,154,880–4,680,187,904 bytes (about **4.15–4.36GiB**), private bytes approximately 761–764MB. Cleanup was confirmed in 0.000243–0.003940 seconds after each call. This does not measure a production resident worker, GPU inference, actual desktop speed or cancellation during generation. Residency can avoid startup work but cannot promise to eliminate the measured prompt computation or repair the order-dependent decisions. Most calls exceed the currently proposed independent 12-second selection ceiling; increasing that ceiling would not resolve the semantic failures.

## Evidence and code delivery

[Cloud run 37271788924](https://github.com/airanluo-dot/DropSpace/actions/runs/37271788924) succeeded at head `f8e937019a2cdd94602cb4a6f5713552164f3ce4`. Artifact **11329465378**, `ai-selection-long-diagnostic-37271788924-1`, is **70,406 bytes**, ZIP SHA256 `a8fe5f9550083c8a03a5ee821302ed6ada886daf1357e201fc3c3f342a0c9cf4`. Size/SHA and archive paths were checked before extraction. The unchanged original ZIP, download identity, prompts, exact arguments, raw stdout/stderr, per-call results and summary remain in `artifacts/beta11-qwen4-evidence-37271788924/`. The model itself is not uploaded as an artifact.

Diagnostic implementation commit `f8e9370` adds embedded immutable model profiles, exact profile byte/hash checks, correct template rendering, selected diagnostic memory admission and the isolated `qwen4-evidence` workflow choice. The new diagnostic project compiled once locally with zero warnings/errors; Windows execution built and ran it successfully. Only twelve focused inference calls were added. No existing test suites, old passing checks, native builds or laptop runs were repeated.

Production remains the existing Hy selection experiment. Its single-model coupling, 500ms request cap, weak-candidate collection in only AiRanked, ordered confirmed-ID/public-source-priority protocol and unified role-aware resident profile still need coherent implementation and evidence before claiming the three modes ready. The independent native-three-second/background-selection budget and publication retirement fixes at 7c1ec87 / 0684c1e remain. This evaluated 4B profile cannot justify publishing Beta11 as a reliable selector; it also does not prove no model or general protocol can ever work. Further investigation should preserve these raw failures and avoid fitting a prompt to the expected answers.
