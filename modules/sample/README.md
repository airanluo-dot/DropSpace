# Official optional sample

`dropspace.sample` version `1.0.0` is the minimal harmless module used to verify the optional
feature lifecycle. It is separate from the App, never installed by default, and uses the
canonical source/manifest in `../templates/worker`. It displays a message, optionally submits
30-second island content, and acknowledges one boolean setting. It writes no user files and
does not move or change any built-in feature.

Build with `pwsh -File modules/sample/Build-Package.ps1`. The result under `artifacts` includes
the self-contained worker ZIP, root `manifest.json` and real package/worker SHA-256 metadata.
This build does not execute the sample or install it. Publication and official source/catalog
registration belong to the App release integration; a local ZIP is not a trusted catalog entry
by itself.

The executable example and its limits are documented in
[`../templates/worker/README.md`](../templates/worker/README.md). In particular: protocol/UI/
host interface 1, Windows x64, official packages only, bounded request/session isolation,
declarative plain-text UI, no general data migrations, and no OS permission sandbox.
