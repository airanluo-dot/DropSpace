# Marian private-helper candidate QA

This isolated Windows experiment does not integrate a model into DropSpace or approve publication. Content status is PENDING SEMANTIC REVIEW. It uses original synthetic lyrics only, with the same frozen 48-line song and independent 12-line holdout as the LLM candidate QA. Model and dependency pins are in `models.json`, `dependencies.json`, and the hash-locked requirements file.

Run with Windows x64, Python 3.11.9 and .NET 10.0.401:

```powershell
./scripts/marian-model-qa/Run-WindowsMarianQa.ps1 -OutputDirectory C:/qa/evidence -ModelDirectory C:/qa/models
```

The independent workflow only reacts to its own scripts/workflow and never cancels a running experiment. It has read-only repository permission and no publication steps. Dependency installation and conversion run first and are excluded from inference measurements. Original publisher PyTorch tensors are loaded with `weights_only=True`, `trust_remote_code=False`, and local-only access; the CT2 converter reads a newly saved safetensors model. Conversion manifests retain original repository revisions, licenses, byte counts/hashes and every converted component hash. Runtime native component hashes and installed versions are recorded separately.

Each target launches a private Python child owned by the production Windows Job implementation, capped at 3 GiB. No HTTP server is created; the inference helper rejects outbound socket connections. CT2 CPU thread count is four. Both cold and warm passes together must fit an active 180-second target deadline, including imports, artifact hashing, model loads, and all pivot hops. Each hop has its own 60-second watchdog. Cleanup is independently observed for at most ten seconds and unconfirmed cleanup blocks later work. This is stricter than giving cold and warm separate 180-second windows.

Source fixture language tags route EN/JA/KO/ZH through four official Helsinki models. These supplied tags do not demonstrate automatic source-language identification. English and Chinese source-only lines bypass models. Japanese and Korean use English as an intermediate for Chinese output; each hop's exact source tokens, hypothesis tokens and raw text are retained. The English-to-Chinese route uses the official `>>cmn_Hans<<` token (ID 5), source SentencePiece for encoding and target SentencePiece for decoding. Model position limits are 512; output is capped at 511 and missing EOS fails rather than accepting truncated text.

Cold means first load within a new process, not an OS page-cache flush. Warm means a second real decode on retained CT2 instances; it is not a result-cache test. JSON IDs are assigned by the adapter according to batch position, then checked with the existing strict LyricsTranslationOutput validator. Structural success cannot establish translation accuracy. The cancellation check waits for an actual decode-start signal before cancelling the supervised child. The source-only check requires zero model calls and unchanged lines.

Original timestamps/text remain in input documents; translated ID arrays are separate evidence. Same-language rows are deliberately copied. No holdout review notes are sent to the model. Small understandable lexical differences are warnings under the user's revised standard; wrong target language, foreign copying or a clear meaning reversal remain blockers. Real Windows results and content review are still required.

Four original model weights total about 1.15 GiB before conversion. This is neither the installed runtime size nor peak process memory. The zh-en model declares CC-BY-4.0; the other three declare Apache-2.0. Preserve attribution and original model notices when preparing a future package. This QA downloads official registry/vendor packages only and does not use MiniSBD or its AGPL dependency. PyTorch/Transformers are build dependencies; this first QA also retains Transformers solely for exact Marian tokenizer behavior, which is an explicit future packaging cost.
