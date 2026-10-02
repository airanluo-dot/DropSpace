# Private CT2 runtime candidate QA

This is an **unused, nonshipping CT2 candidate**. It does not change App registration, package
installation, existing model selection or any release. It includes no translation model. A pass
means this particular candidate passed its bounded Windows smoke checks, not that CT2 or a model
is production-ready. CT2 redistribution gaps affect this candidate only, not unrelated engines.

## Inputs and trust

`dependencies.json` pins the exact eleven-distribution closure accepted by the hardened
`tools/ct2-helper/build.ps1`. Every selected Windows CPython 3.12 x64 wheel was downloaded from
its official PyPI `files.pythonhosted.org` URL and independently checked against the PyPI byte
length/SHA-256 on 2026-10-02. Runtime versions match the model QA engine family: CT2 4.8.2,
SentencePiece 0.2.2, NumPy 2.4.6, PyYAML 6.0.3. The model QA conversion lock currently uses
SentencePiece 0.2.1, so exact tokenizer parity across that version difference is still unproven.
Build tools are also exact pins, without resolving optional extras. No Argos, MiniSBD, Flask,
Torch, Transformers, network client or extra distribution is installed into these environments.
Vendored components inside allowed wheels still exist and are included in license evidence.

`provenance.json` retains official URLs, metadata requirements, wheel license hashes and pinned
upstream CT2/SentencePiece licenses (those two wheels omit license files). The CT2 Windows wheel
contains GPU-related `cudnn64_9.dll` and Intel `libiomp5md.dll`, even though inference uses CPU.
The top-level project license does not establish those libraries' redistribution rights. Their
matching native-component notices, collected VC runtime obligations, and final package notice
completeness remain a **CT2 binary redistribution gate**. Private artifacts are not shipping approval.

The builder is pinned CPython 3.12.10, the final upstream 3.12 Windows binary-installer release;
it is a private build prerequisite, not a user installation or independent product service.
Its complete official installer hash is fixed and Windows Authenticode must be valid for the
Python Software Foundation. Installation is per-user into the job-private directory, without
PATH, launcher, shortcuts or associations. Its executable hash and PSF license are retained.
This is stronger provenance than treating a freshly computed `python.exe` hash alone as trust.
It does not claim CPython 3.12.10 contains every later 3.12 security-only source patch; updating the
frozen Python runtime and checking advisories is also required before any shipping decision.

## Windows-only execution

The workflow runs only on `qa/ct2-runtime-031`, with `contents: read`, no persisted Git credentials,
no secrets and no release/deployment step. Outputs remain Actions artifacts for 14 days.
It downloads reviewed inputs first; the hardened build and inspector installs are then strictly
local/offline (`--no-index --no-deps --require-hashes`). No global package install, service,
security-policy change or networking setting is made. The script invokes the existing reviewed
builder unchanged except separately reviewed validator fixes, and keeps its `--onedir` output
whole. No one-file launcher or increased process Job limit is used.

```powershell
./scripts/ct2-runtime-qa/Get-VerifiedInputs.ps1 -Output C:\qa\new-inputs
./scripts/ct2-runtime-qa/Run-WindowsCt2RuntimeQa.ps1 -Inputs C:\qa\new-inputs `
  -Work C:\qa\new-build -Output C:\qa\new-evidence
```

The native harness compiles the actual `WindowsInferenceProcess` and `LocalInferenceProcess`
source files. Every frozen-helper launch therefore uses the mandatory one-process Job, 3 GiB
cap, `CREATE_NO_WINDOW`, restricted handle inheritance and confirmed OS-exit cleanup.
It checks empty EOF, invalid JSON/schema/UTF-8, CRLF and extra-frame rejection, a genuine non-ASCII
UTF-8 request, and timeout/cancellation of a blocked read. It samples the target's main-window
handle and loaded native modules. A synthetic SentencePiece tokenizer reaches the native imports,
then intentionally rejects an absent CT2 model. The native import test requires CT2 and
SentencePiece DLLs to be observed in the **actual frozen helper**; module sampling can fail a very
fast run, which should be inspected rather than reinterpreted as success.

The separate verified-wheel import probe checks CPU INT8 availability and produces PE import
lists for every executable/DLL/PYD in the frozen bundle. This reports imports and runner-specific
availability; it is not a clean-Windows-machine guarantee, GPU test, translated-output test,
model tokenization comparison, App UX acceptance, or network sandbox. No network restriction is
claimed for the Job object. No model quality is claimed for any successful smoke run.

Evidence includes complete frozen-engine hashes, licenses/notices found inside all wheels,
source provenance, closure details, Python signature/identity, static PE imports, native module
observations, raw stdout/stderr, cleanup outcomes, and a complete output hash inventory.
Read `status.json` and `native-smoke.json`; artifact existence alone does not imply success.
If native import/package collection fails, keep the failure evidence and review the fix rather
than adding unreviewed packages or weakening containment.

## Local non-native checks

```text
python -m unittest discover -s scripts/ct2-runtime-qa -p "test_*.py" -v
python -m unittest discover -s tools/ct2-helper -p "test_*.py" -v
dotnet build scripts/ct2-runtime-qa/Ct2RuntimeQa.csproj -c Release
```

Linux compile/schema tests do not run Windows native containment. Publish or run the dedicated
QA branch only with the parent task's approval; this directory itself performs no push or commit.
