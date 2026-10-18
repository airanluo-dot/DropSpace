# Internal optional-module fixture

`dropspace.sample` version `1.0.0` is the minimal harmless module used to verify the optional
feature lifecycle. It is separate from the App, never installed by default, and uses the
canonical source/manifest in `../templates/worker`. It displays a message, optionally submits
30-second island content, and acknowledges one boolean setting. It writes no user files and
does not move or change any built-in feature.

Build with `pwsh -File modules/sample/Build-Package.ps1`. The result under `artifacts` includes
the self-contained worker ZIP, root `manifest.json` and real package/worker SHA-256 metadata.
This build does not execute or install the sample. This ID is internal-only: do not publish
its ZIP or register it in either user catalog. The registration script rejects it.
`fixture-catalog.json` preserves the historical package descriptor solely for local probes;
its historical URL is not a supported public download. Supply the matching local ZIP to
probes with `--package`, or an explicit `ProbeCatalogFile` for newly built fixture bytes.

The executable example and its limits are documented in
[`../templates/worker/README.md`](../templates/worker/README.md). In particular: protocol/UI/
host interface 1, Windows x64, official packages only, bounded request/session isolation,
declarative plain-text UI, no general data migrations, and no OS permission sandbox.
