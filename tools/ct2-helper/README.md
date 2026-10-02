# Private CT2 helper (not production-selected)

This directory implements a hidden, one-request Windows x64 stdio boundary. It has no listener,
service, downloader, model resolver, or production registration. The shipping lyrics backend is
unchanged. No runtime binary, real dependency inventory, or model package is supplied here.

## Offline build contract

Use an **already populated, reviewed offline wheelhouse** and an existing trusted CPython 3.12 x64
installation on Windows. Select its exact `python.exe` path and independently reviewed SHA-256;
the script never chooses `python`/`py` from PATH or installs into the global interpreter. The
installation, its stdlib, and bundled ensurepip bootstrap are prerequisites that must already be
trusted. Hashing `python.exe` alone is not verification of that whole installation.

`dependencies.json` must satisfy `dependencies.schema.json`. It is the reviewed **exact version
allowlist**, with one name, stable version, local wheel basename, byte length, and SHA-256 per
package. It must contain exactly the eleven explicitly allowed build/runtime distributions;
Argos Translate, Flask, MiniSBD, Torch, Transformers, download clients, and arbitrary additions are
rejected. Versions are not guessed or resolved here. Review the inventory before supplying it.

The script also checks duplicate JSON members, wheel filename/ABI/platform, embedded package
name/version, archive member safety, hashes, missing and extra wheelhouse entries, and reparse
paths. It installs only those hashed wheels into a disposable isolated venv using `--no-index`,
`--no-deps`, `--no-cache-dir`, `--only-binary`, and `--require-hashes`, then checks the dependency
closure and actual installed versions. Every external process exit code is checked. No network,
installer, or build has been run as part of the source-only hardening work.

Example command shape (substitute already reviewed local paths and digest):

```powershell
.\build.ps1 -PythonExecutable C:\ReviewedPython312\python.exe `
  -PythonSha256 <reviewed-python-exe-sha256> `
  -Wheelhouse C:\ReviewedWheels -Inventory C:\ReviewedInputs\dependencies.json `
  -Output C:\Builds\new-ct2-runtime
```

The output directory must not already exist. The output contains `engine/` and a separate
`engine-files.json`, listing every file's relative path, size, and SHA-256, including nested native
libraries, Python DLLs and archives. The generated list is evidence for assembling the private
package manifest; it is not automatically a reviewed trust anchor.

### Single-process packaging is mandatory

PyInstaller must use `--onedir --console --noupx`. Its **entire** output directory is the engine
payload. `--onefile` is forbidden: its extraction bootloader creates a child process and conflicts
with the app's mandatory Windows Job `ActiveProcessLimit = 1`. Do not relax the Job limit, ship only
the EXE, or substitute an unpacking launcher. The app launches the console binary with
`CREATE_NO_WINDOW` and uses its existing memory, timeout, cancellation and cleanup boundary.

The private package layout is:

```text
package/
  manifest.json
  engine/dropspace-ct2-helper.exe
  engine/_internal/...
  model/...
```

The app receives an independently reviewed SHA-256 for the **complete manifest**. That manifest
binds the complete engine and model inventories, fixed model directory layout, source/target
SentencePiece paths, language route, paired tokenizer/decoder protocols, and nullable target
prefix. The C# boundary rejects missing, extra, altered, escaping, and reparse files and retains
verified read locks until process cleanup. Neither a caller-supplied model hash nor a bare EXE hash
is adequate. Refer to `private-package-manifest.schema.json` for the final manifest shape. Package
metadata and complete payload hashes, including both tokenizers and prefix choice, form cache
identity. Do not infer a tokenizer by scanning filenames.

## Version 1 wire protocol

Exactly one UTF-8 JSON object followed by LF and EOF is accepted. The JSON itself is limited to
256 KiB; the final LF is a framing byte outside that limit. CRLF, BOM, trailing frames, duplicate
members, unknown/missing fields, non-finite numbers, malformed UTF-8 and lone surrogates fail.
The response is one UTF-8 JSON object without a trailing LF, at most 256 KiB. No response is
published until the whole translation succeeds. Diagnostics go only to stderr; before importing
native modules the helper redirects both CRT fd 1 and the Win32 stdout handle, retaining a private
protocol fd. Redirection remains active through native shutdown. The app separately caps stderr.

Required request fields:

- `version`: integer `1` (booleans and floats are rejected)
- `source`, `target`: canonical supported route, one of ja→en, ko→en, zh→en, en→zh, ja→zh, ko→zh
- `modelDirectory`: validated absolute local CT2 model directory
- `sourceTokenizer`, `targetTokenizer`: separately validated absolute SentencePiece file paths
- `targetPrefix`: explicit null or one reviewed ASCII token `>>...<<`, at most 64 characters
- `tokenizerProtocol`, `decoderProtocol`: one permitted pair below
- `lines`: 1–2,048 `{ "id": integer, "text": string }` objects; IDs are unique, nonnegative Int32

Each input/output text is nonblank valid Unicode and at most 4,096 UTF-8 bytes. C0 control characters
other than tab are rejected. Each source row is at most 512 tokens including any prefix and EOS;
the total input and total output are each capped at 16,384 tokens. CPU inference uses one inter
thread, four intra threads, batches of at most 16 rows, INT8, and a 512-token decode ceiling. Input
truncation is disabled. Output without the terminal `</s>` is rejected as potentially partial;
that EOS is removed before target SentencePiece decoding. IDs and order are preserved exactly.
The parent can terminate a stalled read/import/inference; the helper creates no child process.

### Tokenizer and decoder pairs

- `ArgosSentencePiece` + `Argos`: source SentencePiece encoding, no language prefix, target
  SentencePiece decoding. `targetPrefix` must be null. Packages with one shared SP model explicitly
  name that same reviewed path for both fields; sharing is never inferred. No sentence splitting,
  Argos runtime, or MiniSBD dependency is introduced.
- `HelsinkiSentencePiece` + `HelsinkiOpus`: source SentencePiece encoding with an explicitly
  supplied prefix token, if any, and final Marian `</s>`; target SentencePiece decoding after
  removing terminal EOS. A dedicated bilingual package may explicitly use null. Multilingual
  tags such as `>>cmn_Hans<<` must come from reviewed package metadata; a tag is never synthesized
  from `zh`, `en`, or another route code.

This is a deliberately narrow raw-SentencePiece protocol, not an implementation of every Marian
or Argos tokenizer wrapper. Packages requiring additional normalization, special-token handling,
or another decoding rule must not claim this protocol. Real token parity against the original
package tokenizer and actual translation quality must be established separately for every model.

## Checks and remaining acceptance

Run the stdlib-only protocol/build-validator suite without installing any dependency:

```text
python -m unittest discover -s tools/ct2-helper -p "test_*.py" -v
```

Tests use fake native modules and synthetic wheels. They cover bounded framing, type/schema/ID
rejection, separate tokenizer selection, explicit prefix behavior, token ceilings, EOS failures,
protocol-only stdout, offline inventory validation and recursive runtime inventory. They do not
prove native translation quality, Windows process containment, or a successful frozen build.

Before any private native acceptance, supply the reviewed offline CPython/wheels and complete
model package, build on Windows x64, compare actual tokenization/decoding with the model's original
reference, and run the frozen executable inside the unchanged one-process Job with timeout,
cancellation, memory, stderr-flood and cleanup tests. The default production engine remains blocked
until that evidence and a separate explicit selection decision exist.
