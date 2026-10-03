# Local lyric model evaluation

2026-10-01. Development evidence, not a broad benchmark or a Windows performance guarantee.

The default is Tencent's official Hy-MT2-1.8B Q4_K_M (1,133,080,448 bytes). The optional experimental compact tier is mradermacher's IQ3_S quantization (876,311,552 bytes), approximately 22.7% smaller. Both have the same 1.8B parameters. Compact is not advertised as faster.

## Fixed-case comparison

CPU-only llama.cpp v0.5.0, production contextual JSON prompt, seed42, four threads, strict line IDs, 60-second per-call bound. Six ten-line language-direction batches cover English/Japanese/Korean to Chinese and Chinese/Japanese/Korean to English.

| Result | Standard Q4 | Compact IQ3_S |
|---|---:|---:|
| Accepted batches | 5/6 | 6/6 |
| Usable lines | 50/60 | 60/60 |
| Negation/speaker reversals on 50 mutually accepted lines | 0 | 0 |
| Material lexical errors on those 50 lines | 2 | 2 |
| Mean latency on mutually accepted batches | 21.74s | 33.15s |
| Observed peak RSS | 2.15 GiB | 1.19 GiB |

Both models rendered the Korean light/fire ambiguity incorrectly in two targets. Compact additionally inserted an unsupported “warm” modifier before coat. The standard Japanese-to-Chinese batch continued malformed explanatory text and timed out; it was rejected, not silently shown. A simpler alternate prompt produced a speaker/polarity error in compact which the full production prompt corrected. Prompt changes require reevaluation.

## Pinned compact compatibility correction

Repository: `mradermacher/Hy-MT2-1.8B-i1-GGUF`
Revision: `9f5c7d98d8b625800775e6197e55c7ed38f2f33a`
File: `Hy-MT2-1.8B.i1-IQ3_S.gguf`
SHA-256: `46e67068820c68ac43ea9f304556c641e8c9dbf54efb14cef2b4dd540a30f12a`

The pinned quantization incorrectly declares EOS token3 (literal `$`). Token120020 is verified as the same CONTROL EOS used by the official Tencent model. Only this exact verified model hash receives the runtime argument `--override-kv tokenizer.ggml.eos_token_id=int:120020`. Strict output parsing remains intact; the model file and weights are not rewritten.

Windows native inference, CPU-variant performance and complete user-flow validation remain release gates. Runtime loading settings subsequently changed to explicit non-mmap loading, so the reported RSS/latency must not be used as guaranteed end-user limits. UI should say compact may use less memory but can be slower and may mistranslate.
