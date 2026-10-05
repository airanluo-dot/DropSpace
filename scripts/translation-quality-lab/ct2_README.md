# 【不准测试】 New runs are prohibited by the latest user instruction.

See [NO_TESTING.md](NO_TESTING.md). Existing evidence is historical; the final
entry-point guard was not tested.

# CT2 explicit-language CPU experiment

This Linux laboratory wrapper reuses the repository's unchanged
`scripts/marian-model-qa/build_models.py` and `infer.py`. It is separate from
production model menus, prompts, cache identities, AI source screening, and Windows
process supervision.

Sources are the four pinned official Helsinki models in
`scripts/marian-model-qa/models.json`. The build verifies each original byte count
and SHA-256, uses PyTorch `weights_only=True` and `trust_remote_code=False`, saves
safetensors, and converts to CT2 int8. It never loads GGUF through CT2.

The supplied single-language tags route `en`, `zh`, `ja`, and `ko` to targets
`en` or `zh`. Japanese and Korean use an English pivot for Chinese. The official
`>>cmn_Hans<<` token selects simplified Mandarin. `mixed` and other languages are
recorded as `route_unsupported`; no automatic language detection is claimed.
Same-language lines bypass all models and are reported separately from translation
quality. Fixture expectations and adjacent lines are never sent to this experiment.

Example, after the pinned build has completed:

```bash
python scripts/translation-quality-lab/ct2_run.py \
  --fixtures scripts/translation-quality-lab/fixtures.json \
  --manifest evidence/translation-quality-20261005/ct2-conversion/conversion-manifest.json \
  --python /workspace/scratch/translation-quality/ct2/venv/bin/python \
  --site-packages /workspace/scratch/translation-quality/ct2/venv/lib/python3.12/site-packages \
  --evidence evidence/translation-quality-20261005/ct2-inference
```

Run with an otherwise idle CPU. Each target first measures its first translatable
fixture in a new process, then the complete supported group in another new process.
The unchanged helper performs a cold and warm decode in each process. Cold includes
model loading, artifact hashing, and tokenizer imports; the OS page cache is not
flushed. Warm uses retained CT2 instances and performs real decoding, without a
result cache. The wrapper records process wall time, phase-result visibility from
process launch, and child peak RSS using Linux `/proc` at 20 ms intervals. These
visibility and RSS observations have sampling uncertainty and are not Windows
application measurements. Reuse a fresh evidence directory for every run.

Decoding remains the existing QA configuration: CPU int8, four intra-op threads,
one inter-op thread, beam size four, 511-token decode cap, required EOS, no input
truncation. Each hop has a 60-second watchdog; both phases share a 180-second helper
deadline. Raw input tokens, hypothesis tokens, each pivot output, stderr and stdout
are retained. Missing EOS or other failures remain failures; no partial translation
is silently accepted.

No model is bundled in git. Download and conversion time are excluded from inference
timing. The pinned source and converted component hashes, wheel URLs/hashes,
installed dependency versions and native binary hashes accompany the evidence.
The original `zh-en` model declares CC-BY-4.0; the other three declare Apache-2.0.
This experiment is not approval for distribution or publication.
