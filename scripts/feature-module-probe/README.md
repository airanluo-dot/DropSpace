# Optional feature-module evidence probe

This standalone .NET 10 console links the current `FeatureModuleRuntime`, package store,
official catalog, worker client/job and private download storage helper directly from their
production files. It references the actual Core/Infrastructure downloader and path policies.
`CS0436` is suppressed only in this probe because those linked types intentionally supersede
their referenced-assembly counterparts. No App project, production allowlist or UI is injected
or changed. Build alone is a non-functional production/source compilation check.

There are no default functional runs. The release owner must reserve each actual invocation in
the one project-wide 20-scenario ledger **before** launching the executable. Failures and
retries count. Run via the built apphost (`bin/Release/net10.0/DropSpace.FeatureModuleProbe.exe`),
not `dotnet run`, because the pathological fixture must restart this apphost as a child process.
Use a fresh `--root` and `--evidence` below `scripts/feature-module-probe/artifacts`; evidence is
written even on a failed scenario and never overwritten. Preserve generated evidence until
the release owner records it. This tool never deletes previous runs or launches full suites.

Build with `dotnet build scripts/feature-module-probe/FeatureModuleProbe.csproj -c Release`.
Each invocation requires `--case` plus the fresh root/evidence arguments:

1. No installed modules: read-only initialization, no storage directory, unchanged worker count.
2. Actual sample install/handshake/action/settings/disable/reenable/uninstall, repeated lifecycle
   calls and retained scoped data. Without `--package`, this uses the real anonymous downloader
   and the production embedded official package identity/source/hash. With `--package`, it is
   an independently catalog-verified local cache run; it does not claim a fresh public download.
3. Journal interruption recovery followed by real locked-file cleanup, truthful PendingCleanup,
   another startup and preserved scoped data. This deliberately edits only a fresh probe state.
4. Actual pathological subprocess/session isolation. Select exactly one `--failure-mode` of
   `cancel`, `crash` or `timeout`. The fixture deliberately crashes or ignores cancellation and
   replies after 15 seconds. Each separate mode is a separate counted scenario; this code is
   neither the public sample nor a normal user panel.
5. Trusted-catalog rejection preserves a prior actually running compatible version. Select
   exactly one `--rejection-mode`: `traversal`, `hash`, `required-capability` or
   `incompatible-update`. Each mode is a separate counted scenario. This case requires a
   separately built probe-only fixture catalog and `--candidate` ZIP; no production trust
   injection exists.

Cases 1/2/3 plus every mode of 4/5 total **10 actual scenario invocations**, not five. Execute
only the release owner's chosen subset; this README does not authorize spending that budget.
Native WinUI layout/navigation/island priority, actual installer upgrades, existing lyrics/model
behavior and public-release publication remain separate verification boundaries.

For case 5, `Prepare-Fixture.ps1 -SampleZip <actual sample ZIP> -Mode <explicit mode>` creates a
malformed ZIP and honest archive hash under a fresh fixture directory; it never executes a
worker. The candidate-only catalog avoids duplicate IDs; the old version uses a separate
descriptor persisted by the actual package store. Build with
`-p:ProbeCatalogFile=<absolute fixture-catalog.json>` into a separate output
directory; the probe explicitly labels fixture provenance. The synthetic official URL is not
downloaded and is not evidence of publisher identity or public availability. Pass
`--previous-descriptor <previous-package-descriptor.json>`, `--package <actual sample ZIP>` and
`--candidate <generated candidate ZIP>`. Rebuild
with the production catalog before any public-download case. Record the exact source commit,
assembly/catalog/ZIP hashes and HTTP evidence independently; do not reuse fixture evidence as
actual publisher/download results.
