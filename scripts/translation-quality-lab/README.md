# 【不准测试】 Translation quality lab (cloud CPU, 2026-10-05)

Latest user instruction stops all new/repeated tests and model runs. See
[NO_TESTING.md](NO_TESTING.md). Commands below are historical reproduction records,
not authorization to execute. Entry points are disabled; final edits are untested.

Independent experiment branch from `64457419ae6fc8af15ab5542a402b7b871cbf9be`.
No production source, model menu/default, admission, cache, cancellation or publication changes.

The original 16-line synthetic song is fixed in `fixtures.json`. Every engine uses these
same source bytes and reviewer-only expectations, with English and Simplified Chinese targets.
Same-language lines are controls, not foreign untranslated failures. Mixed lines are real Hy
requests but explicitly unsupported by this language-pair CT2 adapter. This is a small,
open-label diagnostic, not a blind benchmark or a statistically reliable quality rate.

Experiments isolate:

1. Official Hy-MT2 1.8B Q8 vs the same official family/revision Q4_K_M: identical runtime,
   prompts, sampling, order and per-line fresh-process lifecycle. GGUF template, tokenizer
   metadata and tensor dimensions are checked separately. Publisher quantizations differ;
   we did not create new quantizations or alter tokenizer metadata.
2. Hy 1.8B Q8 frozen plain prompt vs an experimental previous/current/next JSON prompt:
   identical model/runtime/sampling/lifecycle. Only current line should be translated.
   The experimental protocol and identity include template and both neighbors. This code
   never uses the production cache. It is not production integration or admission testing.
3. Optional official Hy 7B Q8: same user prompt and sampling, separate resource allowance.
   Its parameter count, architecture, tokenizer and chat template differ from 1.8B, so this
   is a model-package comparison, not a parameter-count-only improvement claim.
4. Official Helsinki/Marian converted to CT2 CPU int8: reuses the existing isolated
   `scripts/marian-model-qa` converter and inference helper, with explicit source/target
   language tags and English pivot for Japanese/Korean to Chinese. Pair/tokenizer/routing,
   beam decoding, lifecycle and runtime differ from Hy. Speed alone is not a quality win.
5. TranslateGemma 4B: official repository metadata and a tiny unauthenticated config
   request establish the gate. No license acceptance, credentials, weights, third-party
   substitute or inference is performed. It remains blocked for authorized access.

Official model files, Python environments and runtime build products live outside Git in
`/workspace/scratch/translation-quality`. Checked-in provenance includes pinned upstream
metadata, license/card bytes, verified full-file SHA-256 values, converted-file identities,
runtime build logs and component hashes. There were no reusable Linux binaries or models
in the selected cloud environment, despite paths mentioned in historical reports.

Historical preparation/run commands (now disabled; cloud paths):

```bash
python3 scripts/translation-quality-lab/prepare_hy.py \
  --models /workspace/scratch/translation-quality/models \
  --evidence evidence/translation-quality-20261005/provenance
python3 scripts/translation-quality-lab/inspect_gguf.py \
  --models evidence/translation-quality-20261005/provenance/models.json \
  --output evidence/translation-quality-20261005/provenance/gguf-metadata.json
python3 scripts/translation-quality-lab/run_hy.py \
  --runtime /workspace/scratch/translation-quality/runtime/build/bin/llama-completion \
  --models evidence/translation-quality-20261005/provenance/models.json \
  --fixtures scripts/translation-quality-lab/fixtures.json \
  --evidence evidence/translation-quality-20261005/hy
python3 -m unittest discover -s scripts/translation-quality-lab -p 'test_protocol.py' -v
```

Build once from official llama.cpp commit
`7fe450e19305b828c199d602c23a8337aaa1f03b`. The recorded configure log supplies the exact
Release CPU AVX2/FMA/F16C configuration, with CUDA/Vulkan/server/tests disabled and native
CPU auto-selection disabled. Only `llama-completion` and its dependencies were built.
The executable and every shared library identity are recorded in the pre-run plan.

All Hy rows retain exact prompt/argv, raw stdout/stderr, stop reason, process RSS,
shared cgroup peak and cleanup confirmation. Limits are 60 seconds/line, 600 seconds/target,
3 GiB small-model RSS, 12 GiB 7B RSS and 14 GiB shared memory (16 GiB cgroup). A safety stop
or failed row is not scored as a translation pass. No production cache or song persistence path is invoked by this lab.

The fixed sampling and plain prompt bytes mirror `PlainHyLyricsProtocol` and
`LlamaCompletionRunner.BuildPlainArguments`; production code is unchanged. Model loading
uses the official completion binary defaults associated with those exact flags. This does
not measure the private resident Windows worker or its no-mmap/resource behavior.
Every Hy line creates a new process. Sum-of-lines time measures this laboratory song
strategy, not resident App time. First nonwhitespace stdout is observed at 20 ms polling;
it is not an exact first-token timestamp. Disk/page caches were not globally cold-cleared.
`POSIX_FADV_DONTNEED` on experiment-owned prepared files reduces preparation cache pressure;
it does not guarantee equal cache states. CPU inference runs were serialized across engines.
CT2 timed work overlapped background Hy7B download/write I/O; its cold timings retain that
limitation. No GPU, paid API, or user computer was used.

See the checked-in report for actual results and the experiment evidence for complete output.

The second neighbor-prefix arm completed before the stop instruction. Its isolated
protocol preserves the official current-line prompt as a suffix and uses a distinct
identity; it is a constructed experiment, not an official contextual template.
`run_mmap_7b.py` is implemented but was never executed. `validate_evidence.py` and
the new preface test were not run; existing source is retained, with no claim of
validation. Production defaults and the separate AI-identity work remain untouched.
