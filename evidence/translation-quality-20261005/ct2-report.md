# CT2 CPU experiment result

The pinned Marian/CT2 int8 route ran successfully on the shared original fixture.
It is fast and small on this cloud Linux CPU, but does not pass translation quality:
all three Korean fixtures have material errors for both targets, and mixed-language
lyrics have no supported route. No production source, menu, default, prompt, or cache
identity was changed. This is not Windows production validation.

## Execution and provenance

- Baseline delegated by the parent: `64457419`; branch
  `experiment/translation-quality-20261005`. The parent owns commits.
- Machine: AMD EPYC 9V74, Linux x86-64; cgroup limits 4 CPU / 16 GiB.
- Reused the unchanged `scripts/marian-model-qa/build_models.py` and `infer.py`,
  including pinned official Helsinki source revisions, byte counts, SHA-256 checks,
  safe tensor-only PyTorch loading, safetensors conversion, offline inference,
  four CPU threads, beam size four, and required EOS. CT2 never loads GGUF.
- All four official models downloaded and verified successfully; no gated model,
  legal acceptance, GPU, paid API, or user PC was involved. Converted components
  total **321,265,941 bytes (306.38 MiB)**. Converted models are in cloud scratch,
  not git. `ct2-conversion/conversion-manifest.json` retains every component hash
  and original revision/license; `ct2-conversion/source-models.json` contains every
  original download pin.
- Python 3.12.14; torch 2.8.0+cpu; CT2 4.8.2; Transformers 4.57.1;
  SentencePiece 0.2.1; safetensors 0.8.0; sacremoses 0.1.1.
  `ct2-install.json` and `ct2-torch-install.json` record official PyPI/PyTorch wheel
  URLs and hashes, `ct2-linux-py312.freeze.txt` records all versions, and
  `ct2-runtime-manifest.json` hashes installed native binaries. These Linux wheels
  differ from the existing Windows lock, so this is a separate environment.

## Actual measurements

The first English-target fixture is **05 (Chinese source)**, with one `zh-en` model
call. The first Chinese-target fixture is **01 (English source)**, with one `en-zh`
model call. Neither first-sentence result is a same-language bypass. Each separate
suite process decodes 14 supported rows, of which 10 need translation and four are
exact same-language passthrough. Fixtures 15/16 (`mixed`) are excluded from model
requests and explicitly recorded as unsupported for both targets.

| Run | Cold decode + imports/hash/model load | Warm real decode | Launch to cold result visible | Peak child RSS |
| --- | ---: | ---: | ---: | ---: |
| First → EN, fixture 05 | 1.406 s | 0.124 s | 1.447 s | 210.15 MiB |
| First → ZH, fixture 01 | 1.405 s | 0.127 s | 1.446 s | 208.79 MiB |
| Supported 14-row group → EN | 2.836 s | 0.963 s | 2.876 s | 441.41 MiB |
| Supported 14-row group → ZH | 2.565 s | 0.904 s | 2.607 s | 488.80 MiB |

All four child processes exited 0. Exact measurements and command lines are in
`ct2-inference/measurements.json`; each run retains requests, cold/warm results,
raw hypothesis tokens, source tokens, every pivot output, stdout, and stderr.
Conversion/download time is excluded. Cold means first load in a new process,
not an OS page-cache flush; warm means a second decode on retained instances,
not result-cache retrieval. Launch-to-result visibility and peak RSS are sampled
every 20 ms. The group is a supported subset of the 16-row synthetic song, not
successful completion of the full mixed-language song.

No other model inference or runtime build ran during these measurements. The parent
was still downloading/writing Hy7B weights concurrently; storage/cache pressure and
minor download CPU overhead may affect cold timings. This is one measurement per
process, not a statistically repeated latency benchmark. EN/ZH target groups use
different model routes and are not a controlled speed comparison between targets.

## Translation review

Preliminary manual review on **20 actual translations**: **11 pass, 3 warnings,
6 material errors**. Eight source-language bypass outputs and four unsupported
mixed target-case combinations are counted separately. This small original fixture
does not establish general translation accuracy. No unexpected source-language
copying, empty output, or extra explanatory prose was observed among translated
outputs. Cold and warm output strings were identical for every supported row.

| Source/target | Actual translations | Result |
| --- | ---: | --- |
| ZH → EN | 4 | 3 pass; 1 warning (`only` omitted) |
| EN → ZH | 4 | 3 pass; 1 warning (sister narrowed to younger sister) |
| JA → EN | 3 | 3 pass |
| JA → EN → ZH | 3 | 2 pass; 1 warning (light narrowed to lamp) |
| KO → EN | 3 | 3 material errors |
| KO → EN → ZH | 3 | 3 material errors |
| Mixed → EN/ZH | 4 target-case pairs | Explicitly unsupported |

Concrete blocking examples:

- Fixture 12 requires “my friend did not extinguish it; wind did.” The English
  result ends **“The wind's off.”**, losing wind's extinguishing agency; the Chinese
  pivot result **“我朋友没关灯 风也关了”** does not recover that relationship.
- Fixture 13 contrasts the speaker's sadness about an inability to say goodbye
  with sadness about his leaving. English changes the role/cause to **“It's not
  that he's sad he's gone, but that he can't say goodbye.”** Chinese shifts to
  **“并不是他伤心死了 而是他不能说再见”**. `伤心死了` is an idiom for extreme
  sadness; this review does not claim the model asserted literal death.
- Fixture 14 repeats “come back” **three** times and says “today, not yesterday.”
  Both targets keep only **two** repetitions and change/break the temporal clause.

The English pivot's raw errors are retained, so multi-hop damage can be traced;
the Chinese second hop is not presented as an independent Korean model result.
The long repeated mixed fixture 16 was unsupported, so this experiment cannot
claim preservation of its repetitions or long clauses.
`ct2-quality-review.json` contains every source, output, severity, and review note.

## Checks and remaining limits

- Existing Marian inference protocol tests: **4/4 passed**, recorded in
  `ct2-protocol-tests.txt` (fake-module tests establish protocol behavior only).
- Wrapper Python syntax compilation passed.
- Actual-output checks passed for exact fixture ID order, nonempty output,
  cold/warm equality, eight unchanged same-language controls, no mixed requests,
  and no reviewer expectations/adjacent context in requests;
  `ct2-validation.json` records these checks.
- Missing-EOS protection and route/prefix behavior are retained from the tested
  helper; all actual decodes completed with EOS.

After inference was complete, the parent requested reclaiming this experiment's
completed file pages before the next model loads. Only files under the CT2-owned
cloud scratch `models` and isolated `venv` directories were opened read-only;
each was fsynced, then given `POSIX_FADV_DONTNEED`. No global cache operation,
deletion, or access to other experiment files occurred. There were 27,220 advised
regular files totaling 4,016,567,905 bytes, with zero syscall errors. Shared cgroup
memory changed from 11,776,249,856 to 7,663,714,304 bytes while other downloads
continued. These shared before/after figures cannot isolate causal memory savings.
The operation is advisory and does not guarantee a cold cache.
`ct2-cache-advice.json` records model paths and aggregate totals; the full ledger
is retained in CT2 cloud scratch with its SHA-256 recorded in that evidence.

This evidence supports keeping CT2 as an independent candidate experiment. It
does not support promoting this four-model route to a production default. Further
work would need mixed-source handling, Korean quality improvements, independent
bilingual review, and actual Windows supervision/resource validation. Model
attribution/license records must be preserved for any future distribution; no
package or release was produced here.
